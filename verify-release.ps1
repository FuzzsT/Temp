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
Write-Host 'FULLBRIDGE_READY=1'
exit 0
