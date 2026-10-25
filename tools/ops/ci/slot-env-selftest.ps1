# Self-test of SlotEnvLib.ps1 (client-data env resolution of run-in-slot.ps1). Exit 0 = all checks passed, 1 = a check failed.
#   pwsh -NoProfile -File tools/ops/ci/slot-env-selftest.ps1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\OpsEnv.ps1')   # the same StrictMode run-in-slot.ps1 runs under, whatever the lib sets
. (Join-Path $PSScriptRoot 'SlotEnvLib.ps1')
$base = Join-Path ([IO.Path]::GetTempPath()) "slot-env-selftest-$PID"
$script:pass = 0; $script:fail = 0

function Check([string]$Name, [bool]$Ok) {
    if ($Ok) { $script:pass++; "PASS $Name" } else { $script:fail++; "FAIL $Name" }
}
function New-Dirs([string]$Name, $WorkJson, $LanesJson) {
    $w = Join-Path $base "$Name\work"; $l = Join-Path $base "$Name\lanes"
    New-Item -ItemType Directory -Force $w, $l | Out-Null
    if ($null -ne $WorkJson) { [IO.File]::WriteAllText((Join-Path $w 'slot-env.local.json'), $WorkJson) }
    if ($null -ne $LanesJson) { [IO.File]::WriteAllText((Join-Path $l 'slot-env.local.json'), $LanesJson) }
    return @{ Work = $w; Lanes = $l }
}
function Esc([string]$p) { $p.Replace('\', '/') }

try {
    $real = Join-Path $base 'real'; New-Item -ItemType Directory -Force $real | Out-Null
    $rj = Esc $real

    # Wrong-shape files must never throw and must report "ignoring unreadable".
    foreach ($case in @(
        @('empty file', ''), @('null', 'null'), @('array', '[]'), @('string', '"str"'), @('number', '5'), @('garbage', '{not json'))) {
        $d = New-Dirs ('shape-' + $case[0]) $case[1] $null
        $r = Resolve-SlotClientEnv -WorkDir $d.Work -LanesRoot $d.Lanes
        Check "$($case[0]): no throw, both unset" ($null -eq $r.ClientDir -and $null -eq $r.ClientFilesDir)
        Check "$($case[0]): says ignoring unreadable" (@($r.Messages | Where-Object { $_ -like 'ignoring unreadable*' }).Count -eq 1)
    }

    $d = New-Dirs 'emptyobj' '{}' $null
    $r = Resolve-SlotClientEnv -WorkDir $d.Work -LanesRoot $d.Lanes
    Check '{}: both unset' ($null -eq $r.ClientDir -and $null -eq $r.ClientFilesDir)
    Check '{}: per-variable skip lines, file exists wording' (@($r.Messages | Where-Object { $_ -like '*will skip*not set in slot-env.local.json*' }).Count -eq 2)

    $d = New-Dirs 'onekey' "{`"clientDir`":`"$rj`"}" $null
    $r = Resolve-SlotClientEnv -WorkDir $d.Work -LanesRoot $d.Lanes
    Check 'missing key: clientDir read' ($r.ClientDir -eq $rj)
    Check 'missing key: files dir unset' ($null -eq $r.ClientFilesDir)
    Check 'missing key: only files dir named in skip' (@($r.Messages | Where-Object { $_ -like '*will skip*' }).Count -eq 1 -and $r.Messages[0] -like '*CLIENT_FILES_DIR*')

    $d = New-Dirs 'wrongtype' '{"clientDir":5,"clientFilesDir":["a"]}' $null
    $r = Resolve-SlotClientEnv -WorkDir $d.Work -LanesRoot $d.Lanes
    Check 'non-string values ignored' ($null -eq $r.ClientDir -and $null -eq $r.ClientFilesDir)
    Check 'non-string values reported per key' (@($r.Messages | Where-Object { $_ -like 'ignoring client*Dir in*not a non-empty string*' }).Count -eq 2)

    $d = New-Dirs 'onlybad' '[]' $null
    $r = Resolve-SlotClientEnv -WorkDir $d.Work -LanesRoot $d.Lanes
    Check 'unreadable file only: no-readable-file wording' (@($r.Messages | Where-Object { $_ -like '*unset and no readable slot-env.local.json found*' }).Count -eq 2)

    $d = New-Dirs 'both' "{`"clientDir`":`"$rj`",`"clientFilesDir`":`"$rj`"}" $null
    $r = Resolve-SlotClientEnv -WorkDir $d.Work -LanesRoot $d.Lanes
    Check 'both keys: both read, no messages' ($r.ClientDir -eq $rj -and $r.ClientFilesDir -eq $rj -and $r.Messages.Count -eq 0)

    $d = New-Dirs 'none' $null $null
    $r = Resolve-SlotClientEnv -WorkDir $d.Work -LanesRoot $d.Lanes
    Check 'no file: no-file wording' (@($r.Messages | Where-Object { $_ -like '*unset and no readable slot-env.local.json found*' }).Count -eq 2)

    # Precedence: env, then WorkDir, then LanesRoot (per variable).
    $d = New-Dirs 'prec' '{"clientDir":"W-dir","clientFilesDir":"W-files"}' '{"clientDir":"L-dir","clientFilesDir":"L-files"}'
    $r = Resolve-SlotClientEnv -EnvClientDir 'E-dir' -WorkDir $d.Work -LanesRoot $d.Lanes
    Check 'precedence: env beats WorkDir' ($r.ClientDir -eq 'E-dir')
    Check 'precedence: WorkDir beats LanesRoot' ($r.ClientFilesDir -eq 'W-files')
    $d = New-Dirs 'prec2' '{"clientDir":"W-dir"}' '{"clientDir":"L-dir","clientFilesDir":"L-files"}'
    $r = Resolve-SlotClientEnv -WorkDir $d.Work -LanesRoot $d.Lanes
    Check 'precedence: per variable, LanesRoot fills the gap' ($r.ClientDir -eq 'W-dir' -and $r.ClientFilesDir -eq 'L-files')
    $d = New-Dirs 'prec3' '{bad' '{"clientDir":"L-dir","clientFilesDir":"L-files"}'
    $r = Resolve-SlotClientEnv -WorkDir $d.Work -LanesRoot $d.Lanes
    Check 'bad WorkDir file falls through to LanesRoot' ($r.ClientDir -eq 'L-dir' -and $r.ClientFilesDir -eq 'L-files')

    # Nonexistent configured path: warning; existing: none.
    Check 'nonexistent path warns' (@((Resolve-SlotClientEnv -EnvClientDir (Join-Path $base 'nope') -EnvClientFilesDir $real -WorkDir $d.Work -LanesRoot $d.Lanes).Messages | Where-Object { $_ -like 'warning: client dir*does not exist*' }).Count -eq 1)

    # Quoting.
    Check 'single-quote escaping' ((ConvertTo-PsSingleQuoted "a'b") -eq "'a''b'")
    # Every character PowerShell reads as a single quote must round-trip through a parsed child script.
    foreach ($q in 0x27, 0x2018, 0x2019, 0x201A, 0x201B) {
        $v = 'C:\a' + [char]$q + 'b\c'
        $back = $null
        try { $back = & ([scriptblock]::Create('$x = ' + (ConvertTo-PsSingleQuoted $v) + '; $x')) } catch { }
        Check ('quote U+{0:X4} round-trips through a parsed script' -f $q) ($back -eq $v)
    }
}
finally {
    Remove-Item -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue
}
"slot-env-selftest: passed=$script:pass failed=$script:fail"
exit ([int]($script:fail -gt 0))
