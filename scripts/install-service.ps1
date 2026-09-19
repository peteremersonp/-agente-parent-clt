param(
    [ValidateSet("Release","Debug")]
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"

$repo = Split-Path -Path $PSScriptRoot -Parent
$exe = Join-Path $repo "src\ParentCLT.Agent\bin\$Configuration\net10.0\ParentCLT.Agent.exe"
$name = "ParentCLT Agent"

if (-not (Test-Path -LiteralPath $exe)) {
    throw "No se encontro el ejecutable: $exe`nCompilalo primero: dotnet publish src\ParentCLT.Agent -c $Configuration"
}

# Detener y eliminar si existe (idempotente)
sc.exe stop $name 2>$null | Out-Null
Start-Sleep -Milliseconds 800
sc.exe delete $name 2>$null | Out-Null
Start-Sleep -Milliseconds 800

New-Service -Name $name `
    -BinaryPathName "`"$exe`"" `
    -DisplayName "ParentCLT Agent" `
    -Description "Control parental: mini DNS local (127.0.0.1:53), lista negra y sincronizacion de politica." `
    -StartupType Automatic | Out-Null

# Arranque retrasado + reinicio automatico ante fallos (anti-tamper basico)
sc.exe config $name start= delayed-auto | Out-Null
sc.exe failure $name reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
sc.exe start $name | Out-Null

Write-Host "Servicio '$name' instalado y arrancado."
Write-Host "Ejecutable: $exe"
Write-Host "Logs: Event Viewer > Aplicacion (origen: ParentCLT)"