# The redeploy planner and the World control-pipe client used by redeploy.ps1 (docs/ops/redeploy.md). Dot-source this file; it defines
# functions only. The planner compares a built worktree with what the live World runs (the deploy record written into <LiveHome>\bin at
# every deploy, the profile's configuration and the module directory) and picks the cheapest safe way to apply the difference:
#   None     nothing that reaches the daemons changed
#   Hot      only live configuration keys, content tables .reload supports and allowlisted hot modules changed: applied through the
#            World's control pipe (.reload config, .reload <table>, .hotmodule load|reload) with no restart
#   Restart  anything else (server code, a restart-only key, a schema update, no baseline): the hard stop / copy / start
#   Blocked  a hot module that is not built, stale, or not on the allowlist: nothing is touched until it is fixed (or -ForceRestart)
# Get-RedeployPlan is pure (inputs in, plan out) so the rules are tested on fixtures; Get-LiveRedeployPlan gathers the inputs.

Set-StrictMode -Version Latest

# The names .reload accepts besides `config` (ReloadAllMembershipTests.Expected pins them to the reloadables the World registers; the
# planner test pins this list to that one).
$script:ReloadableContent = @(
    'all_loot', 'areatrigger_tavern', 'areatrigger_teleport', 'chat_word_filter', 'conditions', 'creature_loot_template', 'creature_onkill_reputation',
    'creature_template', 'disenchant_loot_template', 'fishing_loot_template', 'game_tele', 'game_weather', 'gameobject_loot_template',
    'gameobject_template', 'gossip_menu', 'gossip_menu_option', 'instance_encounter', 'item_loot_template', 'item_template', 'npc_gossip',
    'npc_text', 'npc_trainer', 'npc_vendor', 'pickpocketing_loot_template', 'points_of_interest', 'quest_template', 'reference_loot_template',
    'reputation_reward_rate', 'reputation_spillover_template', 'reserved_name', 'rules', 'skill_fishing_base_level', 'skinning_loot_template',
    'spell_enchant_charges', 'spell_proc_event', 'spell_proc_item_enchant', 'spell_template', 'spell_threats', 'tuning'
)

# Repository paths that never reach the daemons' binaries or configuration (documentation, tests, operator tools, CI).
$script:NoRuntimeEffectPrefixes = @('docs/', 'tests/', 'tools/', 'scripts/', '.github/', 'apps/')
$script:NoRuntimeEffectFiles = @('.gitignore', '.gitattributes', 'LICENSE', 'docker-compose.yml')

$script:DeployRecordName = 'arcanecore-deploy.json'

function Get-DeployRecordPath {
    param([Parameter(Mandatory)][string]$LiveHome)
    return Join-Path (Join-Path $LiveHome 'bin') $script:DeployRecordName
}

function Read-DeployRecord {
    <#
    .SYNOPSIS
      The deploy record of the live install (what redeploy.ps1 last put there), or $null when there is none or it is unreadable.
    #>
    param([Parameter(Mandatory)][string]$LiveHome)
    $path = Get-DeployRecordPath -LiveHome $LiveHome
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { return Get-Content -Raw -LiteralPath $path | ConvertFrom-Json -AsHashtable } catch { return $null }
}

function Write-DeployRecord {
    param([Parameter(Mandatory)][string]$LiveHome, [Parameter(Mandatory)][hashtable]$Record)
    $path = Get-DeployRecordPath -LiveHome $LiveHome
    $Record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $path -Encoding utf8
}

function Invoke-Git {
    param([Parameter(Mandatory)][string]$Src, [Parameter(Mandatory)][string[]]$Arguments)
    $out = & git -C $Src @Arguments 2>$null
    return [pscustomobject]@{ Code = $LASTEXITCODE; Lines = @($out | Where-Object { $_ -ne $null -and $_ -ne '' }) }
}

function Get-WorktreeState {
    <#
    .SYNOPSIS
      The worktree's HEAD and its uncommitted paths (tracked changes and untracked files that are not ignored), repository-relative with '/'.
    #>
    param([Parameter(Mandatory)][string]$Src)
    $head = Invoke-Git $Src @('rev-parse', 'HEAD')
    if ($head.Code -ne 0 -or $head.Lines.Count -ne 1) { return $null }
    $tracked = Invoke-Git $Src @('diff', '--name-only', '--no-renames', 'HEAD')
    $untracked = Invoke-Git $Src @('ls-files', '--others', '--exclude-standard')
    return [pscustomobject]@{
        Sha        = $head.Lines[0].Trim()
        DirtyPaths = @(@($tracked.Lines) + @($untracked.Lines) | Sort-Object -Unique)
    }
}

function Get-ChangedPaths {
    <#
    .SYNOPSIS
      Every path that may differ between what the live daemons were built from and this worktree: the diff from the recorded SHA to the
      working tree, the untracked files, and the paths that were uncommitted when the record was written (their deployed content is not in
      any commit). Baseline is 'known', 'none' (no record or no SHA in it) or 'unreachable' (the SHA is not in this repository).
    #>
    param([Parameter(Mandatory)][string]$Src, $Record)
    if ($null -eq $Record -or -not $Record.ContainsKey('sha') -or [string]::IsNullOrWhiteSpace([string]$Record['sha'])) {
        return [pscustomobject]@{ Baseline = 'none'; Paths = @() }
    }
    $sha = [string]$Record['sha']
    if ($sha -notmatch '^[0-9a-fA-F]{7,64}$') { return [pscustomobject]@{ Baseline = 'unreachable'; Paths = @() } }
    $exists = Invoke-Git $Src @('cat-file', '-e', "$sha^{commit}")
    if ($exists.Code -ne 0) { return [pscustomobject]@{ Baseline = 'unreachable'; Paths = @() } }
    $diff = Invoke-Git $Src @('diff', '--name-only', '--no-renames', $sha)
    if ($diff.Code -ne 0) { return [pscustomobject]@{ Baseline = 'unreachable'; Paths = @() } }
    $untracked = Invoke-Git $Src @('ls-files', '--others', '--exclude-standard')
    $recordedDirty = @()
    if ($Record.ContainsKey('dirtyPaths') -and $null -ne $Record['dirtyPaths']) { $recordedDirty = @($Record['dirtyPaths']) }
    return [pscustomobject]@{
        Baseline = 'known'
        Paths    = @(@($diff.Lines) + @($untracked.Lines) + $recordedDirty | Where-Object { $_ } | Sort-Object -Unique)
    }
}

