param([string]$BinDir = (Join-Path $PSScriptRoot 'bin'))
$ErrorActionPreference='Stop'
$required = @(
  'Cube7Bridge.exe',
  'Cube7Injector.exe',
  'LaserOSHook.dll',
  'config.json'
)
$missing = @($required | Where-Object { -not (Test-Path (Join-Path $BinDir $_) -PathType Leaf) })
if ($missing.Count -gt 0) {
  Write-Error ("FULLBRIDGE_INCOMPLETE: missing: " + ($missing -join ', '))
  exit 1
}
foreach ($name in $required) {
  $p = Join-Path $BinDir $name
  $size = (Get-Item $p).Length
  if ($size -le 0) { Write-Error "FULLBRIDGE_INCOMPLETE: zero-byte file: $name"; exit 1 }
  Write-Host ("[OK] {0} ({1} bytes)" -f $name, $size)
}

foreach ($name in @('Cube7Bridge.exe','Cube7Injector.exe','LaserOSHook.dll')) {
  $p = Join-Path $BinDir $name
  $bytes = [System.IO.File]::ReadAllBytes($p)
  if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
    Write-Error "FULLBRIDGE_INVALID_PE: $name does not have an MZ header"
    exit 1
  }
  Write-Host "[OK] PE header $name"
}

$configPath = Join-Path $BinDir 'config.json'
$config = Get-Content $configPath -Raw | ConvertFrom-Json
if ($config.allowBleWrites -ne $false) { Write-Error 'SAFETY_FAIL: allowBleWrites must be false'; exit 1 }
if ($config.virtualLaserCube.enabled -ne $false) { Write-Error 'SAFETY_FAIL: virtualLaserCube.enabled must default to false'; exit 1 }
if ($config.preflight.enabled -ne $true) { Write-Error 'HARDENING_FAIL: preflight.enabled must be true'; exit 1 }
if ($config.preflight.requireVerifiedSha256 -ne $true) { Write-Error 'HARDENING_FAIL: requireVerifiedSha256 must be true'; exit 1 }
if ([string]::IsNullOrWhiteSpace([string]$config.preflight.expectedLaserOsSha256) -or ([string]$config.preflight.expectedLaserOsSha256).Length -ne 64) {
  Write-Error 'HARDENING_FAIL: expectedLaserOsSha256 must be pinned'; exit 1
}
if ($config.hardening.writeDryRunTranslation -ne $true) { Write-Error 'HARDENING_FAIL: dry-run translation must be enabled'; exit 1 }
if ($config.hardening.automaticSupportBundle -ne $true) { Write-Error 'HARDENING_FAIL: automatic support bundle must be enabled'; exit 1 }
Write-Host '[OK] safety defaults: physical output path disabled, BLE payload writes disabled'
Write-Host '[OK] hardened defaults: SHA preflight + dry-run translator + support bundle enabled'
Write-Host 'FULLBRIDGE_READY=1'
exit 0
