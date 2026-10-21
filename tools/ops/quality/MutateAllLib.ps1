# Helpers of the whole-repo mutation driver (mutate-all.ps1) and its aggregator (mutate-report.ps1). Dot-source this file after QualityLib.ps1;
# it defines functions and one constant and nothing else. PowerShell 7. The functions here read the repository tree or a Stryker report and
# return values; only Save-MutateAllState and Write-MutateAllJson write a file (the state file / a batch summary).

Set-StrictMode -Version Latest

# Source projects in run order: small and fast first, then the big ones.
$script:MutateAllProjectOrder = @(
    'ArcaneCore.Cryptography', 'ArcaneCore.Protocol', 'ArcaneCore.Gateway', 'ArcaneCore.Realm', 'ArcaneCore.Kernel', 'ArcaneCore.Data',
    'ArcaneCore.Game', 'ArcaneCore.Playerbots', 'ArcaneCore.World.Contracts', 'ArcaneCore.World')

# Globs excluded from every batch (Stryker is told '!' + glob).
$script:MutateAllStandardExcludes = @('**/obj/**', '**/bin/**', '**/ThirdParty/**')

function Get-MutateAllProjectOrder { $script:MutateAllProjectOrder }
function Get-MutateAllStandardExcludes { $script:MutateAllStandardExcludes }

