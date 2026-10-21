# Decompiles one type, or the methods of a type with a given name, of a built assembly to C# or IL (the pinned ilspycmd of
# .config/dotnet-tools.json), to compare what the compiler emitted with what the source says.
#   pwsh -NoProfile -File tools/ops/quality/decompile.ps1 -Assembly ArcaneCore.Game -Type ArcaneCore.Game.Creatures.CreatureMovePackets [-Method Build] [-Il]
# -Assembly is a path to a .dll, or a bare project name (ArcaneCore.Game): the newest src/<name>/bin/*/*/<name>.dll or tests/<name>/bin/*/*/<name>.dll.
# -Type is the full name (a nested type as Outer+Inner; with -Member it is only reported, the member id names the type). -Method decompiles every method of that name in the type (all overloads, found
# through the metadata, no signature needed); -Member takes an ilspycmd member id instead (an XML doc id such as
# "M:Ns.Type.Name(System.Int32)", or a token such as 0x06000005). With neither, the whole type is written.
# -Il writes IL instead of C#. ilspycmd cannot select a type or member in IL mode (--type and --member are ignored with --ilcode), so the whole
# assembly's IL goes to a file under artifacts/decompile/<timestamp>/ and the requested type (or method) block is cut out of it; for a large
# assembly such as ArcaneCore.Game that takes minutes (-IlTimeoutSec, default 900).
# Output goes to stdout. Exit codes: 0 = decompiled, 2 = error (assembly, type or method not found, ilspycmd failed, empty output).
param(
    [Parameter(Mandatory)] [string]$Assembly,
    [Parameter(Mandatory)] [string]$Type,
    [string]$Method,
    [string]$Member,
    [switch]$Il,
    [ValidateRange(10, 7200)] [int]$IlTimeoutSec = 900
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'QualityLib.ps1')

function Stop-Decompile([string]$Message) {
    [Console]::Error.WriteLine("decompile.ps1: $Message")
    exit 2
}

trap { Stop-Decompile "unexpected error: $($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber)" }

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..' '..' '..')).Path
if ($Method -and $Member) { Stop-Decompile '-Method and -Member are alternatives; give one.' }

$dll = $null
if ($Assembly -match '\.dll$') {
    $candidate = if ([IO.Path]::IsPathRooted($Assembly)) { $Assembly } else { Join-Path $repo $Assembly }
    if (Test-Path -LiteralPath $candidate -PathType Leaf) { $dll = (Resolve-Path -LiteralPath $candidate).Path }
}
else {
    $dll = @('src', 'tests') | ForEach-Object { Join-Path $repo "$_/$Assembly/bin" } | Where-Object { Test-Path $_ } |
        ForEach-Object { Get-ChildItem $_ -Recurse -Filter "$Assembly.dll" } | Sort-Object LastWriteTime | Select-Object -Last 1 -ExpandProperty FullName
}
if (-not $dll) { Stop-Decompile "assembly '$Assembly' was not found; build it first (dotnet build) or pass the path of its .dll." }

# Method tokens by name, from the metadata (no load of the assembly).
function Get-MethodTokens([string]$DllPath, [string]$TypeName, [string]$MethodName) {
    $stream = [IO.File]::OpenRead($DllPath)
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        function FullName($handle) {
            $t = $md.GetTypeDefinition($handle)
            $name = $md.GetString($t.Name)
            $outer = $t.GetDeclaringType()
            if (-not $outer.IsNil) { return (FullName $outer) + '+' + $name }
            $ns = $md.GetString($t.Namespace)
            if ($ns) { "$ns.$name" } else { $name }
        }
        $found = $false; $tokens = @()
        foreach ($handle in $md.TypeDefinitions) {
            if ((FullName $handle) -ne $TypeName) { continue }
            $found = $true
            foreach ($mh in $md.GetTypeDefinition($handle).GetMethods()) {
                if ($md.GetString($md.GetMethodDefinition($mh).Name) -eq $MethodName) {
                    $tokens += '0x{0:X8}' -f [System.Reflection.Metadata.Ecma335.MetadataTokens]::GetToken([System.Reflection.Metadata.EntityHandle]$mh)
                }
            }
        }
        [pscustomobject]@{ TypeFound = $found; Tokens = $tokens }
    }
    finally { $stream.Dispose() }
}

