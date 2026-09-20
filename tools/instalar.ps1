#Requires -Version 5.1
<#
.SYNOPSIS
    Instala EdgeWidget en Archivos de programa y deja la tarea de inicio apuntando ahi.
.DESCRIPTION
    Por que en "Archivos de programa": la tarea programada arranca el widget como administrador
    sin pedir UAC. Si el .exe vive en una carpeta donde tu usuario puede escribir, cualquier
    programa que se ejecute con tu cuenta puede sustituirlo y heredar esos permisos en el
    siguiente inicio de sesion. En Archivos de programa solo un administrador puede escribir.

    Pasos: para el widget -> copia el .exe -> reapunta la tarea -> migra los ajustes a
    %LOCALAPPDATA%\EdgeWidget -> lo vuelve a arrancar.

    No borra nada. Los ajustes y el registro anteriores se conservan.
.PARAMETER Origen
    .exe a instalar. Por defecto, dist\EdgeWidget.exe del propio proyecto.
.PARAMETER Desinstalar
    Quita la tarea programada y deja de arrancar el widget (no borra el .exe ni los ajustes).
.PARAMETER Si
    No preguntar. Pensado para reinstalar sin interaccion.
#>
[CmdletBinding()]
param(
    [string]$Origen = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\EdgeWidget.exe'),
    [switch]$Desinstalar,
    [switch]$Si,
    [switch]$Pausa
)

$ErrorActionPreference = 'Stop'
$nombreTarea = 'EdgeWidget'
$destinoDir  = Join-Path $env:ProgramFiles 'EdgeWidget'
$destinoExe  = Join-Path $destinoDir 'EdgeWidget.exe'
$datosDir    = Join-Path $env:LOCALAPPDATA 'EdgeWidget'

function Escribe([string]$t, [string]$c = 'Gray') { Write-Host $t -ForegroundColor $c }
function Fin { if ($Pausa) { [void](Read-Host "`nPulsa Enter para cerrar") } }

function Test-EsAdmin {
    $p = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-EsAdmin)) {
    Escribe 'Hace falta administrador: se abrira una ventana pidiendo permiso.' 'Yellow'
    $a = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Origen `"$Origen`" -Pausa"
    if ($Desinstalar) { $a += ' -Desinstalar' }
    if ($Si)          { $a += ' -Si' }
    try { Start-Process powershell.exe -ArgumentList $a -Verb RunAs }
    catch { Escribe 'Has cancelado el permiso de administrador. No se ha cambiado nada.' 'Red' }
    return
}

# ---------------------------------------------------------------- desinstalar
if ($Desinstalar) {
    Escribe "Se va a quitar la tarea programada '$nombreTarea'." 'Yellow'
    Escribe "El .exe de $destinoDir y tus ajustes NO se borran."
    if (-not $Si) {
        if ((Read-Host 'Continuar? (s/N)') -notmatch '^[sS]') { Escribe 'Cancelado.'; Fin; return }
    }
    if (Get-ScheduledTask -TaskName $nombreTarea -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $nombreTarea -Confirm:$false
        Escribe "Tarea '$nombreTarea' eliminada." 'Green'
    } else {
        Escribe 'No habia tarea que quitar.'
    }
    Get-Process EdgeWidget -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Escribe 'Listo.' 'Green'
    Fin
    return
}

# ---------------------------------------------------------------- comprobaciones
if (-not (Test-Path $Origen)) { Escribe "No encuentro el .exe de origen: $Origen" 'Red'; Fin; return }
$ver = (Get-Item $Origen).VersionInfo.FileVersion

Escribe ''
Escribe '  Instalar EdgeWidget' 'Cyan'
Escribe '  -------------------'
Escribe "  Origen : $Origen  (version $ver)"
Escribe "  Destino: $destinoExe"
Escribe "  Ajustes: $datosDir"
Escribe "  Tarea  : '$nombreTarea', al iniciar sesion, como administrador"
Escribe ''
if (-not $Si) {
    if ((Read-Host 'Continuar? (s/N)') -notmatch '^[sS]') { Escribe 'Cancelado.'; Fin; return }
}

# ---------------------------------------------------------------- parar
Escribe ''
Escribe '1/5  Parando el widget...'
if (Get-ScheduledTask -TaskName $nombreTarea -ErrorAction SilentlyContinue) {
    try { Stop-ScheduledTask -TaskName $nombreTarea -ErrorAction Stop } catch { }
}
Get-Process EdgeWidget -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$vueltas = 0
while ((Get-Process EdgeWidget -ErrorAction SilentlyContinue) -and $vueltas -lt 20) {
    Start-Sleep -Milliseconds 250
    $vueltas++
}
Escribe '     parado.' 'Green'