function Get-MutateAllProjectReferences {
    <# .SYNOPSIS The full paths of the projects a csproj references with <ProjectReference Include="...">. #>
    param([Parameter(Mandatory)] [string]$CsprojPath)
    $directory = Split-Path $CsprojPath -Parent
    $text = Get-Content -LiteralPath $CsprojPath -Raw
    foreach ($m in [regex]::Matches($text, '<ProjectReference\s+Include="([^"]+)"')) {
        [IO.Path]::GetFullPath((Join-Path $directory ($m.Groups[1].Value.Replace('\', '/'))))
    }
}

function Get-MutateAllTestMap {
    <#
    .SYNOPSIS
      Which test projects exercise each source project. Returns an ordered map: source project name -> { Direct = names of the test
      projects that reference it, Transitive = names of the test projects that reach it only through other references }.
      A test project is a tests/*/*.csproj that references Microsoft.NET.Test.Sdk (so ArcaneCore.Data.ProcessRunner is not one).
    #>
    param([Parameter(Mandatory)] [string]$Root)
    $graph = @{}
    foreach ($csproj in Get-ChildItem (Join-Path $Root 'src'), (Join-Path $Root 'tests') -Recurse -Filter '*.csproj' -File) {
        $graph[$csproj.FullName] = @(Get-MutateAllProjectReferences $csproj.FullName)
    }
    $testProjects = @(Get-ChildItem (Join-Path $Root 'tests') -Directory | ForEach-Object { Get-ChildItem $_.FullName -Filter '*.csproj' -File } |
            Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match 'Microsoft\.NET\.Test\.Sdk' })
    $map = [ordered]@{}
    foreach ($test in $testProjects) {
        $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $queue = [System.Collections.Generic.Queue[string]]::new()
        $direct = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($r in $graph[$test.FullName]) { [void]$direct.Add($r); if ($seen.Add($r)) { $queue.Enqueue($r) } }
        while ($queue.Count -gt 0) {
            $next = $queue.Dequeue()
            if (-not $graph.ContainsKey($next)) { continue }
            foreach ($r in $graph[$next]) { if ($seen.Add($r)) { $queue.Enqueue($r) } }
        }
        foreach ($reference in $seen) {
            $name = [IO.Path]::GetFileNameWithoutExtension($reference)
            if (-not $map.Contains($name)) { $map[$name] = [pscustomobject]@{ Direct = [System.Collections.Generic.List[string]]::new(); Transitive = [System.Collections.Generic.List[string]]::new() } }
            $testName = [IO.Path]::GetFileNameWithoutExtension($test.Name)
            if ($direct.Contains($reference)) { $map[$name].Direct.Add($testName) } else { $map[$name].Transitive.Add($testName) }
        }
    }
    $map
}

function Resolve-MutateAllTestProject {
    <#
    .SYNOPSIS
      The one test project a source project is mutated against: the override if given; else <Project>.Tests when it references the
      project directly; else the first direct referencer; else the first test project that reaches it through other references.
      Returns $null when no test project reaches the project.
    #>
    param([Parameter(Mandatory)] [string]$Project, [Parameter(Mandatory)] $Map, [hashtable]$Override)
    if ($Override -and $Override.ContainsKey($Project)) { return [string]$Override[$Project] }
    if (-not $Map.Contains($Project)) { return $null }
    $entry = $Map[$Project]
    $direct = @($entry.Direct | Sort-Object)
    if ($direct -contains "$Project.Tests") { return "$Project.Tests" }
    if ($direct.Count -gt 0) { return $direct[0] }
    $transitive = @($entry.Transitive | Sort-Object)
    if ($transitive -contains 'ArcaneCore.World.Tests') { return 'ArcaneCore.World.Tests' }   # the broadest suite; the others test one daemon
    if ($transitive.Count -gt 0) { return $transitive[0] }
    $null
}

function Get-MutateAllSourceFiles {
    <#
    .SYNOPSIS
      Every .cs file of a project directory outside obj, bin and ThirdParty, as { Rel (forward slashes, relative to the project
      directory), Lines (non-blank lines), Eligible, Reason }. A file is not eligible when it is generated: *.g.cs, *.generated.cs,
      *.Designer.cs, AssemblyInfo.cs, or an '<auto-generated' marker in its first 20 lines.
    #>
    param([Parameter(Mandatory)] [string]$ProjectDirectory)
    $skipDirectory = '(^|/)(obj|bin|ThirdParty|node_modules|\.vs|\.stryker-tmp|StrykerOutput)/'
    $generatedName = '\.(g|g\.i|generated|designer)\.cs$|(^|/)AssemblyInfo\.cs$'
    foreach ($file in Get-ChildItem -LiteralPath $ProjectDirectory -Recurse -Filter '*.cs' -File | Sort-Object FullName) {
        $rel = [IO.Path]::GetRelativePath($ProjectDirectory, $file.FullName).Replace('\', '/')
        if ($rel -match $skipDirectory) { continue }
        $lines = [IO.File]::ReadAllLines($file.FullName)
        $count = 0
        foreach ($l in $lines) { if (-not [string]::IsNullOrWhiteSpace($l)) { $count++ } }
        $reason = $null
        if ($rel -match $generatedName) { $reason = 'generated file name' }
        else {
            $head = $lines | Select-Object -First 20
            if ($head -match '<auto-?generated') { $reason = 'auto-generated header' }
        }
        [pscustomobject]@{ Rel = $rel; Lines = $count; Eligible = ($null -eq $reason); Reason = $reason }
    }
}

function New-MutateAllUnit {
    param([string]$Name, [string[]]$Include, [string[]]$Exclude, [int]$Files, [int]$Lines)
    [pscustomobject]@{ Parts = @($Name); Include = @($Include); Exclude = @($Exclude); Files = $Files; Lines = $Lines }
}

function Get-MutateAllDirUnits {
    <# .SYNOPSIS The units of one directory: the whole directory when it fits the caps, else its own files in chunks plus the units of each subdirectory. #>
    param([string]$Dir, [Parameter(Mandatory)] $Eligible, [Parameter(Mandatory)] $Ineligible, [int]$MaxFiles, [int]$MaxLines)
    $prefix = if ($Dir) { "$Dir/" } else { '' }
    $inTree = @($Eligible | Where-Object { $_.Rel.StartsWith($prefix, [StringComparison]::Ordinal) })
    if ($inTree.Count -eq 0) { return @() }
    $lines = [int](($inTree | Measure-Object Lines -Sum).Sum)
    $label = if ($Dir) { $Dir } else { '(project root)' }
    if ($inTree.Count -le $MaxFiles -and $lines -le $MaxLines) {
        $glob = if ($Dir) { "$Dir/**/*.cs" } else { '**/*.cs' }
        $exclude = @($Ineligible | Where-Object { $_.Rel.StartsWith($prefix, [StringComparison]::Ordinal) } | ForEach-Object { $_.Rel })
        return @(New-MutateAllUnit -Name $label -Include @($glob) -Exclude $exclude -Files $inTree.Count -Lines $lines)
    }
    $units = [System.Collections.Generic.List[object]]::new()
    $direct = @($inTree | Where-Object { $_.Rel.IndexOf('/', $prefix.Length) -lt 0 })
    $chunks = [System.Collections.Generic.List[object]]::new()
    $chunk = [System.Collections.Generic.List[object]]::new(); $chunkLines = 0
    foreach ($f in $direct) {
        if ($chunk.Count -gt 0 -and ($chunk.Count + 1 -gt $MaxFiles -or $chunkLines + $f.Lines -gt $MaxLines)) {
            $chunks.Add([pscustomobject]@{ Files = $chunk.ToArray(); Lines = $chunkLines })
            $chunk = [System.Collections.Generic.List[object]]::new(); $chunkLines = 0
        }
        $chunk.Add($f); $chunkLines += $f.Lines
    }
    if ($chunk.Count -gt 0) { $chunks.Add([pscustomobject]@{ Files = $chunk.ToArray(); Lines = $chunkLines }) }
    for ($i = 0; $i -lt $chunks.Count; $i++) {
        $base = if ($Dir) { "$Dir (own files" } else { '(project root files' }
        $name = if ($chunks.Count -gt 1) { "$base #$($i + 1))" } else { "$base)" }
        $units.Add((New-MutateAllUnit -Name $name -Include @($chunks[$i].Files | ForEach-Object { $_.Rel }) -Exclude @() -Files $chunks[$i].Files.Count -Lines $chunks[$i].Lines))
    }
    $children = @($inTree | Where-Object { $_.Rel.IndexOf('/', $prefix.Length) -ge 0 } |
            ForEach-Object { $_.Rel.Substring($prefix.Length).Split('/')[0] } | Sort-Object -Unique)
    foreach ($child in $children) {
        $childDir = if ($Dir) { "$Dir/$child" } else { $child }
        foreach ($u in (Get-MutateAllDirUnits -Dir $childDir -Eligible $Eligible -Ineligible $Ineligible -MaxFiles $MaxFiles -MaxLines $MaxLines)) { $units.Add($u) }
    }
    $units.ToArray()
}

function Merge-MutateAllUnits {
    <# .SYNOPSIS Packs consecutive small units into one while the sum stays inside both caps (fewer batches, fewer Stryker start-ups). #>
    param([Parameter(Mandatory)] $Units, [int]$MaxFiles, [int]$MaxLines)
    $result = [System.Collections.Generic.List[object]]::new()
    $current = $null
    foreach ($u in $Units) {
        if ($null -ne $current -and $current.Files + $u.Files -le $MaxFiles -and $current.Lines + $u.Lines -le $MaxLines) {
            $current = [pscustomobject]@{
                Parts = @($current.Parts + $u.Parts); Include = @($current.Include + $u.Include); Exclude = @($current.Exclude + $u.Exclude)
                Files = $current.Files + $u.Files; Lines = $current.Lines + $u.Lines
            }
        }
        else {
            if ($null -ne $current) { $result.Add($current) }
            $current = $u
        }
    }
    if ($null -ne $current) { $result.Add($current) }
    $result.ToArray()
}

function New-MutateAllPlan {
    <#
    .SYNOPSIS
      The batch plan of the repository: one entry per batch { Id, Project, Name, TestProject, Include, Exclude, Files, Lines, Status... }
      in run order (project order of Get-MutateAllProjectOrder, directories alphabetical inside a project). A batch is one directory
      (or several small neighbours) of one source project, at most -MaxFiles files and -MaxLines non-blank lines; a bigger directory is
      split into its own files (in chunks) and its subdirectories. Projects without a test project reaching them are returned in
      Unmapped and get no batch.
    #>
    param(
        [Parameter(Mandatory)] [string]$Root, [int]$MaxFiles = 40, [int]$MaxLines = 6000,
        [string[]]$Projects, [hashtable]$TestOverride
    )
    $map = Get-MutateAllTestMap -Root $Root
    $batches = [System.Collections.Generic.List[object]]::new()
    $unmapped = [System.Collections.Generic.List[string]]::new()
    $skippedFiles = [System.Collections.Generic.List[string]]::new()
    foreach ($project in $script:MutateAllProjectOrder) {
        $short = $project -replace '^ArcaneCore\.', ''
        if ($Projects -and ($Projects -notcontains $project) -and ($Projects -notcontains $short)) { continue }
        $directory = Join-Path $Root "src/$project"
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) { continue }
        $test = Resolve-MutateAllTestProject -Project $project -Map $map -Override $TestOverride
        if (-not $test) { $unmapped.Add($project); continue }
        $files = @(Get-MutateAllSourceFiles -ProjectDirectory $directory)
        $eligible = @($files | Where-Object Eligible)
        $ineligible = @($files | Where-Object { -not $_.Eligible })
        foreach ($f in $ineligible) { $skippedFiles.Add("src/$project/$($f.Rel) ($($f.Reason))") }
        $units = @(Get-MutateAllDirUnits -Dir '' -Eligible $eligible -Ineligible $ineligible -MaxFiles $MaxFiles -MaxLines $MaxLines)
        $units = @(Merge-MutateAllUnits -Units $units -MaxFiles $MaxFiles -MaxLines $MaxLines)
        $n = 0
        foreach ($u in $units) {
            $n++
            $name = if ($u.Parts.Count -eq 1) { $u.Parts[0] } else { "$($u.Parts[0]) (+$($u.Parts.Count - 1) more)" }
            $batches.Add([ordered]@{
                    id = ('{0}-{1:D3}' -f $short, $n); project = $project; name = $name; testProject = $test
                    parts = @($u.Parts); include = @($u.Include); exclude = @($u.Exclude); files = $u.Files; lines = $u.Lines
                    status = 'pending'; attempts = 0; head = $null; dirty = $null; startedUtc = $null; endedUtc = $null; durationSec = $null
                    outDir = $null; report = $null; summary = $null; counts = $null; reason = $null; exitCode = $null
                })
        }
    }
    [pscustomobject]@{ Batches = $batches.ToArray(); Unmapped = $unmapped.ToArray(); SkippedFiles = $skippedFiles.ToArray(); TestMap = $map }
}

function Get-MutateAllBatchFileArguments {
    <# .SYNOPSIS The -Files value of mutate.ps1 for a batch: its include globs, its excluded generated files and the standard exclusions, comma separated. #>
    param([Parameter(Mandatory)] $Batch)
    $items = @($Batch.include) + @($Batch.exclude | ForEach-Object { "!$_" }) + @($script:MutateAllStandardExcludes | ForEach-Object { "!$_" })
    $items -join ','
}

function Get-MutateAllBatchSummary {
    <#
    .SYNOPSIS
      Reads a parsed Stryker mutation-report.json and returns { Counts, Files, Survivors }.
      Counts = Killed, Survived, NoCoverage, Timeout, CompileError, Ignored, Other (RuntimeError and anything unknown), Tested
      (Killed + Timeout + Survived + NoCoverage). Files = one { File, Dir, Killed, Survived, NoCoverage, Timeout, CompileError, Ignored,
      Other } per file. Survivors = { File, Line, Column, Status, Mutator, Original, Mutated } of every Survived and NoCoverage mutant.
      File names are made relative to -Root (forward slashes). The Stryker report names files relative to the project directory when
      -ProjectRelativeDirectory is given, the batch's src/<Project> folder is prefixed, so a File is always repo-relative.
    #>
    param([Parameter(Mandatory)] $Report, [Parameter(Mandatory)] [string]$Root, [Parameter(Mandatory)] [string]$ProjectRelativeDirectory)
    $names = 'Killed', 'Survived', 'NoCoverage', 'Timeout', 'CompileError', 'Ignored'
    $counts = [ordered]@{ Killed = 0; Survived = 0; NoCoverage = 0; Timeout = 0; CompileError = 0; Ignored = 0; Other = 0 }
    $files = [System.Collections.Generic.List[object]]::new()
    $survivors = [System.Collections.Generic.List[object]]::new()
    $prefix = $ProjectRelativeDirectory.Replace('\', '/').Trim('/')
    foreach ($property in $Report.files.PSObject.Properties) {
        $file = $property.Value
        $display = ConvertTo-QualityDisplayPath -Name ([string]$property.Name) -Root $Root
        if (-not $display.StartsWith('src/', [StringComparison]::OrdinalIgnoreCase) -and -not $display.StartsWith('tests/', [StringComparison]::OrdinalIgnoreCase)) { $display = "$prefix/$display" }
        $slash = $display.LastIndexOf('/')
        $own = [ordered]@{ File = $display; Dir = $display.Substring(0, $slash); Killed = 0; Survived = 0; NoCoverage = 0; Timeout = 0; CompileError = 0; Ignored = 0; Other = 0 }
        $sourceLines = $null
        foreach ($mutant in $file.mutants) {
            $status = [string]$mutant.status
            $key = if ($names -contains $status) { $status } else { 'Other' }
            $counts[$key]++; $own[$key]++
            if ($status -ne 'Survived' -and $status -ne 'NoCoverage') { continue }
            if ($null -eq $sourceLines) { $sourceLines = ([string]$file.source) -split "`r?`n" }
            $replacement = if ($mutant.PSObject.Properties['replacement']) { [string]$mutant.replacement } else { '' }
            $survivors.Add([pscustomobject]@{
                    File = $display; Line = [int]$mutant.location.start.line; Column = [int]$mutant.location.start.column; Status = $status
                    Mutator = [string]$mutant.mutatorName
                    Original = Format-QualitySnippet (Get-QualitySourceSpan -Lines $sourceLines -Location $mutant.location)
                    Mutated = Format-QualitySnippet $replacement
                })
        }
        $files.Add([pscustomobject]$own)
    }
    $counts['Tested'] = $counts.Killed + $counts.Timeout + $counts.Survived + $counts.NoCoverage
    [pscustomobject]@{ Counts = [pscustomobject]$counts; Files = $files.ToArray(); Survivors = $survivors.ToArray() }
}

function Get-MutateAllJudgement {
    <#
    .SYNOPSIS
      Decides whether a batch that ran is 'done' or 'error'. A batch that judged no mutant, or in which no test covered any mutant
      (everything NoCoverage, which is what a filter that matched no test looks like), is an error with a reason, never done.
      Returns { Status, Reason }.
    #>
    param([Parameter(Mandatory)] $Counts)
    if ($Counts.Tested -eq 0) { return [pscustomobject]@{ Status = 'error'; Reason = 'judged 0 mutants (no mutant in the files, or Stryker judged none)' } }
    if ($Counts.Killed + $Counts.Timeout + $Counts.Survived -eq 0) {
        return [pscustomobject]@{ Status = 'error'; Reason = "no test covered any of the $($Counts.NoCoverage) mutants (0 tests matched)" }
    }
    [pscustomobject]@{ Status = 'done'; Reason = $null }
}

function Get-MutateAllScore {
    <# .SYNOPSIS (Killed + Timeout) / (Killed + Timeout + Survived + NoCoverage) * 100 with two decimals, or $null when nothing was judged. #>
    param([double]$Killed, [double]$Timeout, [double]$Survived, [double]$NoCoverage)
    $tested = $Killed + $Timeout + $Survived + $NoCoverage
    if ($tested -eq 0) { return $null }
    [math]::Round(($Killed + $Timeout) * 100.0 / $tested, 2)
}

function Write-MutateAllJson {
    <# .SYNOPSIS Writes an object as JSON atomically (temp file in the same folder, then a replacing move). #>
    param([Parameter(Mandatory)] $Object, [Parameter(Mandatory)] [string]$Path, [int]$Depth = 10)
    $temp = "$Path.tmp"
    $Object | ConvertTo-Json -Depth $Depth -Compress | Set-Content -LiteralPath $temp -Encoding utf8
    Move-Item -LiteralPath $temp -Destination $Path -Force
}

function Save-MutateAllState {
    param([Parameter(Mandatory)] $State, [Parameter(Mandatory)] [string]$RunDirectory)
    Write-MutateAllJson -Object $State -Path (Join-Path $RunDirectory 'state.json') -Depth 8
}

function Read-MutateAllState {
    param([Parameter(Mandatory)] [string]$RunDirectory)
    $path = Join-Path $RunDirectory 'state.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "no state.json in $RunDirectory" }
    Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
}

function Format-MutateAllDuration {
    param([double]$Seconds)
    $span = [TimeSpan]::FromSeconds([math]::Max($Seconds, 0))
    if ($span.TotalHours -ge 48) { '{0:N1} days' -f $span.TotalDays }
    elseif ($span.TotalHours -ge 1) { '{0}h {1:D2}m' -f [int][math]::Floor($span.TotalHours), $span.Minutes }
    elseif ($span.TotalMinutes -ge 1) { '{0}m {1:D2}s' -f [int][math]::Floor($span.TotalMinutes), $span.Seconds }
    else { '{0}s' -f [int]$span.TotalSeconds }
}
