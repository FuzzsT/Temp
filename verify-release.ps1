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

$probeMarker = Join-Path $BinDir 'VirtualLaserCube.enabled'
if (Test-Path $probeMarker) {
  Write-Error 'SAFETY_FAIL: VirtualLaserCube.enabled probe marker leaked into release output'
  exit 1
}
Write-Host '[OK] no persistent VirtualLaserCube.enabled marker in runtime output'

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
if ($config.virtualDevice.physicalOutput -ne $false) { Write-Error 'SAFETY_FAIL: virtualDevice.physicalOutput must be false'; exit 1 }
if ($config.virtualDevice.allowSyntheticAuthentication -ne $false) { Write-Error 'SAFETY_FAIL: synthetic authentication must be false'; exit 1 }
if ($config.virtualDevice.enabled -ne $true) { Write-Error 'VIRTUAL_DEVICE_FAIL: virtualDevice.enabled must default to true in 0.5.0'; exit 1 }
if ($config.virtualDevice.network -ne $true) { Write-Error 'VIRTUAL_DEVICE_FAIL: virtualDevice.network must default to true'; exit 1 }
if ([string]$config.virtualDevice.usbHid -ne 'trace-first') { Write-Error 'VIRTUAL_DEVICE_FAIL: usbHid must remain trace-first'; exit 1 }
if ($config.virtualLaserCube.enabled -ne $true) { Write-Error 'VIRTUAL_DEVICE_FAIL: injected virtual network responder must be armed'; exit 1 }
if ($config.virtualLaserCube.networkServerEnabled -ne $false) { Write-Error 'VIRTUAL_DEVICE_FAIL: external UDP server must remain disabled by default'; exit 1 }
if ($config.preflight.enabled -ne $true) { Write-Error 'HARDENING_FAIL: preflight.enabled must be true'; exit 1 }
if ($config.preflight.requireVerifiedSha256 -ne $true) { Write-Error 'HARDENING_FAIL: requireVerifiedSha256 must be true'; exit 1 }
if ([string]::IsNullOrWhiteSpace([string]$config.preflight.expectedLaserOsSha256) -or ([string]$config.preflight.expectedLaserOsSha256).Length -ne 64) {
  Write-Error 'HARDENING_FAIL: expectedLaserOsSha256 must be pinned'; exit 1
}
if ($config.hardening.writeDryRunTranslation -ne $true) { Write-Error 'HARDENING_FAIL: dry-run translation must be enabled'; exit 1 }
if ($config.hardening.automaticSupportBundle -ne $true) { Write-Error 'HARDENING_FAIL: automatic support bundle must be enabled'; exit 1 }
if ($config.ble.autoConnect -ne $true) { Write-Error 'BLE_TRACE_FAIL: ble.autoConnect must be true for passive GATT inventory'; exit 1 }
if ($config.ble.subscribeNotifications -ne $true) { Write-Error 'BLE_TRACE_FAIL: ble.subscribeNotifications must be true for passive FFE1 trace'; exit 1 }
Write-Host '[OK] safety defaults: physical output OFF, BLE payload writes OFF, synthetic auth OFF'
Write-Host '[OK] virtual device defaults: injected network responder ON, external UDP server OFF, USB/HID trace-first'
Write-Host '[OK] BLE UART trace: auto-connect + notification subscription enabled; no FFE2 payload writes'
Write-Host '[OK] hardened defaults: SHA preflight + dry-run translator + support bundle enabled'
Write-Host 'FULLBRIDGE_READY=1'
exit 0