# ---------------------------------------------------------------- copiar
Escribe '2/5  Copiando el ejecutable...'
if (-not (Test-Path $destinoDir)) { New-Item -ItemType Directory -Path $destinoDir -Force | Out-Null }
Copy-Item $Origen $destinoExe -Force
Escribe "     $destinoExe" 'Green'

# La carpeta hereda los permisos de Archivos de programa: solo administradores escriben. Se comprueba.
$acl = Get-Acl $destinoDir
$escribenUsuarios = $acl.Access | Where-Object {
    $_.AccessControlType -eq 'Allow' -and
    "$($_.FileSystemRights)" -match 'Write|Modify|FullControl' -and
    "$($_.IdentityReference)" -match 'Users|Usuarios|Everyone|Todos|Authenticated'
}
if ($escribenUsuarios) {
    Escribe '     AVISO: usuarios sin privilegios pueden escribir en esa carpeta.' 'Yellow'
} else {
    Escribe '     permisos correctos: solo administradores pueden escribir ahi.' 'Green'
}

# ---------------------------------------------------------------- ajustes
Escribe '3/5  Migrando los ajustes...'
if (-not (Test-Path $datosDir)) { New-Item -ItemType Directory -Path $datosDir -Force | Out-Null }
$ajustesNuevo = Join-Path $datosDir 'EdgeWidget.settings.json'
# Junto al .exe de origen, y tambien en dist\ del proyecto: al instalar desde una carpeta de
# compilacion recien creada, los ajustes de verdad estan en dist.
$candidatos = @(
    (Join-Path (Split-Path $Origen -Parent) 'EdgeWidget.settings.json'),
    (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\EdgeWidget.settings.json'),
    (Join-Path $destinoDir 'EdgeWidget.settings.json')
)
$ajustesViejo = $candidatos | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($ajustesViejo -and -not (Test-Path $ajustesNuevo)) {
    Copy-Item $ajustesViejo $ajustesNuevo -Force
    Escribe "     copiados desde $ajustesViejo" 'Green'
} elseif (Test-Path $ajustesNuevo) {
    Escribe '     ya estaban migrados.' 'Green'
} else {
    Escribe '     no habia ajustes previos; se usaran los de fabrica.'
}

# El registro anterior quedo como vinculo duro compartido con el contenedor de la app Claude y
# las escrituras del widget no llegaban. Se aparta para empezar un archivo limpio.
$logActual = Join-Path $datosDir 'widget.log'
if (Test-Path $logActual) {
    $enlaces = @(fsutil hardlink list $logActual 2>$null)
    if ($enlaces.Count -gt 1) {
        Move-Item $logActual (Join-Path $datosDir 'widget.anterior.log') -Force
        Escribe '     registro anterior apartado como widget.anterior.log (estaba enlazado).' 'Green'
    }
}

# ---------------------------------------------------------------- tarea
Escribe '4/5  Reapuntando la tarea de inicio...'
$usuario = "$env:USERDOMAIN\$env:USERNAME"
$accion  = New-ScheduledTaskAction -Execute $destinoExe
$disparo = New-ScheduledTaskTrigger -AtLogOn -User $usuario
$ppal    = New-ScheduledTaskPrincipal -UserId $usuario -LogonType Interactive -RunLevel Highest
$ajustes = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable
Register-ScheduledTask -TaskName $nombreTarea -Action $accion -Trigger $disparo -Principal $ppal `
    -Settings $ajustes -Force | Out-Null
Escribe "     '$nombreTarea' -> $destinoExe" 'Green'

# ---------------------------------------------------------------- arrancar
Escribe '5/5  Arrancando el widget...'
Start-ScheduledTask -TaskName $nombreTarea
Start-Sleep -Seconds 4
$p = Get-Process EdgeWidget -ErrorAction SilentlyContinue
if ($p) {
    Escribe "     en marcha (PID $($p.Id), $([math]::Round($p.WorkingSet64/1MB,1)) MB)." 'Green'
} else {
    Escribe '     NO ha arrancado. Mira el registro en:' 'Red'
    Escribe "     $logActual" 'Red'
}

Escribe ''
Escribe 'Instalacion terminada.' 'Cyan'
Escribe "El codigo fuente sigue en $(Split-Path $PSScriptRoot -Parent)."
Escribe 'Para quitar el arranque automatico: instalar.ps1 -Desinstalar'
Fin
