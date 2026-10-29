# One-shot health read of the live World after a (re)start: startup markers, log-level counts, distinct FAIL/CRIT/exception lines,
# the most frequent WARN lines (numbers folded), tick health, first-chance crash reports, and the daemons' process state. Read-only.
#   pwsh -NoProfile -File tools/ops/live/livecheck.ps1 [-Since '2026-10-16 22:17'] [-Tail 60000] [-LiveHome <dir>] [-LiveProfile <dir>]
# Reads whichever log the daemons write (Logging:ArcaneCore:Format): text lines as they are, JSON lines folded into the same
# 'yyyy-MM-dd HH:mm:ss.fff LEVEL message' shape so the sections below work for both.
# -Since is compared with the log's UTC timestamps; it defaults to 15 minutes ago (UTC). Uses ARCANECORE_LIVE_HOME.
param(
    [string]$Since = ([DateTime]::UtcNow.AddMinutes(-15).ToString('yyyy-MM-dd HH:mm')),
    [int]$Tail = 60000,
    [string]$LiveHome,
    [Alias('Profile')][string]$LiveProfile
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LiveEnv.ps1')
$LiveHome = Resolve-LiveHome $LiveHome
$l = Get-LiveDaemonLogPath -Role World -LiveHome $LiveHome -LiveProfile $LiveProfile
# Parse -Since (any invariant form: '2026-10-16 22:17', ISO 'o' with T and offset) and compare in the log's own format; a plain string
# compare of an ISO value put 'T' after every ' ' and kept nothing. A line without a timestamp (a stack trace) goes with the line above it.
$sinceUtc = [DateTime]::Parse($Since, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]'AssumeUniversal, AdjustToUniversal')
$sinceKey = $sinceUtc.ToString('yyyy-MM-dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture)
$keep = $false
function ConvertFrom-JsonLogLine([string]$Line) {
    # A JSON-lines record (keys ts, level, message, exception) as a text-format line; anything that does not parse is returned unchanged.
    if (-not $Line.StartsWith('{')) { return $Line }
    try { $r = $Line | ConvertFrom-Json } catch { return $Line }
    if (-not $r.PSObject.Properties['ts']) { return $Line }
    $when = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse([string]$r.ts, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$when)) { return $Line }
    $when = $when.UtcDateTime
    # The text format's level codes (LogLineFormatter.LevelText).
    $level = switch ([string]$r.level) { 'Trace' { 'TRCE' } 'Debug' { 'DBUG' } 'Information' { 'INFO' } 'Warning' { 'WARN' } 'Error' { 'FAIL' } 'Critical' { 'CRIT' } default { ([string]$r.level).ToUpperInvariant() } }
    $text = "$($when.ToString('yyyy-MM-dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture)) $level $($r.message)"
    if ($r.PSObject.Properties['exception'] -and $r.exception) { $text += " $($r.exception)" }
    return $text
}
$lines = @(Get-Content $l -Tail $Tail | ForEach-Object { ConvertFrom-JsonLogLine $_ } | Where-Object {
    if ($_ -match '^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3} ') { $script:keep = $_.Substring(0, 23) -ge $sinceKey }
    $script:keep
})
"lines since ${Since} UTC: $($lines.Count) ($l)"
# A fallback file means another process owns the World's log name, so the lines above may not be (all) the World's.
foreach ($f in @(Get-LiveLogFallback -Role World -LiveHome $LiveHome -SinceUtc $sinceUtc)) { "WARNING: fallback log written since ${Since}: $($f.FullName) (another writer owns $l)" }
"--- startup markers:"
$lines | Select-String -Pattern 'listening|World started|is ready|Bot chat provider|policy model|high-load threshold|Playerbots? .*(started|spawn)|Playerbot runs:' | Select-Object -First 15 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(220, $_.Line.Length)) }
"--- level counts:"
$lines | ForEach-Object { if ($_ -match '^\S+ \S+ (\w+) ') { $Matches[1] } } | Group-Object | ForEach-Object { "$($_.Name) $($_.Count)" }
"--- ERROR (FAIL/CRIT/Exception, distinct, first 20):"
# Case-sensitive: 'Anomaly error key ...' lines are WARNs, not errors.
$lines | Select-String -CaseSensitive -Pattern ' (FAIL|CRIT) |Exception' | ForEach-Object { ($_.Line -replace '^\S+ \S+ ', '') -replace '\d+', 'N' } | Group-Object | Sort-Object Count -Descending | Select-Object -First 20 | ForEach-Object { "{0,5}x {1}" -f $_.Count, $_.Name.Substring(0, [Math]::Min(230, $_.Name.Length)) }
"--- first-chance crash reports: $(@($lines | Select-String -SimpleMatch 'crash report: first-chance').Count)"
"--- WARN top 15:"
$lines | Select-String -CaseSensitive -Pattern ' WARN ' | ForEach-Object { ($_.Line -replace '^\S+ \S+ WARN ', '') -replace '\d+(\.\d+)?', 'N' } | ForEach-Object { $_.Substring(0, [Math]::Min(150, $_.Length)) } | Group-Object | Sort-Object Count -Descending | Select-Object -First 15 | ForEach-Object { "{0,5}x {1}" -f $_.Count, $_.Name }
"--- tick health:"
$lines | Select-String -Pattern 'TickHang|tick took|slow tick|Slow world update' | Select-Object -Last 5 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(220, $_.Line.Length)) }
Get-Process ArcaneCore.World, ArcaneCore.Realm -ErrorAction SilentlyContinue | ForEach-Object { "{0} PID {1} WS {2} MB CPU {3}s {4}" -f $_.ProcessName, $_.Id, [int]($_.WorkingSet64 / 1MB), [int]$_.CPU, $_.PriorityClass }
