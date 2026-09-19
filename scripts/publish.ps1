param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "artifacts\publish"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Path $PSScriptRoot -Parent

Write-Host "Publicando agente ($Configuration, $Runtime, self-contained single-file)..." -ForegroundColor Cyan
& dotnet publish "$repo\src\ParentCLT.Agent" `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o "$repo\$OutputDir"

if ($LASTEXITCODE -ne 0) { throw "dotnet publish fallo (codigo $LASTEXITCODE)" }

$exe = Join-Path $repo "$OutputDir\ParentCLT.Agent.exe"
if (-not (Test-Path -LiteralPath $exe)) { throw "No se genero $exe" }

Write-Host ""
Write-Host "Publicacion lista: $exe" -ForegroundColor Green
Write-Host "Tamano: $([math]::Round((Get-Item $exe).Length / 1MB, 1)) MB"

$iss = Join-Path $repo "Installer\installer.iss"
if (-not (Test-Path -LiteralPath $iss)) {
    Write-Host "Falta $iss; compilalo con Inno Setup (iscc)."
    return
}
Write-Host "Listo para compilar el instalador:"
Write-Host "  iscc `"$iss`""