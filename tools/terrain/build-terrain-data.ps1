<#
.SYNOPSIS
Builds vmangos' extraction tools and extracts maps, vmaps and mmaps for ArcaneCore from a 1.12.1 client.

.DESCRIPTION
The recipe of docs/integration/maps-vmaps-mmaps.md as one script. Steps (all by default):

  Tools  copy the vmangos source (without .git/sql/bin) to <ToolsDir>\vmangos-src, add the native
         vmap oracle (tools\terrain\vmap-oracle), configure with MSVC + Ninja inside vcvars and build
         MapExtractor, VMapExtractor, VMapAssembler, MoveMapGenerator and VMapProbe. The vmangos
         build writes its binaries into <source>\bin, which is why the source is copied first.
  Maps   MapExtractor   -> <OutDir>\maps   (z1.4, float heights, maps only)
  VMaps  VMapExtractor  -> <OutDir>\Buildings, then VMapAssembler -> <OutDir>\vmaps (VMAP_7.0)
  MMaps  MoveMapGenerator once per -MMapRun entry -> <OutDir>\mmaps (mmap 6 / Detour 7)

Every tool runs with <OutDir> as its working directory, never inside the client folder (it is
only read). The output is derived from Blizzard's assets: keep it outside the repository.

.PARAMETER MMapRun
MoveMapGenerator argument lines, run in order. '0' and '1' build the continents, '' (empty)
builds every other map plus transports (tiles already built are skipped, so it resumes), and
'0 --tile 32,48' builds one tile (mmap tile order: Y first, i.e. the Northshire tile).

.EXAMPLE
pwsh -File tools\terrain\build-terrain-data.ps1 -ToolsDir D:\terrain-tools -OutDir D:\ArcaneCore-data
#>
[CmdletBinding()]
param(
    [string]$VmangosSource = 'D:\refs\vmangos',
    [string]$ClientDir = 'D:\World of Warcraft Classic 1.12.1',
    [Parameter(Mandatory = $true)][string]$ToolsDir,
    [Parameter(Mandatory = $true)][string]$OutDir,
    [ValidateSet('Tools', 'Maps', 'VMaps', 'MMaps')][string[]]$Steps = @('Tools', 'Maps', 'VMaps', 'MMaps'),
    [string[]]$MMapRun = @('0', '1', ''),
    [ValidateRange(1, 64)][int]$Threads = 6,
    [ValidateRange(1, 16)][int]$Jobs = 4
)

$ErrorActionPreference = 'Stop'
$repoTools = $PSScriptRoot
$src = Join-Path $ToolsDir 'vmangos-src'
$build = Join-Path $ToolsDir 'build'
$bin = Join-Path $src 'bin'

function Invoke-Checked([string]$what, [scriptblock]$action) {
    Write-Host "== $what"
    & $action
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}

function Assert-FreeMemory {
    $free = (Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory / 1MB
    if ($free -lt 2) { throw ("only {0:N1} GB RAM free; wait for other builds before continuing" -f $free) }
}

if ($Steps -contains 'Tools') {
    if (-not (Test-Path (Join-Path $VmangosSource 'contrib\mmap'))) { throw "$VmangosSource is not a vmangos source tree" }
    New-Item -ItemType Directory -Force $src, $build | Out-Null
    robocopy $VmangosSource $src /MIR /XD .git sql bin .serena /XF CMakeUserPresets.json /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "copying $VmangosSource failed (robocopy $LASTEXITCODE)" }
    $probe = Join-Path $src 'contrib\vmap_probe'
    New-Item -ItemType Directory -Force $probe | Out-Null
    Copy-Item (Join-Path $repoTools 'vmap-oracle\main.cpp'), (Join-Path $repoTools 'vmap-oracle\CMakeLists.txt') $probe -Force
    $contrib = Join-Path $src 'contrib\CMakeLists.txt'
    $text = Get-Content $contrib -Raw
    if ($text -notmatch 'vmap_probe') {
        Set-Content $contrib ($text -replace 'add_subdirectory\(mmap\)', "add_subdirectory(mmap)`nadd_subdirectory(vmap_probe)") -NoNewline
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw 'no Visual Studio with the C++ x64 tools found' }
    $vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
    $cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
    # CC/CXX are pinned: a shell that exports CC=<something else> makes CMake pick the wrong compiler.
    $configure = "`"$cmake`" -S `"$src`" -B `"$build`" -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl " +
        '-DCMAKE_POLICY_DEFAULT_CMP0141=NEW -DCMAKE_MSVC_DEBUG_INFORMATION_FORMAT=Embedded -DBUILD_EXTRACTORS=ON -DUSE_SCRIPTS=OFF ' +
        '-DENABLE_CPPTRACE=OFF -DBUILD_WARNINGS_AS_ERROR=OFF -DBUILD_FOR_HOST_CPU=OFF'
    $compile = "`"$cmake`" --build `"$build`" --target MapExtractor VMapExtractor VMapAssembler MoveMapGenerator VMapProbe -j $Jobs"
    Assert-FreeMemory
    Invoke-Checked 'configure + build vmangos tools' { cmd /c "call `"$vcvars`" >nul && set CC=cl && set CXX=cl && $configure && $compile" }
}

foreach ($exe in 'MapExtractor', 'VMapExtractor', 'VMapAssembler', 'MoveMapGenerator') {
    if (($Steps | Where-Object { $_ -ne 'Tools' }) -and -not (Test-Path (Join-Path $bin "$exe.exe"))) { throw "$exe.exe missing under $bin; run the Tools step" }
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
Push-Location $OutDir
try {
    if ($Steps -contains 'Maps') {
        Invoke-Checked 'MapExtractor' { & (Join-Path $bin 'MapExtractor.exe') -i $ClientDir -o $OutDir -e 1 -f 0 --silent > (Join-Path $OutDir 'map-extract.log') 2>&1 }
    }

    if ($Steps -contains 'VMaps') {
        Invoke-Checked 'VMapExtractor' { & (Join-Path $bin 'VMapExtractor.exe') -d (Join-Path $ClientDir 'Data') --silent > (Join-Path $OutDir 'vmap-extract.log') 2>&1 }
        New-Item -ItemType Directory -Force (Join-Path $OutDir 'vmaps') | Out-Null
        Invoke-Checked 'VMapAssembler' { 'x' | & (Join-Path $bin 'VMapAssembler.exe') Buildings vmaps > (Join-Path $OutDir 'vmap-assemble.log') 2>&1 }
    }

    if ($Steps -contains 'MMaps') {
        Copy-Item (Join-Path $src 'contrib\mmap\config.json'), (Join-Path $src 'contrib\mmap\offmesh.txt') $OutDir -Force
        New-Item -ItemType Directory -Force (Join-Path $OutDir 'mmaps') | Out-Null
        $i = 0
        foreach ($run in $MMapRun) {
            $arguments = @($run -split '\s+' | Where-Object { $_ }) + @('--silent', '--threads', "$Threads")
            $log = Join-Path $OutDir ("mmap-run{0}.log" -f $i++)
            Invoke-Checked "MoveMapGenerator $run" { & (Join-Path $bin 'MoveMapGenerator.exe') @arguments > $log 2>&1 }
        }
    }
}
finally {
    Pop-Location
}

foreach ($kind in 'maps', 'vmaps', 'mmaps') {
    $dir = Join-Path $OutDir $kind
    if (Test-Path $dir) { '{0,-6} {1,6} files' -f $kind, (Get-ChildItem $dir -File).Count }
}
"World:Maps:DataDirectory = $OutDir"
