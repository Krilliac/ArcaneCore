# Shared helpers of the code-quality scripts (mutate.ps1, decompile.ps1, jit-disasm.ps1). Dot-source this file; it defines functions
# only and does nothing else. PowerShell 7. The Get-/Test-/ConvertTo- functions are pure (no tool is run, no file is written) so
# tests/ArcaneCore.World.Tests/Ops/QualityScriptTests.cs can call them directly; Get-QualityProcessTable reads the OS process list,
# and Add-QualityDescendants, Stop-QualityTracked and Invoke-QualityProcess watch, start or stop processes.

Set-StrictMode -Version Latest

function Get-QualityMutationSummary {
    <#
    .SYNOPSIS
      Reads a Stryker mutation-testing-elements report (mutation-report.json, already parsed with ConvertFrom-Json) and returns
      { Killed, Survived, NoCoverage, Timeout, CompileError, Ignored, Other, Tested, Score, Survivors }.
      Tested = Killed + Timeout + Survived + NoCoverage, the mutants that could be judged; CompileError and Ignored mutants are not
      counted. Score = (Killed + Timeout) / Tested * 100, or $null when Tested is 0 (a run with nothing judged has no score).
      PerFile = one { File, Killed, Survived, NoCoverage, Timeout, Score } per file with a judged mutant.
      Survivors = the Survived and NoCoverage mutants as { File, Line, Column, Status, Mutator, Original, Mutated }, ordered by file
      and position; File is made relative to -Root when that is given and the report names absolute paths. Original is the source text the mutant's location covers, Mutated its replacement, both with runs of white space
      collapsed and cut to 80 characters.
    #>
    param([Parameter(Mandatory)] $Report, [string]$Root)
    $counts = [ordered]@{ Killed = 0; Survived = 0; NoCoverage = 0; Timeout = 0; CompileError = 0; Ignored = 0; Other = 0 }
    $survivors = [System.Collections.Generic.List[object]]::new()
    $perFile = [System.Collections.Generic.List[object]]::new()
    foreach ($fileProperty in $Report.files.PSObject.Properties) {
        $file = $fileProperty.Value
        $lines = ([string]$file.source) -split "`r?`n"
        $displayName = ConvertTo-QualityDisplayPath -Name ([string]$fileProperty.Name) -Root $Root
        $own = [ordered]@{ Killed = 0; Survived = 0; NoCoverage = 0; Timeout = 0 }
        foreach ($mutant in $file.mutants) {
            $status = [string]$mutant.status
            if ($counts.Contains($status)) { $counts[$status]++ } else { $counts['Other']++ }
            if ($own.Contains($status)) { $own[$status]++ }
            if ($status -ne 'Survived' -and $status -ne 'NoCoverage') { continue }
            $replacement = if ($mutant.PSObject.Properties['replacement']) { [string]$mutant.replacement } else { '' }
            $survivors.Add([pscustomobject]@{
                File     = $displayName
                Line     = [int]$mutant.location.start.line
                Column   = [int]$mutant.location.start.column
                Status   = $status
                Mutator  = [string]$mutant.mutatorName
                Original = Format-QualitySnippet (Get-QualitySourceSpan -Lines $lines -Location $mutant.location)
                Mutated  = Format-QualitySnippet $replacement
            })
        }
        $judged = $own.Killed + $own.Timeout + $own.Survived + $own.NoCoverage
        if ($judged -gt 0) {
            $perFile.Add([pscustomobject]@{ File = $displayName; Killed = $own.Killed; Survived = $own.Survived; NoCoverage = $own.NoCoverage; Timeout = $own.Timeout; Score = [math]::Round(($own.Killed + $own.Timeout) * 100.0 / $judged, 2) })
        }
    }
    $tested = $counts.Killed + $counts.Timeout + $counts.Survived + $counts.NoCoverage
    $score = if ($tested -eq 0) { $null } else { [math]::Round(($counts.Killed + $counts.Timeout) * 100.0 / $tested, 2) }
    [pscustomobject]@{
        Killed = $counts.Killed; Survived = $counts.Survived; NoCoverage = $counts.NoCoverage; Timeout = $counts.Timeout
        CompileError = $counts.CompileError; Ignored = $counts.Ignored; Other = $counts.Other
        Tested = $tested; Score = $score
        PerFile = @($perFile | Sort-Object File)
        Survivors = @($survivors | Sort-Object File, Line, Column)
    }
}

