param([ValidateSet('x64','x86')][string]$Arch='x64')
$ErrorActionPreference='Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $root '..\bin'
New-Item -ItemType Directory -Force -Path $out | Out-Null

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (!(Test-Path $vswhere)) { throw 'Visual Studio Build Tools 2022 / vswhere not found.' }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'MSVC C++ build tools not installed.' }
$dev = Join-Path $vs 'Common7\Tools\VsDevCmd.bat'
if (!(Test-Path $dev)) { throw 'VsDevCmd.bat not found.' }

$cmd = @"
call "$dev" -arch=$Arch -host_arch=x64
cd /d "$root"
cl /nologo /std:c++17 /EHsc /O2 /LD LaserOSHook.cpp /link /OUT:"$out\LaserOSHook.dll" Ws2_32.lib
cl /nologo /std:c++17 /EHsc /O2 Injector.cpp /link /OUT:"$out\Cube7Injector.exe"
"@
cmd.exe /c $cmd
if ($LASTEXITCODE -ne 0) { throw "Native build failed: $LASTEXITCODE" }
Write-Host "Built: $out"
