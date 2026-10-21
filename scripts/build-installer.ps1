<#
Builds the Dream Player installer: publishes the app self-contained (.NET and
Windows App SDK included, nothing to install on the target machine) and packs
it with Inno Setup 6 into dist\DreamPlayer-<version>-setup.exe.

The core must be built first (.\scripts\build-core.ps1), or pass -CoreDll.
Inno Setup: https://jrsoftware.org/isinfo.php (or `winget install JRSoftware.InnoSetup`).

  .\scripts\build-installer.ps1                   # version from DreamPlayer.csproj
  .\scripts\build-installer.ps1 -Version 1.2.0
  .\scripts\build-installer.ps1 -CoreDll path\to\dream_player.dll
#>
param(
    [string]$Version,
    [string]$CoreDll,
    [string]$OutDir = (Join-Path $PSScriptRoot "..\dist")
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $root "DreamPlayer\DreamPlayer.csproj"

if (-not $Version) {
    $Version = ([xml](Get-Content -Raw $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { $Version = "0.21" }
}

$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found: winget install JRSoftware.InnoSetup" }

$publish = Join-Path $root "DreamPlayer\bin\installer\publish"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }

Write-Host "publishing $Version -> $publish"
$props = @("-p:Platform=x64", "-p:Version=$Version")
if ($CoreDll) { $props += "-p:CoreDll=$((Resolve-Path $CoreDll).Path)" }
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish @props
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
if (-not (Test-Path (Join-Path $publish "dream_player.dll"))) {
    throw "dream_player.dll is not in the publish output: build the core (.\scripts\build-core.ps1) or pass -CoreDll"
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
Write-Host "packing with $iscc"
& $iscc /Qp "/DAppVersion=$Version" "/DPublishDir=$publish" "/O$OutDir" (Join-Path $root "installer\DreamPlayer.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
Write-Host "done: $(Join-Path $OutDir "DreamPlayer-$Version-setup.exe")"
