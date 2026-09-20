#Requires -Version 5.1
<#
.SYNOPSIS
    Mantiene viva la sesion de Claude Code para no tener que iniciarla a mano.
.DESCRIPTION
    El anillo de Claude del widget lee %USERPROFILE%\.claude\.credentials.json. Ese archivo
    guarda un token de acceso que caduca a las pocas horas y un token de refresco que dura
    semanas. Cuando el de acceso caduca, el widget no puede consultar el uso y muestra
    "Abre Claude Code para renovar".

    Lo que NO renueva el token (comprobado en esta maquina):
      - abrir la aplicacion de escritorio de Claude (guarda su sesion en otro sitio),
      - 'claude auth status' (solo informa; no toca el archivo).
    Lo unico que lo renueva es una llamada de verdad a la API: la CLI detecta que el token
    caduco, lo refresca con el token de refresco y reescribe el archivo. Eso es lo que hace
    este script, con la peticion mas pequena posible y solo cuando hace falta.

    Nunca imprime ni guarda ningun token: solo lee la fecha de caducidad.

.PARAMETER Instalar
    Crea la tarea programada: al iniciar sesion (3 minutos despues, para no cargar el
    arranque) y luego cada 4 horas. No necesita permisos de administrador.
.PARAMETER Desinstalar
    Quita esa tarea programada.
.PARAMETER Forzar
    Renueva aunque al token le quede tiempo de sobra.
.PARAMETER AbrirApp
    Abre tambien la aplicacion de escritorio de Claude si no esta ya abierta.
.PARAMETER Margen
    Horas de margen: si al token le queda menos que esto, se renueva. Por defecto 2.
#>
[CmdletBinding()]
param(
    [switch]$Instalar,
    [switch]$Desinstalar,
    [switch]$Forzar,
    [switch]$AbrirApp,
    [double]$Margen = 2,
    [switch]$Pausa
)

$nombreTarea = 'Claude - Mantener sesion'
$credenciales = Join-Path $env:USERPROFILE '.claude\.credentials.json'
$cli = Join-Path $env:APPDATA 'npm\claude.cmd'
$registro = Join-Path $PSScriptRoot 'claude-sesion.log'

function Apunta([string]$texto) {
    $linea = "{0} {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $texto
    Write-Host $linea
    try {
        Add-Content -Path $registro -Value $linea -Encoding UTF8 -ErrorAction Stop
        # Recorta el registro para que no crezca sin fin.
        $todo = @(Get-Content $registro -ErrorAction Stop)
        if ($todo.Count -gt 500) { $todo[-300..-1] | Set-Content $registro -Encoding UTF8 }
    } catch { }
}

function Fin { if ($Pausa) { [void](Read-Host "`nPulsa Enter para cerrar") } }

# Devuelve la fecha de caducidad del token de acceso, o $null. No lee ningun token.
function Get-Caducidad {
    if (-not (Test-Path $credenciales)) { return $null }
    try {
        $j = Get-Content $credenciales -Raw -ErrorAction Stop | ConvertFrom-Json
        $ms = $j.claudeAiOauth.expiresAt
        if (-not $ms) { return $null }
        return [DateTimeOffset]::FromUnixTimeMilliseconds([int64]$ms).LocalDateTime
    } catch {
        return $null
    }
}

