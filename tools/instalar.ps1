#Requires -Version 5.1
<#
.SYNOPSIS
    Instala Open Control Edge en Archivos de programa y deja la tarea de inicio apuntando ahi.
.DESCRIPTION
    Por que en "Archivos de programa": la tarea programada arranca el widget como administrador
    sin pedir UAC. Si el .exe vive en una carpeta donde tu usuario puede escribir, cualquier
    programa que se ejecute con tu cuenta puede sustituirlo y heredar esos permisos en el
    siguiente inicio de sesion. En Archivos de programa solo un administrador puede escribir.

    Pasos: para el widget -> copia el .exe -> reapunta la tarea -> migra los ajustes a
    %LOCALAPPDATA%\OpenControlEdge -> lo vuelve a arrancar.

    Conserva la carpeta antigua C:\Program Files\EdgeWidget y los ajustes originales.
.PARAMETER Origen
    .exe a instalar. Por defecto, dist\OpenControlEdge.exe del propio proyecto.
.PARAMETER Desinstalar
    Quita la tarea programada y deja de arrancar el widget (no borra el .exe ni los ajustes).
.PARAMETER Si
    No preguntar. Pensado para reinstalar sin interaccion.
#>
[CmdletBinding()]
param(
    [string]$Origen = $(if (Test-Path (Join-Path $PSScriptRoot 'OpenControlEdge.exe')) { Join-Path $PSScriptRoot 'OpenControlEdge.exe' } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\OpenControlEdge.exe' }),
    [switch]$Desinstalar,
    [switch]$Si,
    [switch]$Pausa
)

$ErrorActionPreference = 'Stop'
trap {
    Escribe "Error durante la instalacion: $_" 'Red'
    Fin
    break
}
$nombreTarea = 'OpenControlEdge'
$destinoDir  = Join-Path $env:ProgramFiles 'OpenControlEdge'
$destinoExe  = Join-Path $destinoDir 'OpenControlEdge.exe'
$datosDir    = Join-Path $env:LOCALAPPDATA 'OpenControlEdge'

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
    Get-Process OpenControlEdge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Escribe 'Listo.' 'Green'
    Fin
    return
}

# ---------------------------------------------------------------- comprobaciones
if (-not (Test-Path $Origen)) { Escribe "No encuentro el .exe de origen: $Origen" 'Red'; Fin; return }
$ver = (Get-Item $Origen).VersionInfo.FileVersion
$usuarioActual = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$usuarioInteractivo = (Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue).UserName
if ($usuarioInteractivo -and $usuarioInteractivo -ne $usuarioActual) {
    throw "La instalacion debe elevarse con la misma cuenta de la sesion interactiva ($usuarioInteractivo), no con $usuarioActual."
}

# LibreHardwareMonitor uses PawnIO for CPU sensor access. The driver is never installed automatically.
$pawnIoInstalled = $false
try {
    $pawnCheck = Start-Process -FilePath $Origen -ArgumentList '--check-pawnio' -Wait -PassThru -WindowStyle Hidden
    $pawnIoInstalled = $pawnCheck.ExitCode -eq 0
} catch { }
if (-not $pawnIoInstalled) {
    Escribe 'AVISO: PawnIO no esta instalado; la temperatura de CPU no estara disponible.' 'Yellow'
    Escribe 'PawnIO es un controlador firmado necesario para acceder a los sensores de hardware.' 'Yellow'
    $installPawn = -not $Si -and ((Read-Host '¿Instalar PawnIO con winget ahora? (s/N)') -match '^[sS]')
    if ($installPawn) {
        $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
        if ($winget) {
            & $winget.Source install --id namazso.PawnIO -e
            if ($LASTEXITCODE -ne 0) { Escribe 'No se pudo completar winget. Instala PawnIO desde https://pawnio.eu' 'Red' }
        }
        else { Escribe 'winget no esta disponible. Instala PawnIO desde https://pawnio.eu' 'Red' }
    } else { Escribe 'Instalalo desde https://pawnio.eu cuando quieras activar la temperatura de CPU.' 'Yellow' }
}

Escribe ''
Escribe '  Instalar Open Control Edge' 'Cyan'
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
Get-Process OpenControlEdge -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process EdgeWidget -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$vueltas = 0
while ((Get-Process OpenControlEdge -ErrorAction SilentlyContinue) -and $vueltas -lt 20) {
    Start-Sleep -Milliseconds 250
    $vueltas++
}
Escribe '     parado.' 'Green'
if (Test-Path (Join-Path $env:ProgramFiles 'EdgeWidget')) {
    Escribe "     AVISO: se conserva la carpeta antigua $(Join-Path $env:ProgramFiles 'EdgeWidget')." 'Yellow'
}