function Get-DeployedProjectDirs {
    <#
    .SYNOPSIS
      The repository-relative directories ('src/ArcaneCore.Kernel/') of every project the World and Realm daemons are built from
      (the ProjectReference closure of their .csproj files), plus the built-in packs: a project under src/ that copies itself beside the
      World executable (a CopyPackBesideWorldHost target, src/ArcaneCore.Playerbots) is loaded by the World at start although no daemon
      references it, and so is its closure.
    #>
    param([Parameter(Mandatory)][string]$Src)
    $root = (Resolve-Path -LiteralPath $Src).Path
    $pending = [Collections.Generic.Queue[string]]::new()
    foreach ($daemon in 'ArcaneCore.World', 'ArcaneCore.Realm') { $pending.Enqueue((Join-Path $root "src\$daemon\$daemon.csproj")) }
    foreach ($pack in @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.csproj' -Recurse -File -ErrorAction SilentlyContinue)) {
        if ((Get-Content -Raw -LiteralPath $pack.FullName) -match '<Target\s+Name="CopyPackBesideWorldHost"') { $pending.Enqueue($pack.FullName) }
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    while ($pending.Count -gt 0) {
        $project = [IO.Path]::GetFullPath($pending.Dequeue())
        if (-not $seen.Add($project) -or -not (Test-Path -LiteralPath $project)) { continue }
        [xml]$xml = Get-Content -Raw -LiteralPath $project
        foreach ($reference in @($xml.SelectNodes('//ProjectReference'))) {
            $pending.Enqueue((Join-Path (Split-Path -Parent $project) $reference.GetAttribute('Include')))
        }
    }
    return @($seen | ForEach-Object {
        $dir = [IO.Path]::GetRelativePath($root, (Split-Path -Parent $_)).Replace('\', '/')
        "$dir/"
    } | Sort-Object)
}

function Get-OtherProjectDirs {
    <#
    .SYNOPSIS
      The repository-relative directories of the tracked projects under src/ that are not in -DeployedProjectDirs.
    #>
    param([Parameter(Mandatory)][string]$Src, [string[]]$DeployedProjectDirs = @())
    return @((Invoke-Git $Src @('ls-files', 'src/*.csproj')).Lines | ForEach-Object {
        $dir = ([IO.Path]::GetDirectoryName($_) -replace '\\', '/') + '/'
        if ($DeployedProjectDirs -notcontains $dir) { $dir }
    } | Sort-Object -Unique)
}

function Get-PathClass {
    <#
    .SYNOPSIS
      What a changed repository path means for the live server: 'module:<Name>' (a hot module project's source), 'none' (never reaches
      the daemons), 'not-deployed' (a project the World and Realm are not built from) or 'core' (anything else: server code, build files,
      schema updates; a restart).
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [string[]]$DeployedProjectDirs = @(),
        [string[]]$OtherProjectDirs = @(),
        [object[]]$ModuleProjects = @()
    )
    $p = $Path.Replace('\', '/').TrimStart('/')
    foreach ($module in $ModuleProjects) {
        if ($p.StartsWith($module.Dir, [StringComparison]::OrdinalIgnoreCase)) { return "module:$($module.Name)" }
    }
    foreach ($dir in $DeployedProjectDirs) {
        if ($p.StartsWith($dir, [StringComparison]::OrdinalIgnoreCase)) { return 'core' }
    }
    if ($p.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase)) { return 'none' }
    foreach ($prefix in $script:NoRuntimeEffectPrefixes) {
        if ($p.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return 'none' }
    }
    if ($script:NoRuntimeEffectFiles -contains $p) { return 'none' }
    foreach ($dir in $OtherProjectDirs) {
        # A project the daemons are not built from (the gateway, for one).
        if ($p.StartsWith($dir, [StringComparison]::OrdinalIgnoreCase)) { return 'not-deployed' }
    }
    return 'core'
}

function Import-ConfCatalog {
    <#
    .SYNOPSIS
      The configuration catalog (src/ArcaneCore.Kernel/Configuration/Conf/conf-catalog.json, generated from the options classes): one
      entry per key with its reload class (live = applied by .reload config, restart / start = read once) and the daemons that read it.
    #>
    param([Parameter(Mandatory)][string]$Path)
    $keys = (Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json).keys
    $byPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($key in $keys) { $byPath[$key.path] = $key }
    return [pscustomobject]@{ Keys = @($keys); ByPath = $byPath }
}

function Get-ConfigKeyReload {
    <#
    .SYNOPSIS
      How a changed configuration path can be applied: 'live' (the World applies it at .reload config, and only the World reads it) or the
      reason it needs a restart ('restart', 'start', 'realm', 'framework', 'unknown').
    #>
    param([Parameter(Mandatory)]$Catalog, [Parameter(Mandatory)][string]$Path)
    $key = $null
    if ($Catalog.ByPath.ContainsKey($Path)) { $key = $Catalog.ByPath[$Path] }
    elseif ($Path -match '^(?i)(HostOptions|hostBuilder):' -or ($Path -match '^(?i)Logging:' -and $Path -notmatch '^(?i)Logging:ArcaneCore:')) { return 'framework' }
    else {
        foreach ($candidate in $Catalog.Keys) {
            if ($candidate.kind -ne 'scalar' -and $Path.StartsWith("$($candidate.path):", [StringComparison]::OrdinalIgnoreCase)) { $key = $candidate; break }
        }
    }
    if ($null -eq $key) { return 'unknown' }
    if ([string]$key.daemons -ne 'World') { return 'realm' }
    if ($key.reload -eq 'live') { return 'live' }
    return [string]$key.reload
}

function ConvertTo-FlatConfig {
    param($Node, [string]$Prefix, [hashtable]$Into)
    if ($Node -is [System.Collections.IDictionary]) {
        foreach ($name in $Node.Keys) { ConvertTo-FlatConfig $Node[$name] ($(if ($Prefix) { "${Prefix}:$name" } else { [string]$name })) $Into }
    } elseif ($Node -is [System.Collections.IList] -and $Node -isnot [string]) {
        for ($i = 0; $i -lt $Node.Count; $i++) { ConvertTo-FlatConfig $Node[$i] "${Prefix}:$i" $Into }
    } elseif ($Prefix) {
        $Into[$Prefix] = if ($null -eq $Node) { '' } else { [string]$Node }
    }
}

function Read-ConfFile {
    <#
    .SYNOPSIS
      The keys and values of a .conf file (docs: world.conf.dist SYNTAX): 'Key = Value' lines, '#' and ';' comments, '.' as the level
      separator unless the key already uses ':', values optionally quoted.
    #>
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][hashtable]$Into)
    foreach ($raw in Get-Content -LiteralPath $Path) {
        $line = $raw.Trim()
        if ($line.Length -eq 0 -or $line[0] -in '#', ';') { continue }
        $eq = $line.IndexOf('=')
        if ($eq -le 0) { continue }
        $key = $line.Substring(0, $eq).Trim()
        if ($key -notmatch ':') { $key = $key.Replace('.', ':') }
        $value = $line.Substring($eq + 1).Trim()
        if ($value.Length -ge 2 -and $value.StartsWith('"') -and $value.EndsWith('"')) { $value = $value.Substring(1, $value.Length - 2).Replace('\"', '"').Replace('\\', '\') }
        $Into[$key] = $value
    }
}

function Get-ProfileConfiguration {
    <#
    .SYNOPSIS
      The World's file configuration in effect for the profile, flattened to 'Section:Key' paths (case-insensitive): appsettings.json,
      appsettings.<Environment>.json, then world.conf (in the profile, or in ARCANECORE_CONFIG), later files overriding earlier ones.
    #>
    param([Parameter(Mandatory)][string]$LiveProfile, [string]$Environment = 'Development')
    $flat = @{}
    foreach ($name in 'appsettings.json', "appsettings.$Environment.json") {
        $file = Join-Path $LiveProfile $name
        if (Test-Path -LiteralPath $file) { ConvertTo-FlatConfig (Get-Content -Raw -LiteralPath $file | ConvertFrom-Json -AsHashtable) '' $flat }
    }
    $confDir = if ($env:ARCANECORE_CONFIG) { $env:ARCANECORE_CONFIG } else { $LiveProfile }
    $conf = Join-Path $confDir 'world.conf'
    if (Test-Path -LiteralPath $conf) { Read-ConfFile -Path $conf -Into $flat }
    return $flat
}

function Get-ConfigFingerprint {
    <#
    .SYNOPSIS
      A SHA-256 per configuration path of the profile's file configuration. Values are never stored, but the hashes are unsalted:
      a weak secret (a database password) can be guessed offline from the deploy record, so keep <LiveHome>\bin as private as world.conf.
    #>
    param([Parameter(Mandatory)][hashtable]$Configuration)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $fingerprint = [ordered]@{}
        foreach ($path in ($Configuration.Keys | Sort-Object)) {
            $bytes = [Text.Encoding]::UTF8.GetBytes("$($path.ToLowerInvariant())=$($Configuration[$path])")
            $fingerprint[$path] = [Convert]::ToHexString($sha.ComputeHash($bytes))
        }
        return $fingerprint
    } finally { $sha.Dispose() }
}

