# Shared helpers of the live launchers start-single.ps1 and start-cluster.ps1 (docs/ops/tooling.md), and of launch-daemon.ps1's window title.
# Dot-source this file; it defines functions only. Nothing here starts a process except Start-LiveWindow.
#   - build detection: is <LiveHome>\bin a Debug or a Release build (Get-LiveBuildConfiguration), read from assembly metadata, offline
#   - plan data: window titles, masked environment values, per-child log settings, profile ports, command lines
#   - waiting: a child process of a given parent that owns a given port
# Thread affinity: none. Everything is synchronous and uses no module-level state.

Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'LiveEnv.ps1')
. (Join-Path $PSScriptRoot 'LiveDiagnostics.ps1')

# ---- build detection ---------------------------------------------------------------------------------------------------------------

function Get-LiveAssemblyBuildInfo {
    <#
    .SYNOPSIS
      What one managed assembly says about its build, read from its metadata without loading it (System.Reflection.Metadata over the file).
    .DESCRIPTION
      Two assembly-level attributes carry it:
        DebuggableAttribute          the compiler emits it for every build with debug information; its DisableOptimizations bit (0x100) is
                                     set by Optimize=false (the Debug configuration) and clear for Optimize=true (Release). This is the
                                     truth about how the JIT treats the code, so it decides when present.
        AssemblyConfigurationAttribute  the SDK writes the MSBuild Configuration name ("Debug", "Release") unless
                                     GenerateAssemblyConfigurationAttribute is off; it decides only when DebuggableAttribute is absent.
      Returns $null for a file that is not a managed assembly. Otherwise an object with Path, Configuration ('Debug', 'Release' or
      'unknown'), Source (which attribute decided), DisableOptimizations ($true/$false/$null) and AssemblyConfiguration (string or $null).
      The file is opened for shared read, so a running daemon's assembly can be inspected.
    #>
    param([Parameter(Mandatory)][string]$Path)
    $stream = $null
    $pe = $null
    try {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        if (-not $pe.HasMetadata) { return $null }
        $md = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        if (-not $md.IsAssembly) { return $null }
        $disableOpt = $null
        $configuration = $null
        foreach ($handle in $md.GetAssemblyDefinition().GetCustomAttributes()) {
            $attribute = $md.GetCustomAttribute($handle)
            $ctor = $attribute.Constructor
            $typeName = $null
            $typeNamespace = $null
            if ($ctor.Kind -eq [System.Reflection.Metadata.HandleKind]::MemberReference) {
                $member = $md.GetMemberReference([System.Reflection.Metadata.MemberReferenceHandle]$ctor)
                if ($member.Parent.Kind -eq [System.Reflection.Metadata.HandleKind]::TypeReference) {
                    $type = $md.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$member.Parent)
                    $typeName = $md.GetString($type.Name)
                    $typeNamespace = $md.GetString($type.Namespace)
                }
            } elseif ($ctor.Kind -eq [System.Reflection.Metadata.HandleKind]::MethodDefinition) {
                $method = $md.GetMethodDefinition([System.Reflection.Metadata.MethodDefinitionHandle]$ctor)
                $type = $md.GetTypeDefinition($method.GetDeclaringType())
                $typeName = $md.GetString($type.Name)
                $typeNamespace = $md.GetString($type.Namespace)
            }
            if ($null -eq $typeName) { continue }
            $blob = [byte[]]$md.GetBlobBytes($attribute.Value)
            if ($typeNamespace -eq 'System.Diagnostics' -and $typeName -eq 'DebuggableAttribute') {
                # prolog 01 00, then DebuggingModes (int32) for the enum constructor, or (isJITTrackingEnabled, isJITOptimizerDisabled) bytes.
                if ($blob.Length -ge 6 -and $blob[0] -eq 1 -and $blob[1] -eq 0) {
                    $disableOpt = (([BitConverter]::ToInt32($blob, 2)) -band 0x100) -ne 0
                } elseif ($blob.Length -ge 4 -and $blob[0] -eq 1 -and $blob[1] -eq 0) {
                    $disableOpt = $blob[3] -ne 0
                }
            } elseif ($typeNamespace -eq 'System.Reflection' -and $typeName -eq 'AssemblyConfigurationAttribute') {
                # prolog 01 00, then a SerString: a compressed length (single byte below 0x80) and UTF-8; 0xFF is a null string.
                if ($blob.Length -ge 3 -and $blob[0] -eq 1 -and $blob[1] -eq 0 -and $blob[2] -lt 0x80) {
                    $length = [int]$blob[2]
                    if ($blob.Length -ge 3 + $length) { $configuration = [System.Text.Encoding]::UTF8.GetString($blob, 3, $length) }
                }
            }
        }
        $result = 'unknown'
        $source = 'no DebuggableAttribute or AssemblyConfigurationAttribute'
        if ($null -ne $disableOpt) {
            $result = if ($disableOpt) { 'Debug' } else { 'Release' }
            $source = 'DebuggableAttribute (DisableOptimizations ' + $(if ($disableOpt) { 'set' } else { 'clear' }) + ')'
        } elseif ($configuration -in 'Debug', 'Release') {
            $result = $configuration
            $source = 'AssemblyConfigurationAttribute'
        }
        return [pscustomobject]@{ Path = $Path; Configuration = $result; Source = $source; DisableOptimizations = $disableOpt; AssemblyConfiguration = $configuration }
    } catch [System.BadImageFormatException] {
        return $null
    } finally {
        if ($null -ne $pe) { $pe.Dispose() }
        if ($null -ne $stream) { $stream.Dispose() }
    }
}

