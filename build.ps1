param(
    [ValidateSet('x64','x86')][string]$Arch='x64',
    [switch]$Publish
)
$ErrorActionPreference='Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root 'bin'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet SDK 8 is required. Install Microsoft .NET 8 SDK, then rerun build.ps1.'
}

Write-Host '[1/4] Building Cube7Bridge managed runtime...'
$proj = Join-Path $root 'src\Cube7Bridge\Cube7Bridge.csproj'
dotnet publish $proj -c Release -r "win-$Arch" --self-contained true -p:PublishSingleFile=true -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# IMPORTANT: native build runs AFTER dotnet publish. dotnet publish may clean its
# output directory, which previously removed Cube7Injector.exe/LaserOSHook.dll.
Write-Host '[2/4] Building native hook + injector...'
& (Join-Path $root 'native\build-native.ps1') -Arch $Arch

Write-Host '[3/4] Copying runtime configuration...'
Copy-Item (Join-Path $root 'config.json') (Join-Path $out 'config.json') -Force

Write-Host '[4/4] Verifying FullBridge runtime layout...'
& (Join-Path $root 'verify-release.ps1') -BinDir $out
if ($LASTEXITCODE -ne 0) { throw 'FullBridge release verification failed.' }

Write-Host '[OK] FullBridge runtime is complete.'
Write-Host "Run: $root\start-full.cmd"