function Compare-ConfigFingerprint {
    <#
    .SYNOPSIS
      The configuration paths added, removed or changed between two fingerprints (keys compared without regard to case).
    #>
    param($Old, $New)
    $oldMap = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    $newMap = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    if ($Old) { foreach ($k in $Old.Keys) { $oldMap[[string]$k] = [string]$Old[$k] } }
    if ($New) { foreach ($k in $New.Keys) { $newMap[[string]$k] = [string]$New[$k] } }
    $changed = [Collections.Generic.List[string]]::new()
    foreach ($k in $newMap.Keys) { if (-not $oldMap.ContainsKey($k) -or $oldMap[$k] -ne $newMap[$k]) { $changed.Add($k) } }
    foreach ($k in $oldMap.Keys) { if (-not $newMap.ContainsKey($k)) { $changed.Add($k) } }
    return @($changed | Sort-Object -Unique)
}

function Get-ModuleProjects {
    <#
    .SYNOPSIS
      The hot module projects of the worktree: every tracked .csproj whose assembly name has a folder in the live module directory
      (<Directory>\<Name>\). Dir is repository-relative with '/' and a trailing '/'.
    #>
    param([Parameter(Mandatory)][string]$Src, [string]$ModuleDirectory)
    if (-not $ModuleDirectory -or -not (Test-Path -LiteralPath $ModuleDirectory)) { return @() }
    $root = (Resolve-Path -LiteralPath $Src).Path
    $found = @()
    foreach ($relative in (Invoke-Git $Src @('ls-files', '*.csproj')).Lines) {
        $project = Join-Path $root $relative
        if (-not (Test-Path -LiteralPath $project)) { continue }
        [xml]$xml = Get-Content -Raw -LiteralPath $project
        $assemblyName = @($xml.SelectNodes('//AssemblyName') | ForEach-Object { $_.InnerText.Trim() } | Where-Object { $_ })
        $name = if ($assemblyName.Count -gt 0) { $assemblyName[0] } else { [IO.Path]::GetFileNameWithoutExtension($project) }
        if (Test-Path -LiteralPath (Join-Path $ModuleDirectory $name) -PathType Container) {
            $dir = ([IO.Path]::GetDirectoryName($relative.Replace('\', '/'))).Replace('\', '/')
            $found += [pscustomobject]@{ Name = $name; Dir = "$dir/"; Project = $project }
        }
    }
    return $found
}

function Read-ModuleAllowlist {
    <#
    .SYNOPSIS
      The SHA-256 hashes of a module allowlist file (upper case), or $null when the file is missing or malformed (the World then refuses
      every module, docs/areas/code-hot-reload.md "The module allowlist").
    #>
    param([string]$Path)
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return $null }
    $hashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($raw in Get-Content -LiteralPath $Path) {
        $line = $raw.Trim()
        if ($line.Length -eq 0 -or $line.StartsWith('#')) { continue }
        $token = ($line -split '\s+')[0]
        if ($token -notmatch '^[0-9a-fA-F]{64}$') { return $null }
        [void]$hashes.Add($token)
    }
    return $hashes
}

function Get-ModuleBuild {
    <#
    .SYNOPSIS
      The built dll of a module project (bin\<Configuration>\<tfm>\<Name>.dll), its SHA-256, whether it is older than a changed source
      file of the project (Stale), and whether the allowlist lists it.
    #>
    param(
        [Parameter(Mandatory)]$Module,
        [Parameter(Mandatory)][string]$Src,
        [string[]]$ChangedPaths = @(),
        $Allowlist,
        [string]$Configuration = 'Debug'
    )
    $projectDir = Split-Path -Parent $Module.Project
    $dll = @(Get-ChildItem -Path (Join-Path $projectDir "bin\$Configuration\*\$($Module.Name).dll") -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTimeUtc -Descending)
    if ($dll.Count -eq 0) { return [pscustomobject]@{ Name = $Module.Name; Dll = $null; Sha256 = $null; Stale = $false; Allowlisted = $false } }
    $newestSource = [DateTime]::MinValue
    foreach ($path in $ChangedPaths) {
        if (-not $path.StartsWith($Module.Dir, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $file = Join-Path $Src $path
        if (Test-Path -LiteralPath $file -PathType Leaf) {
            $t = (Get-Item -LiteralPath $file).LastWriteTimeUtc
            if ($t -gt $newestSource) { $newestSource = $t }
        }
    }
    $sha = (Get-FileHash -LiteralPath $dll[0].FullName -Algorithm SHA256).Hash
    return [pscustomobject]@{
        Name        = $Module.Name
        Dll         = $dll[0].FullName
        Sha256      = $sha
        Stale       = $newestSource -gt $dll[0].LastWriteTimeUtc
        Allowlisted = ($null -ne $Allowlist -and $Allowlist.Contains($sha))
    }
}

function Get-RedeployPlan {
    <#
    .SYNOPSIS
      The plan for one redeploy (pure: no file, process or git access). See the header of this file for the actions.
    .PARAMETER Baseline        'known', 'none' or 'unreachable' (Get-ChangedPaths).
    .PARAMETER ConfigChanges   Configuration paths that differ from the deploy record's fingerprint.
    .PARAMETER ConfigBaseline  $false when the record has no configuration fingerprint (every key is then unknown: restart).
    .PARAMETER ContentTables   Reloadable names (.reload <name>) whose rows the operator changed in the live world database.
    .PARAMETER ModuleBuilds    Get-ModuleBuild results, keyed by module name.
    .PARAMETER Capabilities    @{ Pipe; Reload; Modules } of the running World (control pipe on, HotReload:Commands, module lane on).
    #>
    param(
        [ValidateSet('known', 'none', 'unreachable')][string]$Baseline = 'known',
        [string[]]$ChangedPaths = @(),
        [string[]]$DeployedProjectDirs = @(),
        [string[]]$OtherProjectDirs = @(),
        [object[]]$ModuleProjects = @(),
        [hashtable]$ModuleBuilds = @{},
        [string[]]$ConfigChanges = @(),
        [bool]$ConfigBaseline = $true,
        $Catalog,
        [string[]]$ContentTables = @(),
        [bool]$WorldDbSwap = $false,
        [bool]$WorldRunning = $true,
        [hashtable]$Capabilities = @{ Pipe = $true; Reload = $true; Modules = $true },
        [bool]$ForceRestart = $false,
        [int]$ListLimit = 8
    )
    $restart = [Collections.Generic.List[string]]::new()
    $blocked = [Collections.Generic.List[string]]::new()
    $steps = [Collections.Generic.List[object]]::new()
    $classified = @()

    if ($ForceRestart) { $restart.Add('-ForceRestart') }
    switch ($Baseline) {
        'none' { $restart.Add('no deployed SHA is recorded for the live install (first redeploy with the planner)') }
        'unreachable' { $restart.Add('the recorded deployed SHA is not a commit of this repository') }
    }
    if ($WorldDbSwap) { $restart.Add('-WorldDbSwap replaces the world database file, which needs the World stopped') }
    if (-not $WorldRunning) { $restart.Add('the World is not running') }

    # Code and repository files.
    $modulesChanged = [Collections.Generic.SortedSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $core = @()
    foreach ($path in $ChangedPaths) {
        $class = Get-PathClass -Path $path -DeployedProjectDirs $DeployedProjectDirs -OtherProjectDirs $OtherProjectDirs -ModuleProjects $ModuleProjects
        $classified += [pscustomobject]@{ Path = $path; Class = $class }
        if ($class -eq 'core') { $core += $path }
        elseif ($class.StartsWith('module:')) { [void]$modulesChanged.Add($class.Substring(7)) }
    }
    if ($core.Count -gt 0) {
        $shown = ($core | Select-Object -First $ListLimit) -join ', '
        $more = if ($core.Count -gt $ListLimit) { " and $($core.Count - $ListLimit) more" } else { '' }
        $restart.Add("server code or build input changed: $shown$more")
    }

    # Configuration.
    $configKeys = @()
    if (-not $ConfigBaseline -and $Baseline -eq 'known') { $restart.Add('the deploy record has no configuration fingerprint') }
    foreach ($key in $ConfigChanges) {
        $reload = if ($null -eq $Catalog) { 'unknown' } else { Get-ConfigKeyReload -Catalog $Catalog -Path $key }
        $configKeys += [pscustomobject]@{ Path = $key; Reload = $reload }
    }
    $notLive = @($configKeys | Where-Object Reload -ne 'live')
    if ($notLive.Count -gt 0) {
        $restart.Add('configuration that needs a restart changed: ' + (($notLive | Select-Object -First $ListLimit | ForEach-Object { "$($_.Path) ($($_.Reload))" }) -join ', '))
    }
    if ($configKeys.Count -gt 0 -and $notLive.Count -eq 0) {
        $steps.Add([pscustomobject]@{ Kind = 'config'; Name = 'config'; Command = 'reload config' })
    }

    # Content.
    foreach ($table in ($ContentTables | Where-Object { $_ } | Sort-Object -Unique)) {
        if ($script:ReloadableContent -contains $table) {
            $steps.Add([pscustomobject]@{ Kind = 'content'; Name = $table; Command = "reload $table" })
        } else {
            $restart.Add("content table '$table' has no .reload")
        }
    }

    # Hot modules.
    foreach ($name in $modulesChanged) {
        $build = if ($ModuleBuilds.ContainsKey($name)) { $ModuleBuilds[$name] } else { $null }
        if ($null -eq $build -or -not $build.Dll) { $blocked.Add("module $name changed but is not built (build its project first)"); continue }
        if ($build.Stale) { $blocked.Add("module ${name}: $($build.Dll) is older than its changed source (rebuild it)"); continue }
        if (-not $build.Allowlisted) { $blocked.Add("module ${name}: sha256 $($build.Sha256) is not on the module allowlist (approve it: scripts/hotmodule-hash.ps1)"); continue }
        $steps.Add([pscustomobject]@{ Kind = 'module'; Name = $name; Command = "hotmodule reload $name"; Dll = $build.Dll; Sha256 = $build.Sha256 })
    }

    # The hot path needs the World's control pipe and the matching command roots.
    if ($restart.Count -eq 0 -and $steps.Count -gt 0) {
        if (-not $Capabilities['Pipe']) { $restart.Add('the World has no control pipe (World:Control:Pipe:Enabled is false), so nothing can be applied in place') }
        if (@($steps | Where-Object Kind -in 'config', 'content').Count -gt 0 -and -not $Capabilities['Reload']) { $restart.Add('HotReload:Commands is off, so .reload does not exist') }
        if (@($steps | Where-Object Kind -eq 'module').Count -gt 0 -and -not $Capabilities['Modules']) { $restart.Add('World:HotCode:Modules:Enabled is off, so .hotmodule does not exist') }
    }

    $action = if ($blocked.Count -gt 0 -and -not $ForceRestart) { 'Blocked' }
    elseif ($restart.Count -gt 0) { 'Restart' }
    elseif ($steps.Count -gt 0) { 'Hot' }
    else { 'None' }

    return [pscustomobject]@{
        Action        = $action
        RestartReasons = @($restart)
        BlockedReasons = @($blocked)
        Steps         = @($steps)
        Paths         = @($classified)
        ConfigKeys    = @($configKeys)
        Modules       = @($modulesChanged)
    }
}

function Format-RedeployPlan {
    param([Parameter(Mandatory)]$Plan)
    $lines = @("redeploy plan: $($Plan.Action)")
    foreach ($r in $Plan.BlockedReasons) { $lines += "  blocked: $r" }
    foreach ($r in $Plan.RestartReasons) { $lines += "  restart: $r" }
    if ($Plan.Action -eq 'Hot') { foreach ($s in $Plan.Steps) { $lines += "  hot: .$($s.Command)" } }
    $counts = $Plan.Paths | Group-Object Class | ForEach-Object { "$($_.Name)=$($_.Count)" }
    if ($counts) { $lines += "  changed paths: $($counts -join ', ')" }
    foreach ($k in $Plan.ConfigKeys) { $lines += "  config: $($k.Path) ($($k.Reload))" }
    return $lines
}

# ---- World control pipe client (src/ArcaneCore.World/Ops/Control/WorldControlPipe.cs) -------------------------------------------------

function Initialize-PipeInterop {
    if ('ArcaneCoreOps.PipeInterop' -as [type]) { return }
    Add-Type -Namespace ArcaneCoreOps -Name PipeInterop -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
public static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);
'@
}

function Resolve-WorldControlPipeName {
    <#
    .SYNOPSIS
      The control pipe name of a World process: World:Control:Pipe:Name from the profile configuration (default arcanecore-world-{pid})
      with {pid} replaced.
    #>
    param([Parameter(Mandatory)][hashtable]$Configuration, [Parameter(Mandatory)][int]$WorldPid)
    $template = 'arcanecore-world-{pid}'
    if ($Configuration.ContainsKey('World:Control:Pipe:Name') -and $Configuration['World:Control:Pipe:Name']) { $template = $Configuration['World:Control:Pipe:Name'] }
    return $template.Replace('{pid}', [string]$WorldPid, [StringComparison]::OrdinalIgnoreCase)
}

function Connect-WorldControl {
    <#
    .SYNOPSIS
      Open the World's control pipe and check that the process serving it is -ExpectedPid (the live World), so a pipe some other process
      created under that name is never sent a command.
    #>
    param([Parameter(Mandatory)][string]$PipeName, [Parameter(Mandatory)][int]$ExpectedPid, [int]$TimeoutMs = 5000)
    Initialize-PipeInterop
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $PipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect($TimeoutMs)
        [uint32]$serverPid = 0
        if (-not [ArcaneCoreOps.PipeInterop]::GetNamedPipeServerProcessId($pipe.SafePipeHandle, [ref]$serverPid)) { throw "could not read the server process of \\.\pipe\$PipeName" }
        if ($serverPid -ne $ExpectedPid) { throw "\\.\pipe\$PipeName is served by process $serverPid, not the World ($ExpectedPid): refusing to send commands" }
    } catch {
        $pipe.Dispose()
        throw
    }
    $encoding = [Text.UTF8Encoding]::new($false)
    $writer = [IO.StreamWriter]::new($pipe, $encoding, 4096, $true)
    $writer.AutoFlush = $true
    $writer.NewLine = "`n"
    return [pscustomobject]@{
        Pipe    = $pipe
        Reader  = [IO.StreamReader]::new($pipe, $encoding, $false, 4096, $true)
        Writer  = $writer
        Pending = $null
        Name    = $PipeName
    }
}

function Read-WorldControlLine {
    <#
    .SYNOPSIS
      The next line from the pipe, or $null on timeout (the read stays pending for the next call) or at the end of the connection.
    #>
    param([Parameter(Mandatory)]$Session, [int]$TimeoutMs = 30000)
    if ($null -eq $Session.Pending) { $Session.Pending = $Session.Reader.ReadLineAsync() }
    if (-not $Session.Pending.Wait($TimeoutMs)) { return $null }
    $line = $Session.Pending.Result
    $Session.Pending = $null
    if ($null -eq $line) { throw "the World closed \\.\pipe\$($Session.Name)" }
    return $line
}

function Invoke-WorldControlCommand {
    <#
    .SYNOPSIS
      Send one console command and return its reply lines (without the '[console] ' prefix) up to the '#done' line.
    #>
    param([Parameter(Mandatory)]$Session, [Parameter(Mandatory)][string]$Command, [int]$TimeoutSec = 40)
    $Session.Writer.WriteLine($Command)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    $lines = @()
    while ($true) {
        $left = [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds
        if ($left -le 0) { throw "no answer to '$Command' within $TimeoutSec s" }
        $line = Read-WorldControlLine -Session $Session -TimeoutMs $left
        if ($null -eq $line) { continue }
        if ($line -eq '#done') { return $lines }
        $lines += ($line -replace '^\[console\] ', '')
    }
}

function Wait-WorldControlLine {
    <#
    .SYNOPSIS
      Read reply lines (a command that finishes later answers after '#done') until one matches -Pattern; returns it, or $null on timeout.
    #>
    param([Parameter(Mandatory)]$Session, [Parameter(Mandatory)][string]$Pattern, [int]$TimeoutSec = 120)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ($true) {
        $left = [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds
        if ($left -le 0) { return $null }
        $line = Read-WorldControlLine -Session $Session -TimeoutMs $left
        if ($null -eq $line) { continue }
        $text = $line -replace '^\[console\] ', ''
        if ($text -match $Pattern) { return $text }
    }
}

function Close-WorldControl {
    param($Session)
    if ($null -eq $Session) { return }
    try { $Session.Writer.Dispose() } catch { }
    try { $Session.Reader.Dispose() } catch { }
    $Session.Pipe.Dispose()
}

function New-HotStepError {
    <#
    .SYNOPSIS
      A hot step failure. Kind 'Unavailable': the hot path could not be used (no answer, command missing, busy), so the hard restart is the
      fallback. Kind 'Rejected': the World refused the change and kept what it runs, so the redeploy stops and the operator decides.
    #>
    param([Parameter(Mandatory)][ValidateSet('Unavailable', 'Rejected')][string]$Kind, [Parameter(Mandatory)][string]$Message)
    $exception = [InvalidOperationException]::new($Message)
    $exception.Data['HotStep'] = $Kind
    return $exception
}

function Invoke-HotReloadStep {
    <#
    .SYNOPSIS
      Run '.reload <name>' through the pipe and wait for its result; the step succeeds only when '.reload status' then says Applied.
    #>
    param([Parameter(Mandatory)]$Session, [Parameter(Mandatory)][string]$Name, [string]$Reason = 'redeploy', [int]$TimeoutSec = 180)
    $reply = @(Invoke-WorldControlCommand -Session $Session -Command "reload $Name $Reason")
    if (-not ($reply -match "^Re-loading $([regex]::Escape($Name))\.\.\.")) {
        throw (New-HotStepError Unavailable ".reload $Name was not started: $($reply -join ' | ')")
    }
    $escaped = [regex]::Escape($Name)
    $result = Wait-WorldControlLine -Session $Session -Pattern "^$escaped (?!\.\.\.)" -TimeoutSec $TimeoutSec
    if ($null -eq $result) { throw (New-HotStepError Unavailable ".reload $Name gave no result within $TimeoutSec s") }
    if ($result -match "^$escaped not reloaded \((?<status>\w+)\)") {
        $kind = if ($Matches.status -eq 'Busy') { 'Unavailable' } else { 'Rejected' }
        throw (New-HotStepError $kind $result)
    }
    $status = @(Invoke-WorldControlCommand -Session $Session -Command 'reload status') | Where-Object { $_ -match "^${escaped}: " } | Select-Object -First 1
    if ($status -notmatch "^${escaped}: Applied ") { throw (New-HotStepError Rejected ".reload $Name answered '$result' but .reload status says '$status'") }
    return $result
}

function Invoke-HotModuleStep {
    <#
    .SYNOPSIS
      Put a module build into the module directory and load or replace it through the pipe. On any failure the previous dll is put
      back, so the directory matches what the World still runs (a rejected replace keeps the old version loaded).
    #>
    param(
        [Parameter(Mandatory)]$Session,
        [Parameter(Mandatory)]$Step,
        [Parameter(Mandatory)][string]$ModuleDirectory,
        [string]$Reason = 'redeploy',
        [int]$TimeoutSec = 120
    )
    $name = $Step.Name
    $listed = @(Invoke-WorldControlCommand -Session $Session -Command 'hotmodule list')
    if (-not ($listed -match '^Unloaded modules not yet freed')) { throw (New-HotStepError Unavailable ".hotmodule list failed: $($listed -join ' | ')") }
    $verb = if ($listed -match "^$([regex]::Escape($name)): ") { 'reload' } else { 'load' }
    $folder = Join-Path $ModuleDirectory $name
    $target = Join-Path $folder "$name.dll"
    $backup = $null
    if (Test-Path -LiteralPath $target) {
        $backup = "$target.bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        Copy-Item -LiteralPath $target -Destination $backup
    }
    try {
        Copy-Item -LiteralPath $Step.Dll -Destination $target -Force
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $Step.Sha256) { throw (New-HotStepError Rejected "copied $target does not hash to $($Step.Sha256)") }
        $pdb = [IO.Path]::ChangeExtension($Step.Dll, '.pdb')
        if (Test-Path -LiteralPath $pdb) { Copy-Item -LiteralPath $pdb -Destination (Join-Path $folder "$name.pdb") -Force }
        $reply = @(Invoke-WorldControlCommand -Session $Session -Command "hotmodule $verb $name $Reason")
        if (-not ($reply -match "^Module $verb requested\.")) { throw (New-HotStepError Unavailable ".hotmodule $verb $name was not started: $($reply -join ' | ')") }
        $result = Wait-WorldControlLine -Session $Session -Pattern "^Module $verb (done|rejected|failed)" -TimeoutSec $TimeoutSec
        if ($null -eq $result) { throw (New-HotStepError Unavailable ".hotmodule $verb $name gave no result within $TimeoutSec s") }
        if ($result -notmatch "^Module $verb done") { throw (New-HotStepError Rejected $result) }
        $after = @(Invoke-WorldControlCommand -Session $Session -Command 'hotmodule list') | Where-Object { $_ -match "^$([regex]::Escape($name)): " }
        if (-not ($after -match "sha256 $($Step.Sha256.Substring(0, 16))")) { throw (New-HotStepError Rejected ".hotmodule list does not show $name at sha256 $($Step.Sha256): $after") }
        if ($backup) { Remove-Item -LiteralPath $backup -ErrorAction SilentlyContinue }
        return $result
    } catch {
        if ($backup) { Copy-Item -LiteralPath $backup -Destination $target -Force; Remove-Item -LiteralPath $backup -ErrorAction SilentlyContinue }
        elseif (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -ErrorAction SilentlyContinue }
        throw
    }
}

# ---- Live inputs and the hot apply ---------------------------------------------------------------------------------------------------

function Test-ConfigFlag {
    param([Parameter(Mandatory)][hashtable]$Configuration, [Parameter(Mandatory)][string]$Path)
    return $Configuration.ContainsKey($Path) -and ([string]$Configuration[$Path]) -match '^(?i)true$'
}

function Resolve-ProfilePath {
    param([Parameter(Mandatory)][string]$LiveProfile, [string]$Path)
    if (-not $Path) { return $null }
    if ([IO.Path]::IsPathRooted($Path)) { return $Path }
    return Join-Path $LiveProfile $Path
}

function Get-LiveRedeployPlan {
    <#
    .SYNOPSIS
      Gather the planner's inputs from the worktree (git, projects, module builds, the configuration catalog), the live install (the
      deploy record) and the profile (configuration, module directory and allowlist), and plan.
    #>
    param(
        [Parameter(Mandatory)][string]$Src,
        [Parameter(Mandatory)][string]$LiveHome,
        [Parameter(Mandatory)][string]$LiveProfile,
        [string[]]$ContentTables = @(),
        [bool]$WorldDbSwap = $false,
        [bool]$WorldRunning = $true,
        [bool]$ForceRestart = $false
    )
    $record = Read-DeployRecord -LiveHome $LiveHome
    $changes = Get-ChangedPaths -Src $Src -Record $record
    $configuration = Get-ProfileConfiguration -LiveProfile $LiveProfile
    $fingerprint = Get-ConfigFingerprint -Configuration $configuration
    $configBaseline = $null -ne $record -and $record.ContainsKey('config') -and $null -ne $record['config']
    $configChanges = if ($configBaseline) { @(Compare-ConfigFingerprint -Old $record['config'] -New $fingerprint) } else { @() }
    $deployed = @(Get-DeployedProjectDirs -Src $Src)
    $other = @(Get-OtherProjectDirs -Src $Src -DeployedProjectDirs $deployed)
    $moduleDirectory = Resolve-ProfilePath $LiveProfile ([string]$configuration['World:HotCode:Modules:Directory'])
    $modules = @(Get-ModuleProjects -Src $Src -ModuleDirectory $moduleDirectory)
    $allowlist = Read-ModuleAllowlist -Path (Resolve-ProfilePath $LiveProfile ([string]$configuration['World:HotCode:Modules:Allowlist']))
    $builds = @{}
    foreach ($module in $modules) { $builds[$module.Name] = Get-ModuleBuild -Module $module -Src $Src -ChangedPaths $changes.Paths -Allowlist $allowlist }
    $catalog = Import-ConfCatalog -Path (Join-Path $Src 'src\ArcaneCore.Kernel\Configuration\Conf\conf-catalog.json')
    $capabilities = @{
        Pipe    = Test-ConfigFlag $configuration 'World:Control:Pipe:Enabled'
        Reload  = Test-ConfigFlag $configuration 'HotReload:Commands'
        Modules = Test-ConfigFlag $configuration 'World:HotCode:Modules:Enabled'
    }
    $plan = Get-RedeployPlan -Baseline $changes.Baseline -ChangedPaths $changes.Paths -DeployedProjectDirs $deployed -OtherProjectDirs $other `
        -ModuleProjects $modules -ModuleBuilds $builds -ConfigChanges $configChanges -ConfigBaseline $configBaseline -Catalog $catalog `
        -ContentTables $ContentTables -WorldDbSwap $WorldDbSwap -WorldRunning $WorldRunning -Capabilities $capabilities -ForceRestart $ForceRestart
    return [pscustomobject]@{
        Plan            = $plan
        Record          = $record
        Configuration   = $configuration
        Fingerprint     = $fingerprint
        ModuleDirectory = $moduleDirectory
        ModuleBuilds    = $builds
        Worktree        = Get-WorktreeState -Src $Src
    }
}

function New-DeployRecordData {
    <#
    .SYNOPSIS
      The deploy record for what the live install runs after a redeploy: the worktree's HEAD and uncommitted paths, the configuration
      fingerprint, the module hashes, how it got there and the record it replaced.
    #>
    param([Parameter(Mandatory)]$Live, [Parameter(Mandatory)][string]$Src, [Parameter(Mandatory)][ValidateSet('restart', 'hot')][string]$Method, [hashtable]$Modules = @{})
    $previous = $Live.Record
    $moduleHashes = @{}
    if ($null -ne $previous -and $previous.ContainsKey('modules') -and $previous['modules'] -is [System.Collections.IDictionary]) {
        foreach ($k in $previous['modules'].Keys) { $moduleHashes[$k] = $previous['modules'][$k] }
    }
    foreach ($k in $Modules.Keys) { $moduleHashes[$k] = $Modules[$k] }
    return [ordered]@{
        schema      = 1
        sha         = if ($Live.Worktree) { $Live.Worktree.Sha } else { $null }
        dirtyPaths  = if ($Live.Worktree) { @($Live.Worktree.DirtyPaths) } else { @() }
        src         = $Src
        method      = $Method
        deployedUtc = [DateTime]::UtcNow.ToString('o')
        previousSha = if ($null -ne $previous -and $previous.ContainsKey('sha')) { $previous['sha'] } else { $null }
        config      = $Live.Fingerprint
        modules     = $moduleHashes
    }
}

function Invoke-HotRedeploy {
    <#
    .SYNOPSIS
      Apply a Hot plan through the World's control pipe, step by step in plan order. Outcome 'Applied' (every step), 'Unavailable' (the
      hot path could not be used: fall back to the hard restart) or 'Rejected' (the World refused a change and kept what it runs).
      Applied lists the steps that took effect before a failure.
    #>
    param(
        [Parameter(Mandatory)]$Plan,
        [Parameter(Mandatory)][int]$WorldPid,
        [Parameter(Mandatory)][hashtable]$Configuration,
        [string]$ModuleDirectory,
        [string]$Reason = 'redeploy'
    )
    $applied = [Collections.Generic.List[string]]::new()
    $modules = @{}
    $session = $null
    try {
        $session = Connect-WorldControl -PipeName (Resolve-WorldControlPipeName -Configuration $Configuration -WorldPid $WorldPid) -ExpectedPid $WorldPid
        foreach ($step in $Plan.Steps) {
            if ($step.Kind -eq 'module') {
                $result = Invoke-HotModuleStep -Session $session -Step $step -ModuleDirectory $ModuleDirectory -Reason $Reason
                $modules[$step.Name] = $step.Sha256
            } else {
                $result = Invoke-HotReloadStep -Session $session -Name $step.Name -Reason $Reason
            }
            $applied.Add(".$($step.Command): $result")
        }
        return [pscustomobject]@{ Outcome = 'Applied'; Detail = ''; Applied = @($applied); Modules = $modules }
    } catch {
        $kind = $_.Exception.Data['HotStep']
        if (-not $kind) { $kind = 'Unavailable' }
        return [pscustomobject]@{ Outcome = $kind; Detail = $_.Exception.Message; Applied = @($applied); Modules = $modules }
    } finally {
        Close-WorldControl $session
    }
}