function Get-LiveBuildConfiguration {
    <#
    .SYNOPSIS
      'Debug', 'Release', 'Mixed' or 'unknown' for the build in a bin directory: every ArcaneCore.*.dll in it is read
      (Get-LiveAssemblyBuildInfo) and they must agree. Falls back to 'unknown' when the directory or the assemblies are missing or none says.
    .DESCRIPTION
      Returns an object: Configuration, Detail (one line for the plan) and Assemblies (the per-file results). ArcaneCore.Kernel.dll is the
      anchor every daemon references; Mixed means two assemblies disagree (a Release World beside a Debug Kernel, for example): the
      redeploy copies whole build outputs, so Mixed points at a hand-copied bin.
    #>
    param([Parameter(Mandatory)][string]$BinDir)
    if (-not (Test-Path -LiteralPath $BinDir -PathType Container)) {
        return [pscustomobject]@{ Configuration = 'unknown'; Detail = "bin directory not found"; Assemblies = @() }
    }
    $infos = @(Get-ChildItem -LiteralPath $BinDir -File -Filter 'ArcaneCore.*.dll' -ErrorAction SilentlyContinue | Sort-Object Name |
        ForEach-Object { Get-LiveAssemblyBuildInfo -Path $_.FullName } | Where-Object { $null -ne $_ })
    $known = @($infos | Where-Object { $_.Configuration -ne 'unknown' })
    if ($known.Count -eq 0) {
        return [pscustomobject]@{ Configuration = 'unknown'; Detail = "no ArcaneCore assembly in the bin directory carries a build marker ($($infos.Count) read)"; Assemblies = $infos }
    }
    $kinds = @($known | ForEach-Object Configuration | Sort-Object -Unique)
    if ($kinds.Count -gt 1) {
        $debug = @($known | Where-Object Configuration -eq 'Debug' | ForEach-Object { Split-Path -Leaf $_.Path })
        return [pscustomobject]@{ Configuration = 'Mixed'; Detail = "assemblies disagree; Debug: $($debug -join ', ')"; Assemblies = $infos }
    }
    $kernel = $known | Where-Object { (Split-Path -Leaf $_.Path) -eq 'ArcaneCore.Kernel.dll' } | Select-Object -First 1
    $via = if ($kernel) { $kernel.Source } else { $known[0].Source }
    return [pscustomobject]@{ Configuration = $kinds[0]; Detail = "$($known.Count) ArcaneCore assemblies, all $($kinds[0]) (via $via)"; Assemblies = $infos }
}

