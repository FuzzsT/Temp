Get-Process | Where-Object { $_.ProcessName -match 'LaserOS|LaserCube|Laser' } | Select-Object Id,ProcessName,Path | Format-Table -AutoSize
