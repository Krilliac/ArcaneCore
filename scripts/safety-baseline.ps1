<#
.SYNOPSIS
  Rewrite analyzers/safety-baseline.txt from the safety analyzer's current findings (docs/ops/memory-safety.md).

.DESCRIPTION
  The baseline is the allow-list of ARC000x findings that predate a rule. It may only shrink, so run this to RECORD a new
  rule's existing violations or after FIXING some (the analyzer reports ARC9001 for an entry that now allows more than the code has).
  Never run it to silence a finding you just introduced: fix the code instead.

  1. Empties the baseline (a header only), 2. builds the solution with warnings not treated as errors, 3. collects every distinct
  ARC0001-ARC0006 finding with the baseline line the analyzer prints in its message, 4. writes them back, grouped and counted.
  On any failure the previous baseline is restored.

.PARAMETER Solution  Solution to build (default ArcaneCore.slnx at the repository root).
.PARAMETER Configuration  Build configuration (default Release).

.EXAMPLE
  powershell -NoProfile -File scripts/safety-baseline.ps1
#>
[CmdletBinding()]
param(
    [string]$Solution = 'ArcaneCore.slnx',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$baselinePath = Join-Path $root 'analyzers/safety-baseline.txt'
$header = @(
    '# Safety-analyzer baseline (docs/ops/memory-safety.md): violations that existed when a rule was introduced.',
    '# Format: RULE|repo-relative path|Type.Member|detail|count    (the analyzer prints the exact line in each diagnostic)',
    '# The file may only shrink: a count above what the code has is itself an error (ARC9001). Do not add lines for new code; fix the code.',
    '# Regenerate with scripts/safety-baseline.ps1 only after fixing findings or when a new rule lands.'
)

$previous = Get-Content -LiteralPath $baselinePath -Raw

# BOM-less UTF-8 with LF line endings on every PowerShell version, so a regenerated file diffs cleanly against the committed one.
function Write-Baseline([string[]]$lines) {
    [IO.File]::WriteAllText($baselinePath, (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding $false))
}
$log = Join-Path ([IO.Path]::GetTempPath()) ('safety-baseline-' + [Guid]::NewGuid().ToString('N') + '.log')
try {
    Write-Baseline $header
    Push-Location $root
    try {
        & dotnet build $Solution -c $Configuration -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:NuGetAudit=false -p:TreatWarningsAsErrors=false *> $log
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE); see $log" }
    }
    finally { Pop-Location }

    # Every warning is printed more than once (inline, then in the summary); one finding is one distinct "file(line,col): warning ARCnnnn" prefix (an assembly-level finding has no location: "CSC : warning ARCnnnn", distinct by its key).
    $findings = @{}
    foreach ($line in Get-Content -LiteralPath $log) {
        if ($line -match '^(?<where>.+?\(\d+,\d+\)|CSC ?): warning (?<id>ARC000\d): .*Baseline line: (?<key>ARC000\d\|[^|]+\|[^|]+\|[^|]+)\|1') {
            $findings[$Matches.where + '|' + $Matches.key] = $Matches.key
        }
    }
    if ($findings.Count -eq 0 -and ($previous -match '(?m)^ARC')) {
        throw ("the build reported no findings although the old baseline had entries; refusing to write an empty baseline (see $log)")
    }

    $counts = @{}
    foreach ($key in $findings.Values) { $counts[$key] = 1 + [int]$counts[$key] }
    $keys = [string[]]@($counts.Keys)
    [Array]::Sort($keys, [StringComparer]::Ordinal)
    $body = $keys | ForEach-Object { '{0}|{1}' -f $_, $counts[$_] }
    Write-Baseline ($header + $body)
    Write-Host ("safety baseline: {0} findings in {1} entries -> {2}" -f $findings.Count, $counts.Count, $baselinePath)
    Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue
}
catch {
    [IO.File]::WriteAllText($baselinePath, $previous, (New-Object Text.UTF8Encoding $false))
    throw
}