function Get-LiveWindowTitle {
    <# .SYNOPSIS  "<name> (<Debug|Release|Mixed|unknown>, live)": the title every window of the live launchers carries. #>
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Configuration)
    return "$Name ($Configuration, live)"
}

# ---- masking -----------------------------------------------------------------------------------------------------------------------

function Test-LiveSecretName {
    <# .SYNOPSIS  $true for a variable or configuration key name that holds a secret (its value is never printed). #>
    param([Parameter(Mandatory)][string]$Name)
    return $Name -match '(?i)(secret|password|passwd|token|credential|apikey|api_key|privatekey|(^|[_:])key$)'
}

function Format-LiveEnvValue {
    <# .SYNOPSIS  The value to print for a variable: '***' when its name says secret, else the value. #>
    param([Parameter(Mandatory)][string]$Name, [AllowNull()][string]$Value)
    if (Test-LiveSecretName $Name) { return '***' }
    return $Value
}

function Hide-LiveSecrets {
    <# .SYNOPSIS  Text with every non-empty secret value replaced by '***' (belt and braces over the name-based masking). #>
    param([AllowEmptyString()][string]$Text, [string[]]$Secrets = @())
    foreach ($secret in $Secrets) {
        if (-not [string]::IsNullOrEmpty($secret)) { $Text = $Text.Replace($secret, '***') }
    }
    return $Text
}

# ---- plan data ---------------------------------------------------------------------------------------------------------------------

function Get-LiveDotnetRoot {
    <# .SYNOPSIS  DOTNET_ROOT, else <user profile>\.dotnet (as launch-daemon.ps1 resolves it). #>
    if ($env:DOTNET_ROOT) { return $env:DOTNET_ROOT }
    $userHome = if ($env:USERPROFILE) { $env:USERPROFILE } elseif ($env:HOME) { $env:HOME } else { '' }
    if ($userHome) { return Join-Path $userHome '.dotnet' }
    return ''
}

function Get-LiveDaemonEnvironmentPlan {
    <#
    .SYNOPSIS
      The environment launch-daemon.ps1 gives a daemon (name -> value, ordered), as data, for the plan: the fixed variables, the log
      path variables, the GC setting and the diagnostics settings that will be applied (not those already set in the environment).
      Pure: sets nothing.
    #>
    param(
        [Parameter(Mandatory)][ValidateSet('Realm', 'World')][string]$Role,
        [Parameter(Mandatory)][string]$LiveHome,
        [string]$LiveProfile,
        [ValidateSet('Standard', 'Max')][string]$Diagnostics = 'Max',
        [int]$GCHighMemPercent = 97,
        [string]$LogStem
    )
    $plan = [ordered]@{ DOTNET_ENVIRONMENT = 'Development'; ARCANECORE_LIVE_SERVER = '1' }
    foreach ($entry in (Get-LiveLogSettings -LiveHome $LiveHome -LiveProfile $LiveProfile -LogStem $(if ($LogStem) { $LogStem } else { $Role.ToLowerInvariant() })).GetEnumerator()) {
        $plan[$entry.Key -replace ':', '__'] = $entry.Value
    }
    $plan['DOTNET_GCHighMemPercent'] = '0x{0:X}' -f $GCHighMemPercent
    $diag = Get-LiveDiagnosticsPlan -Role $Role -LiveHome $LiveHome -Level $Diagnostics -DotnetRoot (Get-LiveDotnetRoot)
    foreach ($setting in $diag.Settings) {
        if (-not $setting.Kept) { $plan[$setting.Name] = $setting.Value }
    }
    return $plan
}

