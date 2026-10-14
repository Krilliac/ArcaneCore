<#
.SYNOPSIS
  Sample Windows disk latency, queue length, page-in rate and available memory once per second into a CSV file.
.PARAMETER Seconds
  Number of 1-second samples to collect (default 150).
.PARAMETER Out
  Output CSV file path.
.PARAMETER Drive
  PhysicalDisk instance name to monitor (default '0 C:').
#>
param(
    [int]$Seconds = 150,
    [Parameter(Mandatory)][string]$Out,
    [string]$Drive = '0 C:'
)
$counters = "\PhysicalDisk($Drive)\Avg. Disk sec/Write", "\PhysicalDisk($Drive)\Avg. Disk sec/Read", "\PhysicalDisk($Drive)\Current Disk Queue Length",
            "\PhysicalDisk($Drive)\Disk Write Bytes/sec", '\Memory\Pages Input/sec', '\Memory\Available MBytes'
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('utc,wr_ms,rd_ms,queue,write_MBps,pages_in_per_s,avail_MB')
Get-Counter -Counter $counters -SampleInterval 1 -MaxSamples $Seconds | ForEach-Object {
    $v = @($_.CounterSamples | ForEach-Object { $_.CookedValue })
    $lines.Add(('{0},{1:N1},{2:N1},{3},{4:N1},{5:N0},{6:N0}' -f $_.Timestamp.ToUniversalTime().ToString('HH:mm:ss'), ($v[0]*1000), ($v[1]*1000), $v[2], ($v[3]/1MB), $v[4], $v[5]))
}
Set-Content -Path $Out -Value $lines
