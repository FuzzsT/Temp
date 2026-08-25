param(
    [ValidateSet('x64','x86')][string]$Arch='x64',
    [switch]$Publish
)
$ErrorActionPreference='Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root 'bin'
$ccOut = Join-Path $root 'controlcenter-bin'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $ccOut) { Remove-Item $ccOut -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out,$ccOut | Out-Null

if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet SDK 8 is required. Install Microsoft .NET 8 SDK, then rerun build.ps1.'
}

Write-Host '[1/5] Building Cube7Bridge managed runtime...'
$proj = Join-Path $root 'src\Cube7Bridge\Cube7Bridge.csproj'
dotnet publish $proj -c Release -r "win-$Arch" --self-contained true -p:PublishSingleFile=true -o $out
if ($LASTEXITCODE -ne 0) { throw 'Cube7Bridge dotnet publish failed.' }

Write-Host '[2/5] Building native hook + injector...'
& (Join-Path $root 'native\build-native.ps1') -Arch $Arch

Write-Host '[3/5] Building WPF Control Center...'
$ccProj = Join-Path $root 'src\Cube7ControlCenter\Cube7ControlCenter.csproj'
dotnet publish $ccProj -c Release -r "win-$Arch" --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $ccOut
if ($LASTEXITCODE -ne 0) { throw 'Cube7ControlCenter dotnet publish failed.' }
if (!(Test-Path (Join-Path $ccOut 'Cube7ControlCenter.exe'))) { throw 'Cube7ControlCenter.exe missing after publish.' }

Write-Host '[4/5] Copying runtime configuration...'
Copy-Item (Join-Path $root 'config.json') (Join-Path $out 'config.json') -Force
Copy-Item (Join-Path $root 'config.diagnostic.json') (Join-Path $out 'config.diagnostic.json') -Force

Write-Host '[5/5] Verifying FullBridge runtime layout...'
& (Join-Path $root 'verify-release.ps1') -BinDir $out
if ($LASTEXITCODE -ne 0) { throw 'FullBridge release verification failed.' }

Write-Host '[OK] FullBridge runtime + Control Center build is complete.'
Write-Host "Console: $root\start-full.cmd"
Write-Host "GUI: $ccOut\Cube7ControlCenter.exe"
