param([ValidateSet('x64','x86')][string]$Arch='x64')
$ErrorActionPreference='Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = [System.IO.Path]::GetFullPath((Join-Path $root '..\bin'))
New-Item -ItemType Directory -Force -Path $out | Out-Null

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (!(Test-Path $vswhere)) { throw 'Visual Studio Build Tools / vswhere not found.' }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'MSVC C++ build tools not installed.' }
$dev = Join-Path $vs 'Common7\Tools\VsDevCmd.bat'
if (!(Test-Path $dev)) { throw 'VsDevCmd.bat not found.' }

$dllOut = Join-Path $out 'LaserOSHook.dll'
$exeOut = Join-Path $out 'Cube7Injector.exe'
Remove-Item $dllOut,$exeOut -Force -ErrorAction SilentlyContinue

$cmd = @"
call "$dev" -arch=$Arch -host_arch=x64
if errorlevel 1 exit /b %errorlevel%
cd /d "$root"
cl /nologo /std:c++17 /EHsc /O2 /LD /Fe:"$dllOut" LaserOSHook.cpp Ws2_32.lib
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /EHsc /O2 /Fe:"$exeOut" Injector.cpp
if errorlevel 1 exit /b %errorlevel%
"@
cmd.exe /d /s /c $cmd
if ($LASTEXITCODE -ne 0) { throw "Native build failed: $LASTEXITCODE" }

$missing = @()
if (!(Test-Path $dllOut -PathType Leaf)) { $missing += 'LaserOSHook.dll' }
if (!(Test-Path $exeOut -PathType Leaf)) { $missing += 'Cube7Injector.exe' }
if ($missing.Count -gt 0) {
    Write-Host 'Native directory contents:'
    Get-ChildItem -Path $root -Force | Format-Table Name,Length,FullName -AutoSize
    Write-Host 'Target bin directory contents:'
    Get-ChildItem -Path $out -Force | Format-Table Name,Length,FullName -AutoSize
    throw ("Native build reported success but output is missing: " + ($missing -join ', '))
}

Write-Host ("[OK] LaserOSHook.dll: {0} bytes" -f (Get-Item $dllOut).Length)
Write-Host ("[OK] Cube7Injector.exe: {0} bytes" -f (Get-Item $exeOut).Length)
Write-Host "Built native runtime: $out"