function ConvertTo-QualityDisplayPath {
    <# .SYNOPSIS A report path with forward slashes, relative to -Root when it is absolute and a root is given. #>
    param([Parameter(Mandatory)] [string]$Name, [string]$Root)
    if ($Root -and [IO.Path]::IsPathRooted($Name)) { $Name = [IO.Path]::GetRelativePath($Root, $Name) }
    $Name.Replace('\', '/')
}

function Get-QualitySourceSpan {
    <#
    .SYNOPSIS
      The text of a mutation-testing-elements location ({start:{line,column}, end:{line,column}}, 1-based, the end column one past
      the last character) out of the file's lines.
    #>
    param([Parameter(Mandatory)] [AllowEmptyString()] [string[]]$Lines, [Parameter(Mandatory)] $Location)
    $startLine = [int]$Location.start.line; $endLine = [int]$Location.end.line
    $startColumn = [int]$Location.start.column; $endColumn = [int]$Location.end.column
    if ($startLine -lt 1 -or $endLine -gt $Lines.Count -or $endLine -lt $startLine) { return '' }
    $parts = for ($line = $startLine; $line -le $endLine; $line++) {
        $text = $Lines[$line - 1]
        $from = if ($line -eq $startLine) { [math]::Min([math]::Max($startColumn - 1, 0), $text.Length) } else { 0 }
        $to = if ($line -eq $endLine) { [math]::Min([math]::Max($endColumn - 1, $from), $text.Length) } else { $text.Length }
        $text.Substring($from, $to - $from)
    }
    $parts -join ' '
}

function Format-QualitySnippet {
    <# .SYNOPSIS Collapses runs of white space to one space and cuts the text to 80 characters (an ellipsis marks a cut). #>
    param([AllowEmptyString()] [string]$Text)
    $flat = ($Text -replace '\s+', ' ').Trim()
    if ($flat.Length -gt 80) { $flat.Substring(0, 77) + '...' } else { $flat }
}

function ConvertTo-QualityMutateGlob {
    <#
    .SYNOPSIS
      A Stryker --mutate glob is relative to the project directory. A glob written from the repository root (src/<Project>/...) has
      the project's repo-relative directory stripped off; a leading '!' (exclude) is kept. Backslashes become slashes.
    #>
    param([Parameter(Mandatory)] [string]$Glob, [Parameter(Mandatory)] [string]$ProjectRelativeDirectory)
    $negate = $Glob.StartsWith('!')
    $body = $Glob.TrimStart('!').Replace('\', '/')
    $prefix = $ProjectRelativeDirectory.Replace('\', '/').Trim('/') + '/'
    if ($body.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $body = $body.Substring($prefix.Length) }
    if ($negate) { '!' + $body } else { $body }
}

function Get-QualityTargetFramework {
    <# .SYNOPSIS The value of the first <TargetFramework> (or <TargetFrameworks>) element in project or props XML text, XML comments ignored; $null when there is none. #>
    param([AllowEmptyString()] [AllowNull()] [string]$Xml)
    if (-not $Xml) { return $null }
    $live = [regex]::Replace($Xml, '<!--.*?-->', '', [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $m = [regex]::Match($live, '<TargetFrameworks?>\s*([^<]*?)\s*</TargetFrameworks?>')
    if ($m.Success) { $m.Groups[1].Value } else { $null }
}

function Test-QualityJitOutput {
    <#
    .SYNOPSIS
      Whether the contents of a DOTNET_JitStdOutFile hold a disassembly. DOTNET_JitDisasm writes a listing ("; Assembly listing for
      method <name>") only for a method that matches its pattern, so a listing in the file is a listing of the requested method. A file
      that is not empty is not enough: with DOTNET_JitDisasmSummary=1 it fills with "JIT compiled <method> [Tier0, IL size=..]" lines for
      every method the JIT compiled, whether or not the requested one matched (or was inlined into its caller and so never compiled alone).
      Returns { Ok, Listings, Names, SummaryLines, Reason }; Reason says why Ok is false and is empty otherwise.
    #>
    param([AllowEmptyString()] [AllowNull()] [string]$Text, [Parameter(Mandatory)] [string]$Method)
    if ($null -eq $Text) { $Text = '' }
    $names = @([regex]::Matches($Text, '(?m)^; Assembly listing for method (?<name>[^\r\n(]*)') | ForEach-Object { $_.Groups['name'].Value.Trim() })
    $summaryLines = [regex]::Matches($Text, '(?m)^JIT compiled ').Count
    $reason = ''
    if ($names.Count -eq 0) {
        $reason = if ($Text.Trim().Length -eq 0) { "the JIT wrote no output for '$Method'" }
        elseif ($summaryLines -gt 0) { "the JIT compiled $summaryLines method(s) (the summary lines) but wrote no listing for '$Method'" }
        else { "the output holds no '; Assembly listing for method' for '$Method'" }
        $reason += ': the pattern matched no compiled method (inlined into its caller: name the caller, or use a Debug build, which does not inline; precompiled ReadyToRun code; never called by the tests; or a wrong Class:Method name).'
    }
    [pscustomobject]@{ Ok = ($names.Count -gt 0); Listings = $names.Count; Names = $names; SummaryLines = $summaryLines; Reason = $reason }
}

function Test-QualityMutationRun {
    <#
    .SYNOPSIS
      Whether a finished Stryker run judged anything. -Summary is the result of Get-QualityMutationSummary, -LogText the run's stryker.log.
      Not Ok when the log says "Number of tests found: 0" (no test ran, so nothing could kill a mutant), when no mutant was judged, or
      when every judged mutant is NoCoverage (Killed + Timeout + Survived is 0: no test reaches the mutated code, which points at a
      filter or project mix-up and is not a result). Returns { Ok, Reason }.
    #>
    param([Parameter(Mandatory)] $Summary, [AllowEmptyString()] [AllowNull()] [string]$LogText)
    if ($LogText -match 'Number of tests found:\s*0(?!\d)') {
        return [pscustomobject]@{ Ok = $false; Reason = 'Stryker found no tests (log: "Number of tests found: 0"): the -TestFilter matched no test, or the test project has none.' }
    }
    if ($Summary.Tested -eq 0) {
        return [pscustomobject]@{ Ok = $false; Reason = 'no mutant was judged (Stryker found no mutants in the files, or no test ran).' }
    }
    if ($Summary.Killed + $Summary.Timeout + $Summary.Survived -eq 0) {
        return [pscustomobject]@{ Ok = $false; Reason = "all $($Summary.NoCoverage) judged mutants are NoCoverage: no test reached the mutated code (check -TestFilter and -TestProject)." }
    }
    [pscustomobject]@{ Ok = $true; Reason = '' }
}

function Get-QualityIlBlock {
    <#
    .SYNOPSIS
      Cuts one type (or, with -Method, every method of that name in it) out of the IL text ilspycmd prints for a whole assembly.
      -Type is the full name, a nested type as Outer+Inner (ILSpy writes a top-level type with its namespace and a nested type inside
      its outer type's block, by its own name). The class lines are followed as a stack, so a type is matched by its whole path and a
      same-named type elsewhere is not. Returns { TypeSeen, MethodSeen, Block }.
    #>
    param([Parameter(Mandatory)] [AllowEmptyString()] $Lines, [Parameter(Mandatory)] [string]$Type, [string]$Method)
    $target = $Type.Replace("'", '')
    $stack = [System.Collections.Generic.List[string]]::new()
    $block = [System.Collections.Generic.List[string]]::new()
    $methodLines = [System.Collections.Generic.List[string]]::new()
    $inType = $false; $inMethod = $false; $typeSeen = $false; $methodSeen = $false; $targetDepth = 0
    foreach ($line in $Lines) {
        if ($line -match '^\s*\.class\b') {
            $name = if ($line -match "\s(?<name>'[^']*'|[^\s'<]+)(?:<[^>]*>)?\s*$") { $Matches['name'].Replace("'", '') } else { '' }
            $stack.Add($name)
            if (-not $inType -and ($stack -join '+') -eq $target) { $inType = $true; $typeSeen = $true; $targetDepth = $stack.Count }
            if ($inType -and -not $Method) { $block.Add($line) }
            continue
        }
        if ($line -match '^\s*\} // end of class ') {
            if ($inType -and -not $Method) { $block.Add($line) }
            if ($inType -and $stack.Count -eq $targetDepth) { break }
            if ($stack.Count -gt 0) { $stack.RemoveAt($stack.Count - 1) }
            continue
        }
        if (-not $inType) { continue }
        if (-not $Method) { $block.Add($line); continue }
        if ($stack.Count -ne $targetDepth) { continue }
        if ($line -match '^\s*\.method\b') { $inMethod = $true; $methodLines.Clear() }
        if ($inMethod) {
            $methodLines.Add($line)
            if ($line -match ('^\s*\} // end of method .*::' + [regex]::Escape($Method) + '\s*$')) { $block.AddRange($methodLines); $block.Add(''); $methodSeen = $true; $inMethod = $false }
            elseif ($line -match '^\s*\} // end of method ') { $inMethod = $false }
        }
    }
    [pscustomobject]@{ TypeSeen = $typeSeen; MethodSeen = $methodSeen; Block = @($block) }
}

function ConvertTo-QualityArgumentString {
    <# .SYNOPSIS One command-line string for Start-Process -ArgumentList: an argument with white space or a quote is quoted (embedded quotes escaped). #>
    param([Parameter(Mandatory)] [AllowEmptyString()] [string[]]$Arguments)
    ($Arguments | ForEach-Object {
        if ($_.Length -gt 0 -and $_ -notmatch '[\s"]') { $_ } else { '"' + ($_ -replace '(\\*)"', '$1$1\"' -replace '(\\+)$', '$1$1') + '"' }
    }) -join ' '
}

function Get-QualityDescendantIds {
    <#
    .SYNOPSIS
      The processes in -Table (rows of { ProcessId, ParentProcessId, Start }) that descend from -RootId, as { ProcessId, Start }. The
      root need not be alive: a child keeps its parent's id when the parent exits. A row only counts as a child when it started no
      earlier than its parent (2 s of clock slack), so a recycled process id is not mistaken for the parent. -RootStart is the root's start.
    #>
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [object[]]$Table, [Parameter(Mandatory)] [int]$RootId, [datetime]$RootStart = [datetime]::MinValue)
    $slack = [TimeSpan]::FromSeconds(2)
    $starts = @{ $RootId = $RootStart }
    $found = [System.Collections.Generic.List[object]]::new()
    do {
        $grew = $false
        foreach ($row in $Table) {
            if ($starts.ContainsKey([int]$row.ProcessId) -or -not $starts.ContainsKey([int]$row.ParentProcessId)) { continue }
            $parentStart = $starts[[int]$row.ParentProcessId]
            if ($parentStart -ne [datetime]::MinValue -and $row.Start -lt ($parentStart - $slack)) { continue }
            $starts[[int]$row.ProcessId] = $row.Start
            $found.Add([pscustomobject]@{ ProcessId = [int]$row.ProcessId; Start = $row.Start })
            $grew = $true
        }
    } while ($grew)
    $found.ToArray()
}

function Get-QualityProcessTable {
    <# .SYNOPSIS Every running process as { ProcessId, ParentProcessId, Start, Name, CommandLine } (Start is MinValue where the platform gives none). Reads the OS process list only. #>
    if ($IsWindows) {
        Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, CreationDate, Name, CommandLine | ForEach-Object {
            [pscustomobject]@{ ProcessId = [int]$_.ProcessId; ParentProcessId = [int]$_.ParentProcessId; Start = [datetime]$_.CreationDate; Name = [string]$_.Name; CommandLine = [string]$_.CommandLine }
        }
    }
    else {
        & ps -A -o 'pid=,ppid=' | ForEach-Object {
            $p = $_.Trim() -split '\s+'
            if ($p.Count -eq 2) { [pscustomobject]@{ ProcessId = [int]$p[0]; ParentProcessId = [int]$p[1]; Start = [datetime]::MinValue; Name = ''; CommandLine = '' } }
        }
    }
}

function Add-QualityDescendants {
    <# .SYNOPSIS Adds the current descendants of -RootId to the -Tracked hashtable (id -> start). Call it while the root runs: once it has exited, its children are orphans that only the process table still ties to it. #>
    param([Parameter(Mandatory)] [hashtable]$Tracked, [Parameter(Mandatory)] [int]$RootId, [datetime]$RootStart = [datetime]::MinValue)
    $table = @(Get-QualityProcessTable)
    $byId = @{}
    foreach ($row in $table) { $byId[[int]$row.ProcessId] = $row }
    foreach ($d in (Get-QualityDescendantIds -Table $table -RootId $RootId -RootStart $RootStart)) {
        if (Test-QualitySharedBuildServer -Row $byId[$d.ProcessId]) { continue }
        $Tracked[$d.ProcessId] = $d.Start
    }
}

function Test-QualitySharedBuildServer {
    <#
    .SYNOPSIS
      Whether a process-table row is a build server another build may be using: the Roslyn compiler server (VBCSCompiler) or an MSBuild
      node ("/nodemode:", reused between builds unless MSBUILDDISABLENODEREUSE=1 or -nodeReuse:false). A server that a run of ours started
      with reuse off ends by itself, so leaving these alone costs nothing and never takes out another slot's build.
    #>
    param($Row)
    if ($null -eq $Row) { return $false }
    $name = if ($Row.PSObject.Properties['Name']) { [string]$Row.Name } else { '' }
    $command = if ($Row.PSObject.Properties['CommandLine']) { [string]$Row.CommandLine } else { '' }
    $name -match '^VBCSCompiler' -or $command -match 'VBCSCompiler' -or $command -match '/nodemode:\d|-nodemode:\d'
}

function Stop-QualityTracked {
    <# .SYNOPSIS Kills the processes of a -Tracked table that still run with the recorded start (an id reused by another process is left alone). Returns how many were stopped. #>
    param([Parameter(Mandatory)] [hashtable]$Tracked)
    $stopped = 0
    foreach ($id in @($Tracked.Keys)) {
        $process = Get-Process -Id $id -ErrorAction SilentlyContinue
        if (-not $process) { continue }
        $recorded = [datetime]$Tracked[$id]
        if ($recorded -ne [datetime]::MinValue) {
            try { if ([math]::Abs(($process.StartTime - $recorded).TotalSeconds) -gt 2) { continue } } catch { continue }
        }
        try { $process.Kill($true); $stopped++ } catch { }
    }
    $stopped
}

function Invoke-QualityProcess {
    <#
    .SYNOPSIS
      Runs -FilePath with -Arguments, output to -LogFile (stderr to <LogFile>.err), and waits at most -TimeoutSec. Output goes to files, not
      a pipe: a child the tool leaves running (vstest.console, testhost) holds a pipe open and a pipe reader would wait for it forever.
      When the process ends, normally or by the limit, every process that descended from it is stopped, except shared build servers
      (see Test-QualitySharedBuildServer). -Environment sets variables for the child only; -OnTick is called after every 3 s poll and once
      after the end (to show a growing log). Returns { ExitCode, TimedOut, Stopped } (ExitCode is -1 after a timeout).
    #>
    param([Parameter(Mandatory)] [string]$FilePath, [Parameter(Mandatory)] [string[]]$Arguments, [Parameter(Mandatory)] [string]$LogFile,
        [Parameter(Mandatory)] [int]$TimeoutSec, [string]$WorkingDirectory = (Get-Location).Path, [hashtable]$Environment, [scriptblock]$OnTick)
    $saved = @{}
    if (-not $Environment) { $Environment = @{} }
    foreach ($key in @($Environment.Keys)) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, [string]$Environment[$key])
    }
    try {
        $process = Start-Process $FilePath -ArgumentList (ConvertTo-QualityArgumentString $Arguments) -WorkingDirectory $WorkingDirectory -NoNewWindow -PassThru `
            -RedirectStandardOutput $LogFile -RedirectStandardError "$LogFile.err"
    }
    finally {
        foreach ($key in @($saved.Keys)) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
    }
    $start = try { $process.StartTime } catch { [datetime]::MinValue }
    $tracked = @{}
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    $timedOut = $false
    while (-not $process.WaitForExit(3000)) {
        Add-QualityDescendants -Tracked $tracked -RootId $process.Id -RootStart $start
        if ($OnTick) { & $OnTick }
        if ([DateTime]::UtcNow -gt $deadline) { $timedOut = $true; break }
    }
    Add-QualityDescendants -Tracked $tracked -RootId $process.Id -RootStart $start
    if ($timedOut) { try { $process.Kill() } catch { } } else { $process.WaitForExit() }
    $stopped = Stop-QualityTracked -Tracked $tracked
    if ($OnTick) { & $OnTick }
    [pscustomobject]@{ ExitCode = $(if ($timedOut) { -1 } else { $process.ExitCode }); TimedOut = $timedOut; Stopped = $stopped }
}
