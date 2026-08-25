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
$virtualMarker = Join-Path $out 'VirtualLaserCube.enabled'
$selfTestOut = Join-Path $env:TEMP ("Cube7NativeSelfTest-{0}.exe" -f [guid]::NewGuid().ToString('N'))
$loopbackSelfTestOut = Join-Path $env:TEMP ("Cube7LoopbackSelfTest-{0}.exe" -f [guid]::NewGuid().ToString('N'))
$loopbackProbeOut = Join-Path $env:TEMP ("LaserOSLoopbackHookProbe-{0}.exe" -f [guid]::NewGuid().ToString('N'))
$loopbackProbePass = Join-Path $env:TEMP ("LaserOSLoopbackHookProbe-{0}.pass" -f [guid]::NewGuid().ToString('N'))
$loopbackProfile = Join-Path $env:TEMP ("Cube7LoopbackProfile-{0}.txt" -f [guid]::NewGuid().ToString('N'))
$probeOut = Join-Path $env:TEMP ("LaserOSEarlyHookProbe-{0}.exe" -f [guid]::NewGuid().ToString('N'))
$probePass = Join-Path $env:TEMP ("LaserOSEarlyHookProbe-{0}.pass" -f [guid]::NewGuid().ToString('N'))
Remove-Item $dllOut,$exeOut,$virtualMarker,$selfTestOut,$loopbackSelfTestOut,$loopbackProbeOut,$loopbackProbePass,$loopbackProfile,$probeOut,$probePass -Force -ErrorAction SilentlyContinue
$env:CUBE7_EARLYHOOK_PASSFILE = $probePass
$env:CUBE7_LOOPBACK_PASSFILE = $loopbackProbePass
@"
mode=loopback
enabled=1
address=127.0.0.1
alivePort=45456
commandPort=45457
dataPort=45458
rewriteDestinations=1
rewriteClientBinds=1
physicalOutput=0
syntheticAuthentication=0
"@ | Set-Content -Path $loopbackProfile -Encoding ASCII

$tmpCmd = Join-Path $env:TEMP ("cube7-native-{0}.cmd" -f [guid]::NewGuid().ToString('N'))
@"
@echo off
call "$dev" -arch=$Arch -host_arch=x64
if errorlevel 1 exit /b %errorlevel%
cd /d "$root"
cl /nologo /DNOMINMAX /std:c++17 /EHsc /O2 /Fe:"$selfTestOut" VirtualLaserCubeSelfTest.cpp
if errorlevel 1 exit /b %errorlevel%
"$selfTestOut"
if errorlevel 1 exit /b %errorlevel%
cl /nologo /DNOMINMAX /std:c++17 /EHsc /O2 /Fe:"$loopbackSelfTestOut" LoopbackAutoconfigSelfTest.cpp Ws2_32.lib
if errorlevel 1 exit /b %errorlevel%
"$loopbackSelfTestOut"
if errorlevel 1 exit /b %errorlevel%
cl /nologo /DNOMINMAX /std:c++17 /EHsc /O2 /LD /Fe:"$dllOut" LaserOSHook.cpp Ws2_32.lib
if errorlevel 1 exit /b %errorlevel%
cl /nologo /DNOMINMAX /std:c++17 /EHsc /O2 /Fe:"$exeOut" Injector.cpp
if errorlevel 1 exit /b %errorlevel%
cl /nologo /DNOMINMAX /std:c++17 /EHsc /O2 /Fe:"$loopbackProbeOut" LaserOSLoopbackHookProbe.cpp Ws2_32.lib
if errorlevel 1 exit /b %errorlevel%
copy /y "$loopbackProfile" "$virtualMarker" >nul
"$exeOut" --launch "$loopbackProbeOut" --dll "$dllOut" --once
if errorlevel 1 exit /b %errorlevel%
if not exist "$loopbackProbePass" (
  echo LOOPBACK_HOOK_PROBE FAIL: injected routing did not deliver broadcast command to localhost server
  exit /b 42
)
cl /nologo /DNOMINMAX /std:c++17 /EHsc /O2 /Fe:"$probeOut" LaserOSEarlyHookProbe.cpp Ws2_32.lib
if errorlevel 1 exit /b %errorlevel%
echo ==== EARLYHOOK PROBE IMPORTS ====
dumpbin /nologo /imports "$probeOut"
if errorlevel 1 exit /b %errorlevel%
echo ==== END EARLYHOOK PROBE IMPORTS ====
copy /y nul "$virtualMarker" >nul
"$exeOut" --launch "$probeOut" --dll "$dllOut" --once
if errorlevel 1 exit /b %errorlevel%
if not exist "$probePass" (
  echo EARLYHOOK_PROBE FAIL: target was not launched or did not discover virtual LaserCube
  exit /b 41
)
exit /b 0
"@ | Set-Content -Path $tmpCmd -Encoding ASCII

try {
    & $env:ComSpec /d /c $tmpCmd
    if ($LASTEXITCODE -ne 0) { throw "Native build failed: $LASTEXITCODE" }
}
finally {
    Remove-Item $tmpCmd,$selfTestOut,$loopbackSelfTestOut,$loopbackProbeOut,$loopbackProbePass,$loopbackProfile,$probeOut,$probePass,$virtualMarker -Force -ErrorAction SilentlyContinue
    Remove-Item Env:CUBE7_EARLYHOOK_PASSFILE -ErrorAction SilentlyContinue
    Remove-Item Env:CUBE7_LOOPBACK_PASSFILE -ErrorAction SilentlyContinue
}

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
if (Test-Path $virtualMarker) {
    throw 'Native build leaked VirtualLaserCube.enabled into release output.'
}

Write-Host ("[OK] LaserOSHook.dll: {0} bytes" -f (Get-Item $dllOut).Length)
Write-Host ("[OK] Cube7Injector.exe: {0} bytes" -f (Get-Item $exeOut).Length)
Write-Host '[OK] VirtualLaserCube.enabled probe marker cleaned from runtime output.'
Write-Host "Built native runtime: $out"