function Get-LiveLogSettings {
    <#
    .SYNOPSIS
      The Logging:ArcaneCore configuration keys that give a process the log file <LiveHome>\logs\<stem>.debug.<ext> (key -> value, ordered):
      File:Path, and Json:Path when the legacy JSON sink is on. Computed by Set-LiveDaemonLogEnvironment itself (the two variables are
      saved and restored around the call), so the format rules exist once.
    #>
    param([Parameter(Mandatory)][string]$LiveHome, [string]$LiveProfile, [Parameter(Mandatory)][string]$LogStem)
    $names = 'Logging__ArcaneCore__File__Path', 'Logging__ArcaneCore__Json__Path'
    $saved = @{}
    foreach ($name in $names) { $saved[$name] = [System.Environment]::GetEnvironmentVariable($name) }
    try {
        Set-LiveDaemonLogEnvironment -Role World -LiveHome $LiveHome -LiveProfile $LiveProfile -LogStem $LogStem
        $settings = [ordered]@{}
        foreach ($name in $names) {
            $value = [System.Environment]::GetEnvironmentVariable($name)
            if ($value) { $settings[$name -replace '__', ':'] = $value }
        }
        return $settings
    } finally {
        foreach ($name in $names) { [System.Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    }
}

function ConvertTo-LiveConfigArguments {
    <# .SYNOPSIS  --Section:Key=value tokens for a command line (a daemon's command line wins over its environment). #>
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Settings)
    return @($Settings.GetEnumerator() | ForEach-Object { "--$($_.Key)=$($_.Value)" })
}

function Get-LiveProfilePorts {
    <#
    .SYNOPSIS
      The Auth:Port and World:Port the profile's appsettings.json configures, or the daemon defaults (3724, 8085) where it sets none.
    #>
    param([string]$LiveProfile)
    $ports = [ordered]@{ Auth = 3724; World = 8085 }
    if (-not $LiveProfile) { return $ports }
    $settings = Join-Path $LiveProfile 'appsettings.json'
    if (-not (Test-Path -LiteralPath $settings)) { return $ports }
    try { $root = Get-Content -Raw -LiteralPath $settings | ConvertFrom-Json } catch { return $ports }
    foreach ($section in 'Auth', 'World') {
        if ($root.PSObject.Properties[$section] -and $root.$section -and $root.$section.PSObject.Properties['Port']) {
            $parsed = 0
            if ([int]::TryParse([string]$root.$section.Port, [ref]$parsed) -and $parsed -ge 1 -and $parsed -le 65535) { $ports[$section] = $parsed }
        }
    }
    return $ports
}

function Get-LiveWindowCommand {
    <# .SYNOPSIS  The argument string of a launcher window: pwsh <this> runs `-NoExit -NoProfile -File <script> <args>`. The plan prints it and Start-LiveWindow runs exactly it. #>
    param([Parameter(Mandatory)][string]$Script, [string[]]$Arguments = @())
    return ConvertTo-LiveArgumentString (@('-NoExit', '-NoProfile', '-File', $Script) + $Arguments)
}

function Invoke-WithLiveEnvironment {
    <#
    .SYNOPSIS
      Runs -ScriptBlock with the given variables set in this process, then restores each to its previous value (or removes it). A window
      started inside inherits them; the variables never outlive the call. Returns the script block's output.
    #>
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Variables, [Parameter(Mandatory)][scriptblock]$ScriptBlock)
    $saved = [ordered]@{}
    foreach ($name in $Variables.Keys) { $saved[$name] = [System.Environment]::GetEnvironmentVariable($name) }
    try {
        foreach ($name in $Variables.Keys) { [System.Environment]::SetEnvironmentVariable($name, [string]$Variables[$name]) }
        & $ScriptBlock
    } finally {
        foreach ($name in $saved.Keys) { [System.Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    }
}

function Start-LiveWindow {
    <# .SYNOPSIS  Starts `pwsh <CommandLine>` in a new console window and returns its Process (handle opened). Windows only. #>
    param([Parameter(Mandatory)][string]$CommandLine)
    $process = Start-Process pwsh -ArgumentList $CommandLine -PassThru
    [void]$process.Handle # open the handle now: ExitCode is $null for a process whose handle was never opened
    return $process
}

# ---- preflight and waiting ---------------------------------------------------------------------------------------------------------

function Get-LiveListeningPorts {
    <# .SYNOPSIS  The TCP ports something on this machine listens on (no connection is made to any of them). #>
    return @([System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() | ForEach-Object { $_.Port } | Sort-Object -Unique)
}

function Get-LivePortOwner {
    <# .SYNOPSIS  The id of the process listening on -Port, or 0 (Windows: Get-NetTCPConnection). #>
    param([Parameter(Mandatory)][int]$Port)
    $connection = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($connection) { return [int]$connection.OwningProcess }
    return 0
}

function Find-LiveChildProcess {
    <# .SYNOPSIS  The live direct child of -ParentPid whose image is -ImageName (for example ArcaneCore.World.exe), or $null (Windows: CIM). #>
    param([Parameter(Mandatory)][int]$ParentPid, [Parameter(Mandatory)][string]$ImageName)
    $row = Get-CimInstance Win32_Process -Filter "ParentProcessId=$ParentPid AND Name='$ImageName'" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $row) { return $null }
    return Get-Process -Id ([int]$row.ProcessId) -ErrorAction SilentlyContinue
}

function Wait-LiveDaemonUp {
    <#
    .SYNOPSIS
      Waits until -ImageName runs as a child of -ParentPid and, when -Port is given, that child listens on it; then confirms it is
      still alive after a short settle. Returns the Process. Throws when the child (or -WatchPid) is gone first, or at -TimeoutSec.
    .DESCRIPTION
      The same evidence livecheck and server-status use: a live process and its listener (server-status's PROCESSES section lists
      the listener's owner). The daemons' own startup lines ("Logon daemon listening", "World daemon listening") are printed at the
      moment the listener binds, so the port is the same signal without parsing a log whose name and format depend on the profile.
      -Port 0 waits only for the process to stay alive for the settle time (the cluster supervisor itself has no listener).
      -FollowRelaunch is for a child of the cluster supervisor: one that exits while it starts is relaunched by the supervisor, so the wait
      continues for the image's next process (a child of -ParentPid again) within -TimeoutSec instead of throwing; -WatchPid (the
      supervisor) going away still stops it. Without the switch (a daemon this script started itself) an early exit throws.
    #>
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][int]$ParentPid,
        [Parameter(Mandatory)][string]$ImageName,
        [int]$Port = 0,
        [int]$TimeoutSec = 600,
        [int]$WatchPid = 0,
        [int]$SettleSeconds = 3,
        [switch]$FollowRelaunch
    )
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    $seen = $null
    $parent = Get-Process -Id $ParentPid -ErrorAction SilentlyContinue
    while ($true) {
        $child = Find-LiveChildProcess -ParentPid $ParentPid -ImageName $ImageName
        if ($child) {
            $seen = $child
            if ($Port -eq 0 -or (Get-LivePortOwner -Port $Port) -eq $child.Id) {
                Start-Sleep -Seconds $SettleSeconds
                $child.Refresh()
                if (-not $child.HasExited) { return $child }
                if (-not $FollowRelaunch) { throw "$Label (pid $($child.Id)) exited within $SettleSeconds s of coming up (exit code $($child.ExitCode)); see its console window and log." }
                Write-Host "$Label (pid $($child.Id)) exited within $SettleSeconds s of coming up (exit code $($child.ExitCode)); its supervisor relaunches it, waiting for the new process."
                $seen = $null
            }
        } elseif ($null -ne $seen -and $FollowRelaunch) {
            Write-Host "$Label (pid $($seen.Id)) exited before it was up$(if ($seen.HasExited) { " (exit code $($seen.ExitCode))" }); its supervisor relaunches it, waiting for the new process."
            $seen = $null
        } elseif ($null -ne $seen) {
            throw "$Label (pid $($seen.Id)) exited before it was up$(if ($seen.HasExited) { " (exit code $($seen.ExitCode))" }); see its console window and log."
        } elseif ($parent -and $parent.HasExited) {
            throw "$Label never started: its console window (pid $ParentPid) exited with code $($parent.ExitCode)."
        }
        if ($WatchPid -gt 0 -and -not (Get-Process -Id $WatchPid -ErrorAction SilentlyContinue)) {
            throw "$Label is not up and the process that launches it (pid $WatchPid) has exited; see its console window."
        }
        if ([DateTime]::UtcNow -gt $deadline) {
            throw "$Label was not up after $TimeoutSec s ($(if ($Port) { "port $Port not listening" } else { 'process not running' })); nothing was stopped."
        }
        Start-Sleep -Seconds 1
    }
}

