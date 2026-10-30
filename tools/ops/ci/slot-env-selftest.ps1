# Self-test of SlotEnvLib.ps1 (client-data env resolution of run-in-slot.ps1) and of run-in-slot.ps1's exit-code honesty. Exit 0 = all checks passed, 1 = a check failed.
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

    # MSBuild width rewrite (Add-MaxCpuCount).
    Check 'width: default build gets the cap' ((Add-MaxCpuCount -Command 'dotnet build x.sln -c Release' -MaxCpuCount 6) -eq 'dotnet build -maxcpucount:6 x.sln -c Release')
    Check 'width: existing -m left alone' ((Add-MaxCpuCount -Command 'dotnet build x.sln -m:2' -MaxCpuCount 6) -eq 'dotnet build x.sln -m:2')
    Check 'width: existing -MaxCpuCount left alone' ((Add-MaxCpuCount -Command 'dotnet build x.sln -MaxCpuCount 0' -MaxCpuCount 6) -eq 'dotnet build x.sln -MaxCpuCount 0')
    Check 'width: 0 disables the rewrite' ((Add-MaxCpuCount -Command 'dotnet build x.sln' -MaxCpuCount 0) -eq 'dotnet build x.sln')
    Check 'width: lands before the -- passthrough' ((Add-MaxCpuCount -Command 'dotnet test x.csproj -- RunConfiguration.Foo=1' -MaxCpuCount 6) -eq 'dotnet test -maxcpucount:6 x.csproj -- RunConfiguration.Foo=1')
    Check 'width: dotnet.exe and a chained second build' ((Add-MaxCpuCount -Command 'dotnet.exe restore; dotnet.exe build a; dotnet test b' -MaxCpuCount 4) -eq 'dotnet.exe restore -maxcpucount:4; dotnet.exe build -maxcpucount:4 a; dotnet test -maxcpucount:4 b')
    Check 'width: build-server is not a build' ((Add-MaxCpuCount -Command 'dotnet build-server shutdown' -MaxCpuCount 6) -eq 'dotnet build-server shutdown')
    Check 'width: stray non-dotnet -m before a build does not suppress the cap' ((Add-MaxCpuCount -Command 'git commit -m x; dotnet build a' -MaxCpuCount 6) -eq 'git commit -m x; dotnet build -maxcpucount:6 a')
    Check 'width: python -m && build still capped' ((Add-MaxCpuCount -Command 'python -m foo && dotnet build a' -MaxCpuCount 6) -eq 'python -m foo && dotnet build -maxcpucount:6 a')
    Check 'width: -m after -- belongs to the runner' ((Add-MaxCpuCount -Command 'dotnet test a -- -m 1' -MaxCpuCount 6) -eq 'dotnet test -maxcpucount:6 a -- -m 1')
    Check 'width: -m on one call does not uncap the next' ((Add-MaxCpuCount -Command 'dotnet build a -m:2; dotnet test b' -MaxCpuCount 6) -eq 'dotnet build a -m:2; dotnet test -maxcpucount:6 b')
    Check 'width: newline separates calls' ((Add-MaxCpuCount -Command ("dotnet build a -m:2`ndotnet build b") -MaxCpuCount 6) -eq ("dotnet build a -m:2`ndotnet build -maxcpucount:6 b"))
    Check 'width: restore is capped' ((Add-MaxCpuCount -Command 'dotnet restore x.slnx --locked-mode' -MaxCpuCount 6) -eq 'dotnet restore -maxcpucount:6 x.slnx --locked-mode')
    Check 'width: quoted dotnet.exe path' ((Add-MaxCpuCount -Command '& "C:\sdk\dotnet.exe" build a' -MaxCpuCount 6) -eq '& "C:\sdk\dotnet.exe" build -maxcpucount:6 a')
    Check 'width: verb is case-insensitive' ((Add-MaxCpuCount -Command 'DOTNET Build a' -MaxCpuCount 6) -eq 'DOTNET Build -maxcpucount:6 a')
    Check 'width: dotnet run is not capped' ((Add-MaxCpuCount -Command 'dotnet run --project a' -MaxCpuCount 6) -eq 'dotnet run --project a')

    # Exit-code honesty of the real gate (run-in-slot.ps1): each command runs through the gate with a temp lanes root.
    $gate = Join-Path $PSScriptRoot 'run-in-slot.ps1'
    $gateLanes = Join-Path $base 'gate-lanes'; $gateWork = Join-Path $base 'gate-work'
    New-Item -ItemType Directory -Force $gateLanes, $gateWork | Out-Null
    $gateCases = @(
        @{ Name = 'throw'; Cmd = "throw 'x'"; Want = 'nonzero' },
        @{ Name = 'terminating cmdlet error'; Cmd = 'Get-Item C:/definitely/missing -ErrorAction Stop'; Want = 'nonzero' },
        @{ Name = 'native exit 3'; Cmd = 'cmd /c exit 3'; Want = 3 },
        @{ Name = 'native exit 3 then success stays failed'; Cmd = 'cmd /c exit 3; Write-Output ok'; Want = 3 },
        @{ Name = 'native success then PowerShell failure'; Cmd = 'cmd /c exit 0; Get-Item C:/definitely/missing'; Want = 1 },
        @{ Name = 'non-terminating cmdlet failure, no native command'; Cmd = 'Get-Item C:/definitely/missing'; Want = 1 },
        @{ Name = 'unknown command then success'; Cmd = 'nonexistentcmd_zz; Write-Output ok'; Want = 'nonzero' },
        @{ Name = '.NET exception then native success'; Cmd = "[IO.File]::ReadAllText('C:/definitely/missing'); cmd /c exit 0"; Want = 'nonzero' },
        @{ Name = 'native failure then throw keeps native code'; Cmd = "cmd /c exit 3; throw 'x'"; Want = 3 },
        @{ Name = 'last statement SilentlyContinue error'; Cmd = 'Get-Item C:/definitely/missing -ErrorAction SilentlyContinue'; Want = 1 },
        @{ Name = 'plain success, no native command'; Cmd = 'Write-Output ok'; Want = 0 },
        @{ Name = 'native exit 0'; Cmd = 'cmd /c exit 0'; Want = 0 })
    foreach ($c in $gateCases) {
        $null = & pwsh -NoProfile -File $gate -Lane 'selftest' -WorkDir $gateWork -Command $c.Cmd -LanesRoot $gateLanes -MinAvailableMB 1 2>&1
        $rc = $LASTEXITCODE
        $ok = if ($c.Want -eq 'nonzero') { $rc -ne 0 } else { $rc -eq $c.Want }
        Check "gate exit: $($c.Name) (got $rc, want $($c.Want))" $ok
    }
    $longWork = Join-Path $base ('w' * 250)
    $null = & pwsh -NoProfile -File $gate -Lane 'selftest' -WorkDir $longWork -Command 'Write-Output ok' -LanesRoot $gateLanes -MinAvailableMB 1 2>&1
    Check "gate refuses a WorkDir over 247 characters (got $LASTEXITCODE)" ($LASTEXITCODE -ne 0)
}
finally {
    Remove-Item -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue
}
"slot-env-selftest: passed=$script:pass failed=$script:fail"
exit ([int]($script:fail -gt 0))
