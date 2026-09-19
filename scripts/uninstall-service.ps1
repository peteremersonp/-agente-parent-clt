$ErrorActionPreference = "Stop"
$name = "ParentCLT Agent"

sc.exe stop $name 2>$null | Out-Null
Start-Sleep -Milliseconds 800
sc.exe delete $name 2>$null | Out-Null

Write-Host "Servicio '$name' eliminado."