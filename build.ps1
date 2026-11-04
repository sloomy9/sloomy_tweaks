<#
.SYNOPSIS
  Builds, tests and publishes Sloomy Tweaks on Windows. Optionally signs the binaries and builds the installer.

.EXAMPLE
  ./build.ps1 -Configuration Release
  ./build.ps1 -Configuration Release -SignThumbprint 0123ABCD... -BuildInstaller
#>
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')] [string] $Runtime = 'win-x64',
    [string] $SignThumbprint,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [switch] $BuildInstaller
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host '==> Restoring and building solution' -ForegroundColor Magenta
dotnet build SloomyTweaks.sln -c $Configuration
if ($LASTEXITCODE) { throw 'Build failed.' }

Write-Host '==> Running tests' -ForegroundColor Magenta
dotnet run --project tests/SloomyTweaks.Tests -c $Configuration --no-build
if ($LASTEXITCODE) { throw 'Tests failed.' }

if (-not (Test-Path 'src/SloomyTweaks.App/licensing.json')) {
    Write-Warning 'src/SloomyTweaks.App/licensing.json is missing: the published app will run with licensing NOT configured.'
}

$out = "artifacts/SloomyTweaks-$Runtime"
Write-Host "==> Publishing self-contained app to $out" -ForegroundColor Magenta
dotnet publish src/SloomyTweaks.App -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out
if ($LASTEXITCODE) { throw 'Publish failed.' }

if ($SignThumbprint) {
    Write-Host '==> Signing' -ForegroundColor Magenta
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe not found (install the Windows SDK).' }
    & $signtool.FullName sign /sha1 $SignThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /d 'Sloomy Tweaks' "$out\SloomyTweaks.exe"
    if ($LASTEXITCODE) { throw 'Signing failed.' }
}

if ($BuildInstaller) {
    Write-Host '==> Building installer (Inno Setup)' -ForegroundColor Magenta
    $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (-not (Test-Path $iscc)) { throw 'Inno Setup 6 not found.' }
    & $iscc "/DSourceDir=$((Resolve-Path $out).Path)" installer/SloomyTweaks.iss
    if ($LASTEXITCODE) { throw 'Installer build failed.' }
    if ($SignThumbprint) {
        Get-ChildItem artifacts/installer/*.exe | ForEach-Object {
            & $signtool.FullName sign /sha1 $SignThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 /d 'Sloomy Tweaks Setup' $_.FullName
        }
    }
}
Write-Host '==> Done' -ForegroundColor Green
