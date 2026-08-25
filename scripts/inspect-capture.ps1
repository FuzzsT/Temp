param(
  [Parameter(Mandatory=$true)][string]$Path,
  [switch]$StagesOnly
)

$rows = @()
Get-Content $Path | ForEach-Object {
  try { $rows += ($_ | ConvertFrom-Json) } catch { }
}

$highest = 0
$stageRows = @()

foreach ($r in $rows) {
  $hex = [string]$r.hexPrefix
  if ([string]::IsNullOrWhiteSpace($hex) -or $hex.Length -lt 2) { continue }
  $op = $hex.Substring(0,2).ToUpperInvariant()
  $len = [int]$r.payloadLength
  $stage = $null
  $number = 0

  if ($op -eq '27' -and $len -eq 1) { $stage='DISCOVERY_REQUEST'; $number=1 }
  elseif ($op -eq '27' -and $len -eq 2 -and $hex.ToUpperInvariant().StartsWith('2700')) { $stage='DISCOVERY_ACCEPTED'; $number=2 }
  elseif ($op -eq '77' -and $len -eq 1) { $stage='FULL_INFO_REQUEST'; $number=3 }
  elseif ($op -eq '77' -and $len -eq 64 -and $hex.ToUpperInvariant().StartsWith('770000')) { $stage='FULL_INFO_ACCEPTED'; $number=4 }
  elseif ($op -eq 'B0' -and $len -gt 2) { $stage='AUTH_REQUEST'; $number=5 }
  elseif ($op -eq 'B0' -and $len -eq 2 -and $hex.ToUpperInvariant().StartsWith('B000')) { $stage='AUTH_REQUEST_ACK'; $number=6 }
  elseif ($op -eq 'B1' -and $len -eq 1) { $stage='AUTH_RESPONSE_QUERY'; $number=7 }
  elseif ($op -eq 'B1' -and $len -ge 3) { $stage='AUTH_RESPONSE_CAPTURED'; $number=8 }
  elseif ($highest -ge 8 -and @('78','82','8A','8D','A0') -contains $op) { $stage='POST_AUTH_DEVICE_TRAFFIC'; $number=9 }

  if ($stage -and $number -gt $highest) {
    $highest = $number
    $stageRows += [pscustomobject]@{
      Stage = $stage
      TimeUtc = $r.timeUtc
      Direction = $r.direction
      Port = $r.port
      Length = $len
    }
  }
}

if ($stageRows.Count -eq 0) {
  Write-Host '[stage] No recognized LaserCube handshake stage found.'
} else {
  Write-Host '=== LaserCube handshake progress ==='
  $stageRows | Format-Table -AutoSize
  Write-Host ('Highest stage: {0}/9 {1}' -f $highest, $stageRows[-1].Stage)
}

if (-not $StagesOnly) {
  Write-Host ''
  Write-Host '=== Capture packets ==='
  $rows | Select-Object timeUtc,direction,remote,port,payloadLength,hexPrefix,@{n='Kind';e={$_.decoded.kind}},@{n='Opcode';e={$_.decoded.opcode}},@{n='Points';e={$_.decoded.points}} | Format-Table -AutoSize
}
