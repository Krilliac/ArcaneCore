# Shared helpers of the code-quality scripts (mutate.ps1, jit-disasm.ps1). Dot-source this file; it defines functions only and does
# nothing else. PowerShell 7. Every function here is pure (no tool is run, no file is written) so
# tests/ArcaneCore.World.Tests/Ops/QualityScriptTests.cs can call them directly.

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
