param([Parameter(Mandatory=$true)][string]$Path)
Get-Content $Path | ForEach-Object {
  try { $_ | ConvertFrom-Json } catch { return }
} | Select-Object timeUtc,direction,remote,port,payloadLength,@{n='Kind';e={$_.decoded.kind}},@{n='Opcode';e={$_.decoded.opcode}},@{n='Points';e={$_.decoded.points}} | Format-Table -AutoSize