if ($Il) {
    if ($Member) { Stop-Decompile '-Il cannot take -Member (ilspycmd ignores it in IL mode); name the method with -Method.' }
    $outDir = Join-Path $repo "artifacts/decompile/$(Get-Date -Format 'yyyyMMdd-HHmmss')-$PID"
    if (Test-Path -LiteralPath $outDir) { Stop-Decompile "the output folder $outDir exists already; run again." }
    New-Item -ItemType Directory $outDir | Out-Null
    $ilFile = Join-Path $outDir 'assembly.il'
    $process = Start-Process dotnet -ArgumentList @('tool', 'run', 'ilspycmd', '--', "`"$dll`"", '--ilcode') -WorkingDirectory $repo -NoNewWindow -PassThru `
        -RedirectStandardOutput $ilFile -RedirectStandardError (Join-Path $outDir 'ilspycmd.err')
    if (-not $process.WaitForExit($IlTimeoutSec * 1000)) {
        $process.Kill($true)
        Stop-Decompile "ilspycmd did not finish the IL of $dll within $IlTimeoutSec s (raise -IlTimeoutSec, or use C# mode, which selects a member)."
    }
    if ($process.ExitCode -ne 0) { Stop-Decompile "ilspycmd exited $($process.ExitCode) (see $(Join-Path $outDir 'ilspycmd.err'))." }
    # The type is matched by its whole path (namespace.Outer+Inner): a nested type is found inside its outer type's block, and a
    # type of the same short name elsewhere is not taken for it.
    $cut = Get-QualityIlBlock -Lines ([IO.File]::ReadLines($ilFile)) -Type $Type -Method $Method
    if (-not $cut.TypeSeen) { Stop-Decompile "type '$Type' has no class block in the IL of $dll (see $ilFile; a nested type is written Outer+Inner)." }
    if ($Method -and -not $cut.MethodSeen) { Stop-Decompile "type '$Type' has no method named '$Method' in the IL (see $ilFile)." }
    "// $dll (IL)"
    $cut.Block
    exit 0
}
# ilspycmd takes --type or --member, not both: -Type alone decompiles the whole type, a member id names its own type.
$arguments = @('tool', 'run', 'ilspycmd', '--', $dll)
Push-Location $repo
try {
    if ($Method) {
        $lookup = Get-MethodTokens $dll $Type $Method
        if (-not $lookup.TypeFound) { Stop-Decompile "type '$Type' is not in $dll (a nested type is written Outer+Inner)." }
        if ($lookup.Tokens.Count -eq 0) { Stop-Decompile "type '$Type' has no method named '$Method' (a lambda or local function has a compiler-generated name in a nested type)." }
        $text = foreach ($token in $lookup.Tokens) {
            "// ---- $Type.$Method ($token)"
            & dotnet @arguments '--member' $token 2>&1
            if ($LASTEXITCODE -ne 0) { Stop-Decompile "ilspycmd exited $LASTEXITCODE for $token." }
        }
    }
    else {
        $arguments += if ($Member) { @('--member', $Member) } else { @('--type', $Type) }
        $text = & dotnet @arguments 2>&1
        if ($LASTEXITCODE -ne 0) { Stop-Decompile "ilspycmd exited $LASTEXITCODE for type '$Type' ($($text -join ' | '))." }
    }
}
finally { Pop-Location }

# ilspycmd reports a failure through its exit code (checked above: 65 for a type or member it cannot find, 1 for a missing file), so
# the text is not searched for "Error": valid C# can start a line with that word. Only an empty answer is refused here.
$joined = ($text | ForEach-Object { "$_" }) -join [Environment]::NewLine
if ($joined -notmatch '\S') { Stop-Decompile "ilspycmd printed no code for '$Type' in $dll." }
"// $dll"
$joined
exit 0