function Test-LivePidAlive {
    param([Parameter(Mandatory)][int]$ProcessId)
    return $null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)
}

# ---- plan output -------------------------------------------------------------------------------------------------------------------

function Write-LivePlan {
    <#
    .SYNOPSIS
      Prints a launch plan: the build, the preflight verdict and every process (window title, command line, environment, wait) a launcher
      would start. -Steps are objects with Name, Title, Command (the argument string), Environment (ordered), Wait and Note, and optionally
      Program (default pwsh).
      Secrets are masked by name and by value (-Secrets). Starts nothing.
    #>
    param(
        [Parameter(Mandatory)][string]$Heading,
        [Parameter(Mandatory)][string[]]$Header,
        [Parameter(Mandatory)][object[]]$Steps,
        [string[]]$Secrets = @()
    )
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("PLAN $Heading (nothing was started)")
    foreach ($h in $Header) { $lines.Add($h) }
    $index = 0
    foreach ($step in $Steps) {
        $index++
        $lines.Add('')
        $lines.Add("[$index] $($step.Name)")
        $lines.Add("    title:   $($step.Title)")
        $program = if ($step.PSObject.Properties['Program']) { $step.Program } else { 'pwsh' }
        $lines.Add("    command: $program $($step.Command)")
        if ($step.Environment -and $step.Environment.Count -gt 0) {
            $lines.Add('    env:')
            foreach ($entry in $step.Environment.GetEnumerator()) { $lines.Add("      $($entry.Key)=$(Format-LiveEnvValue -Name $entry.Key -Value ([string]$entry.Value))") }
        }
        if ($step.Wait) { $lines.Add("    wait:    $($step.Wait)") }
        if ($step.Note) { $lines.Add("    note:    $($step.Note)") }
    }
    return Hide-LiveSecrets -Text ($lines -join [Environment]::NewLine) -Secrets $Secrets
}