# ---------------------------------------------------------------- instalar
if ($Instalar) {
    $argumentos = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$PSCommandPath`""
    if ($AbrirApp) { $argumentos += ' -AbrirApp' }

    $accion = New-ScheduledTaskAction -Execute "$PSHOME\powershell.exe" -Argument $argumentos

    # Al iniciar sesion, 3 minutos despues: el arranque de Windows ya es el momento mas
    # caliente del dia en este portatil y no conviene anadirle nada.
    $alIniciar = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
    $alIniciar.Delay = 'PT3M'

    # Y cada 4 horas, porque el token dura unas 8.
    $cada4h = New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddMinutes(5) `
        -RepetitionInterval (New-TimeSpan -Hours 4)

    $ppal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
        -LogonType Interactive -RunLevel Limited
    $ajustes = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew -StartWhenAvailable

    Register-ScheduledTask -TaskName $nombreTarea -Action $accion -Trigger $alIniciar, $cada4h `
        -Principal $ppal -Settings $ajustes -Force | Out-Null

    Apunta "tarea '$nombreTarea' instalada (al iniciar sesion +3 min, y cada 4 h)"

    # La tarea antigua 'ClaudeSession' hacia 'claude auth status', que no renueva nada.
    # Se deja desactivada, no se borra.
    $vieja = Get-ScheduledTask -TaskName 'ClaudeSession' -ErrorAction SilentlyContinue
    if ($vieja -and $vieja.State -ne 'Disabled') {
        Disable-ScheduledTask -TaskName 'ClaudeSession' | Out-Null
        Apunta "tarea antigua 'ClaudeSession' desactivada (no renovaba el token). No se ha borrado."
    }
    Fin
    return
}

# ---------------------------------------------------------------- desinstalar
if ($Desinstalar) {
    if (Get-ScheduledTask -TaskName $nombreTarea -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $nombreTarea -Confirm:$false
        Apunta "tarea '$nombreTarea' eliminada"
    } else {
        Apunta 'no habia tarea que quitar'
    }
    Fin
    return
}

# ---------------------------------------------------------------- comprobar y renovar
if (-not (Test-Path $cli)) {
    Apunta "ERROR: no encuentro la CLI de Claude Code en $cli"
    Apunta '       instalala con: npm install -g @anthropic-ai/claude-code'
    Fin
    return
}

$caduca = Get-Caducidad
if ($null -eq $caduca) {
    Apunta 'no hay credenciales legibles: hay que iniciar sesion una vez a mano (claude auth login)'
    Fin
    return
}

$quedan = ($caduca - (Get-Date)).TotalHours
$estado = if ($quedan -le 0) { 'CADUCADO' } else { "quedan {0:N1} h" -f $quedan }
Apunta ("token de acceso: caduca {0:dd/MM/yyyy HH:mm} ({1})" -f $caduca, $estado)

if (-not $Forzar -and $quedan -gt $Margen) {
    Apunta "no hace falta renovar (margen de $Margen h)"
} else {
    Apunta 'renovando con una peticion minima...'
    try {
        # --tools "" no deja ninguna herramienta disponible y --no-session-persistence no
        # guarda la conversacion: es la llamada mas pequena que obliga a refrescar el token.
        $salida = & cmd.exe /c "`"$cli`" -p --tools `"`" --no-session-persistence --model haiku ok" 2>&1
        $codigo = $LASTEXITCODE
        if ($codigo -ne 0) { Apunta "la CLI ha devuelto el codigo $codigo" }
    } catch {
        Apunta "ERROR al llamar a la CLI: $($_.Exception.Message)"
    }

    $nueva = Get-Caducidad
    if ($nueva -and $nueva -gt $caduca) {
        Apunta ("renovado: ahora caduca el {0:dd/MM/yyyy HH:mm}" -f $nueva)
    } else {
        Apunta 'NO se ha renovado. Abre una terminal y ejecuta: claude auth login'
    }
}

# ---------------------------------------------------------------- abrir la aplicacion
if ($AbrirApp) {
    if (Get-Process claude -ErrorAction SilentlyContinue) {
        Apunta 'la aplicacion de Claude ya estaba abierta'
    } else {
        try {
            Start-Process 'explorer.exe' 'shell:AppsFolder\Claude_pzs8sxrjxfjjc!Claude' -ErrorAction Stop
            Apunta 'aplicacion de Claude abierta'
        } catch {
            Apunta "no se ha podido abrir la aplicacion: $($_.Exception.Message)"
        }
    }
}

Fin
