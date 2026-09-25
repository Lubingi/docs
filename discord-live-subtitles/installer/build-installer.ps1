<#
  Builds LiveSubtitlesSetup-<version>.exe:
    1. runs the unit tests
    2. publishes the app self-contained for win-x64 (includes the .NET runtime and the ONNX models)
    3. compiles installer\LiveSubtitles.iss with Inno Setup 6
  Requirements: .NET 10 SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup  or  choco install innosetup).
  Usage (from the repository folder):  powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
#>
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$props = Get-Content "$root\Directory.Build.props"
$version = $props.Project.PropertyGroup.Version
Write-Host "Building Live Subtitles $version" -ForegroundColor Cyan

if (-not $SkipTests) {
    dotnet test tests\LiveSubtitles.Core.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

$publish = "$root\artifacts\publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish src\LiveSubtitles.App -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
foreach ($m in 'silero_vad.onnx', 'campplus_voxceleb_16k.onnx') {
    if (-not (Test-Path "$publish\models\$m")) { throw "Model $m missing from the publish output" }
}

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source }
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

& $iscc "/DMyAppVersion=$version" "/DPublishDir=$publish" "$root\installer\LiveSubtitles.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
Write-Host "Installer: $root\installer\Output\LiveSubtitlesSetup-$version.exe" -ForegroundColor Green