# ---------------------------------------------------------------- copiar
Escribe '2/5  Copiando los archivos de la aplicacion...'
if (-not (Test-Path $destinoDir)) { New-Item -ItemType Directory -Path $destinoDir -Force | Out-Null }
$stagingDir = "$destinoDir.new"
if (Test-Path $stagingDir) { Remove-Item -LiteralPath $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
$copiado = $false
for ($intento = 1; $intento -le 5 -and -not $copiado; $intento++) {
    try {
        Get-ChildItem -LiteralPath (Split-Path $Origen -Parent) -Force | Where-Object Name -ne 'instalar.ps1' | Copy-Item -Destination $stagingDir -Recurse -Force -ErrorAction Stop
        $copiado = $true
    } catch { if ($intento -eq 5) { throw }; Start-Sleep -Seconds 1 }
}
$stagingExe = Join-Path $stagingDir 'OpenControlEdge.exe'
if ((Get-FileHash -LiteralPath $Origen -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $stagingExe -Algorithm SHA256).Hash) {
    throw 'La comprobacion SHA256 del ejecutable copiado ha fallado.'
}
 $backupDir = "$destinoDir.previous-$([guid]::NewGuid().ToString('N'))"
if (Test-Path $destinoDir) { Move-Item -LiteralPath $destinoDir -Destination $backupDir }
try { Move-Item -LiteralPath $stagingDir -Destination $destinoDir }
catch {
    if (Test-Path $backupDir) { Move-Item -LiteralPath $backupDir -Destination $destinoDir }
    throw
}
if (Test-Path $backupDir) { Remove-Item -LiteralPath $backupDir -Recurse -Force }
Escribe "     $destinoExe" 'Green'

# Comprobar escritura de grupos no privilegiados por SID tanto en el directorio como en el ejecutable.
$sidNoAdmin = @('S-1-5-32-545', 'S-1-1-0', 'S-1-5-11')
function Test-EscrituraNoAdmin($Ruta, [switch]$Directorio) {
    $acl = Get-Acl -LiteralPath $Ruta
    foreach ($regla in $acl.Access) {
        if ($regla.AccessControlType -ne 'Allow') { continue }
        $sid = $regla.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        if ($sid -notin $sidNoAdmin) { continue }
        $rights = [int]$regla.FileSystemRights
        $writeMask = [int][Security.AccessControl.FileSystemRights]::WriteData -bor
            [int][Security.AccessControl.FileSystemRights]::AppendData -bor
            [int][Security.AccessControl.FileSystemRights]::WriteAttributes -bor
            [int][Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
            [int][Security.AccessControl.FileSystemRights]::Modify -bor
            [int][Security.AccessControl.FileSystemRights]::FullControl
        if ($Directorio) { $writeMask = $writeMask -bor [int][Security.AccessControl.FileSystemRights]::CreateFiles -bor [int][Security.AccessControl.FileSystemRights]::CreateDirectories }
        if (($rights -band $writeMask) -ne 0) { return $true }
    }
    return $false
}
$escribenUsuarios = (Test-EscrituraNoAdmin $destinoDir -Directorio) -or (Test-EscrituraNoAdmin $destinoExe)
if ($escribenUsuarios) {
    Escribe '     AVISO: usuarios sin privilegios pueden escribir en esa carpeta.' 'Yellow'
} else {
    Escribe '     permisos correctos: solo administradores pueden escribir ahi.' 'Green'
}

# ---------------------------------------------------------------- ajustes
Escribe '3/5  Migrando los ajustes...'
if (-not (Test-Path $datosDir)) { New-Item -ItemType Directory -Path $datosDir -Force | Out-Null }
$dataItem = Get-Item -LiteralPath $datosDir -Force
if ($dataItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'La carpeta de datos es un enlace o punto de reanalisis; se cancela para evitar escrituras elevadas fuera de ella.' }
$dataLinks = Get-ChildItem -LiteralPath $datosDir -Force -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }
if ($dataLinks) { throw 'La carpeta de datos contiene enlaces o puntos de reanalisis; se cancela para evitar escrituras elevadas fuera de ella.' }
& icacls.exe $datosDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' /setowner '*S-1-5-32-544' /T | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'No se pudo restringir la escritura de la carpeta de datos a administradores.' }
& icacls.exe $datosDir /setintegritylevel '(OI)(CI)H' /T | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'No se pudo proteger la carpeta de datos con nivel de integridad alto.' }
$ajustesNuevo = Join-Path $datosDir 'OpenControlEdge.settings.json'
# Junto al .exe de origen, y tambien en dist\ del proyecto: al instalar desde una carpeta de
# compilacion recien creada, los ajustes de verdad estan en dist.
$candidatos = @(
    (Join-Path (Join-Path $env:LOCALAPPDATA 'EdgeWidget') 'EdgeWidget.settings.json'),
    (Join-Path (Split-Path $Origen -Parent) 'OpenControlEdge.settings.json'),
    (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\OpenControlEdge.settings.json'),
    (Join-Path $destinoDir 'OpenControlEdge.settings.json')
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
$tareaAntigua = Get-ScheduledTask -TaskName 'EdgeWidget' -ErrorAction SilentlyContinue
if ($tareaAntigua) {
    try { Stop-ScheduledTask -TaskName 'EdgeWidget' -ErrorAction SilentlyContinue } catch { }
    try { Disable-ScheduledTask -TaskName 'EdgeWidget' -ErrorAction SilentlyContinue | Out-Null } catch { }
    Unregister-ScheduledTask -TaskName 'EdgeWidget' -Confirm:$false
    Escribe "     tarea antigua 'EdgeWidget' desactivada y eliminada." 'Green'
}

# ---------------------------------------------------------------- arrancar
Escribe '5/5  Arrancando el widget...'
Start-ScheduledTask -TaskName $nombreTarea
Start-Sleep -Seconds 4
$p = Get-Process OpenControlEdge -ErrorAction SilentlyContinue
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
