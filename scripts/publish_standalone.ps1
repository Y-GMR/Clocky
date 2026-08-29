# Clocky Standalone Single-File Build Script
$ErrorActionPreference = 'Stop'

Write-Host "=========================================" -ForegroundColor Cyan
Write-Host " Building Standalone Clocky.exe (.NET 9) " -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan

$projectDir = Resolve-Path "$PSScriptRoot\..\src\Clocky"
$distDir = Resolve-Path "$PSScriptRoot\..\dist" -ErrorAction SilentlyContinue

if (-not $distDir) {
    New-Item -ItemType Directory -Path "$PSScriptRoot\..\dist" -Force | Out-Null
    $distDir = "$PSScriptRoot\..\dist"
}

Write-Host "Publishing self-contained win-x64 binary..." -ForegroundColor Yellow

dotnet publish "$projectDir\Clocky.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o "$distDir"

if ($LASTEXITCODE -eq 0) {
    $exePath = Join-Path $distDir "Clocky.exe"
    $fileSizeMB = [Math]::Round(((Get-Item $exePath).Length / 1MB), 2)
    $hash = (Get-FileHash -Path $exePath -Algorithm SHA256).Hash.ToLower()
    $checksumContent = "$hash  Clocky.exe"
    Set-Content -Path (Join-Path $distDir "SHA256SUMS.txt") -Value $checksumContent -Encoding ASCII
    Write-Host "`n[SUCCESS] Standalone single-file published successfully!" -ForegroundColor Green
    Write-Host "Binary Location: $exePath ($fileSizeMB MB)" -ForegroundColor Green
    Write-Host "SHA256 Checksum: $hash" -ForegroundColor Cyan
} else {
    Write-Host "`n[ERROR] Publish failed with exit code $LASTEXITCODE" -ForegroundColor Red
}