function Get-LiveLaunchProblems {
    <#
    .SYNOPSIS
      Why a launch must be refused right now (empty when none): a live daemon is already running (Get-LiveDaemon for Realm and World, which
      also reports two of one; Get-Process for -ExtraImages such as ArcaneCore.Gateway), or a port the launch needs is already listening.
      -Ports maps a label to a port number. Reads process and socket tables only.
    #>
    param([System.Collections.IDictionary]$Ports = @{}, [string[]]$ExtraImages = @())
    $problems = [System.Collections.Generic.List[string]]::new()
    foreach ($role in 'Realm', 'World') {
        try {
            $running = Get-LiveDaemon -Role $role
            if ($running) { $problems.Add("ArcaneCore.$role is already running (pid $($running.Id)); stop it first (a launch never replaces a running daemon).") }
        } catch { $problems.Add($_.Exception.Message) }
    }
    foreach ($image in $ExtraImages) {
        $found = @(Get-Process -Name $image -ErrorAction SilentlyContinue)
        if ($found.Count -gt 0) { $problems.Add("$image is already running (pid $($found.Id -join ', ')); stop it first.") }
    }
    $listening = Get-LiveListeningPorts
    foreach ($label in ($Ports.Keys | Sort-Object)) {
        if ($listening -contains [int]$Ports[$label]) { $problems.Add("$label port $($Ports[$label]) is already in use by another process.") }
    }
    return $problems.ToArray()
}
