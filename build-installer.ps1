# Builds artifacts\WinXplorer-Setup-<version>.exe:
#   1. publishes WinXplorer self-contained (includes .NET, so the target PC needs nothing else) to artifacts\app
#   2. compiles installer\WinXplorer.iss with Inno Setup 6 (https://jrsoftware.org/isinfo.php)
# Usage:  .\build-installer.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src\WinXplorer\WinXplorer.csproj'
$app = Join-Path $root 'artifacts\app'

$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "WinXplorer $version"

if (Test-Path $app) { Remove-Item $app -Recurse -Force }
dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $app
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup)' }
& $iscc "/DAppVersion=$version" (Join-Path $root 'installer\WinXplorer.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
Get-ChildItem (Join-Path $root 'artifacts') -Filter 'WinXplorer-Setup-*.exe' | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
