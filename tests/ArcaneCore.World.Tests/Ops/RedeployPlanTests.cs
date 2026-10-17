using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ArcaneCore.World.Ops.Control;
using ArcaneCore.World.Tests.Ops.Control;
using ArcaneCore.World.Tests.Reload;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// The redeploy planner (tools/ops/live/RedeployPlan.ps1, docs/ops/redeploy.md): a changed-file set, configuration keys, content tables
/// and module builds go in, the cheapest safe action comes out (None, Hot with its .reload/.hotmodule steps, Restart, Blocked). The
/// fixtures run through one pwsh process; the git, configuration and control-pipe client parts run against real files, a real
/// repository and a real World's control pipe.
/// </summary>
public sealed class RedeployPlanTests(ITestOutputHelper output)
{
    private static readonly string Root = RepositorySource.RequireRoot();
    private static readonly string Library = Path.Combine(Root, "tools", "ops", "live", "RedeployPlan.ps1");
    private static readonly string Catalog = Path.Combine(Root, "src", "ArcaneCore.Kernel", "Configuration", "Conf", "conf-catalog.json");

    /// <summary>One planner fixture: the inputs as PowerShell parameter text, the expected action, and what the plan must say.</summary>
    public sealed record Fixture(string Name, string Arguments, string Action, string[] Steps, string? Reason = null);

    private const string Gm = "@{ Name = 'GmCommands'; Dll = 'C:\\b\\GmCommands.dll'; Sha256 = 'AA'; Stale = $false; Allowlisted = $true }";

    public static readonly Fixture[] Fixtures =
    [
        new("docs, tests and tools only", "-ChangedPaths 'docs/x.md','tests/A/B.cs','tools/ops/live/redeploy.ps1','README.md','.github/workflows/x.yml'", "None", []),
        new("a project the daemons are not built from", "-ChangedPaths 'src/ArcaneCore.Gateway/Program.cs'", "None", []),
        new("nothing changed", "", "None", []),
        new("server code", "-ChangedPaths 'docs/a.md','src/ArcaneCore.World/WorldHost.cs'", "Restart", [], "src/ArcaneCore.World/WorldHost.cs"),
        new("a shared core project", "-ChangedPaths 'src/ArcaneCore.Kernel/Ops/X.cs'", "Restart", [], "src/ArcaneCore.Kernel/Ops/X.cs"),
        new("a build input at the root", "-ChangedPaths 'Directory.Build.props'", "Restart", [], "Directory.Build.props"),
        new("a schema update", "-ChangedPaths 'sql/updates/world/sqlite/0100.sql'", "Restart", [], "sql/updates/world/sqlite/0100.sql"),
        new("an unknown root file", "-ChangedPaths 'global.json'", "Restart", [], "global.json"),
        new("a live config key", "-ConfigChanges 'World:Motd'", "Hot", ["reload config"]),
        new("two live config keys", "-ConfigChanges 'World:Motd','World:AllowTwoSideChat'", "Hot", ["reload config"]),
        new("a restart-only config key", "-ConfigChanges 'World:Motd','World:TickIntervalMs'", "Restart", [], "World:TickIntervalMs (restart)"),
        new("a key read once at start", "-ConfigChanges 'World:Console:Enabled'", "Restart", [], "World:Console:Enabled (start)"),
        new("a realm key", "-ConfigChanges 'Realm:Console:Enabled'", "Restart", [], "Realm:Console:Enabled (realm)"),
        new("an unknown key", "-ConfigChanges 'World:NoSuchKey'", "Restart", [], "World:NoSuchKey (unknown)"),
        new("a host framework key", "-ConfigChanges 'Logging:LogLevel:Default'", "Restart", [], "Logging:LogLevel:Default (framework)"),
        new("no configuration baseline", "-ConfigBaseline $false", "Restart", [], "no configuration fingerprint"),
        new("reloadable content", "-ContentTables 'spell_template','npc_vendor','spell_template'", "Hot", ["reload npc_vendor", "reload spell_template"]),
        new("content without a reload", "-ContentTables 'creature'", "Restart", [], "'creature' has no .reload"),
        new("an allowlisted module", $"-ChangedPaths 'tools/hotmodule-template/GmCommands/GmCommands.cs' -ModuleBuilds @{{ GmCommands = [pscustomobject]{Gm} }}", "Hot", ["hotmodule reload GmCommands"]),
        new("a module not on the allowlist", "-ChangedPaths 'tools/hotmodule-template/GmCommands/GmCommands.cs' -ModuleBuilds @{ GmCommands = [pscustomobject]@{ Name = 'GmCommands'; Dll = 'x.dll'; Sha256 = 'BB'; Stale = $false; Allowlisted = $false } }", "Blocked", [], "sha256 BB is not on the module allowlist"),
        new("a module not built", "-ChangedPaths 'tools/hotmodule-template/GmCommands/GmCommands.cs'", "Blocked", [], "module GmCommands changed but is not built"),
        new("a stale module build", "-ChangedPaths 'tools/hotmodule-template/GmCommands/GmCommands.cs' -ModuleBuilds @{ GmCommands = [pscustomobject]@{ Name = 'GmCommands'; Dll = 'x.dll'; Sha256 = 'AA'; Stale = $true; Allowlisted = $true } }", "Blocked", [], "is older than its changed source"),
        new("a blocked module with -ForceRestart", "-ChangedPaths 'tools/hotmodule-template/GmCommands/GmCommands.cs' -ForceRestart $true", "Restart", [], "-ForceRestart"),
        new("a blocked module beside server code", "-ChangedPaths 'tools/hotmodule-template/GmCommands/GmCommands.cs','src/ArcaneCore.World/X.cs'", "Blocked", [], "not built"),
        new("config, content and a module together", $"-ConfigChanges 'World:Motd' -ContentTables 'quest_template' -ChangedPaths 'docs/a.md','tools/hotmodule-template/GmCommands/GmCommands.cs' -ModuleBuilds @{{ GmCommands = [pscustomobject]{Gm} }}", "Hot", ["reload config", "reload quest_template", "hotmodule reload GmCommands"]),
        new("hot changes beside server code", "-ConfigChanges 'World:Motd' -ChangedPaths 'src/ArcaneCore.Game/Maps/WorldRuntime.cs'", "Restart", [], "src/ArcaneCore.Game/Maps/WorldRuntime.cs"),
        new("no recorded deploy", "-Baseline none -ConfigChanges 'World:Motd'", "Restart", [], "no deployed SHA is recorded"),
        new("a recorded SHA this repository lacks", "-Baseline unreachable", "Restart", [], "not a commit of this repository"),
        new("a world database swap", "-WorldDbSwap $true -ContentTables 'spell_template'", "Restart", [], "-WorldDbSwap"),
        new("the World not running", "-WorldRunning $false -ConfigChanges 'World:Motd'", "Restart", [], "the World is not running"),
        new("no control pipe", "-ConfigChanges 'World:Motd' -Capabilities @{ Pipe = $false; Reload = $true; Modules = $true }", "Restart", [], "no control pipe"),
        new(".reload off", "-ContentTables 'spell_template' -Capabilities @{ Pipe = $true; Reload = $false; Modules = $true }", "Restart", [], "HotReload:Commands is off"),
        new("the module lane off", $"-ChangedPaths 'tools/hotmodule-template/GmCommands/GmCommands.cs' -ModuleBuilds @{{ GmCommands = [pscustomobject]{Gm} }} -Capabilities @{{ Pipe = $true; Reload = $true; Modules = $false }}", "Restart", [], "World:HotCode:Modules:Enabled is off"),
        new("-ForceRestart with nothing changed", "-ForceRestart $true", "Restart", [], "-ForceRestart"),
    ];

    public static IEnumerable<object[]> FixtureNames() => Fixtures.Select(f => new object[] { f.Name });

    private static readonly Lazy<Dictionary<string, JsonElement>> Plans = new(RunFixtures);

    private static Dictionary<string, JsonElement> RunFixtures()
    {
        var script = new StringBuilder();
        script.AppendLine($". '{Quote(Library)}'");
        script.AppendLine($"$cat = Import-ConfCatalog -Path '{Quote(Catalog)}'");
        script.AppendLine("$deployed = @('src/ArcaneCore.Cryptography/','src/ArcaneCore.Data/','src/ArcaneCore.Game/','src/ArcaneCore.Kernel/','src/ArcaneCore.Protocol/','src/ArcaneCore.Realm/','src/ArcaneCore.World.Contracts/','src/ArcaneCore.World/')");
        script.AppendLine("$modules = @([pscustomobject]@{ Name = 'GmCommands'; Dir = 'tools/hotmodule-template/GmCommands/' })");
        script.AppendLine("$plans = [ordered]@{}");
        foreach (Fixture fixture in Fixtures)
        {
            script.AppendLine($"$plans['{Quote(fixture.Name)}'] = Get-RedeployPlan -Catalog $cat -DeployedProjectDirs $deployed -OtherProjectDirs @('src/ArcaneCore.Gateway/') -ModuleProjects $modules {fixture.Arguments}");
        }

        script.AppendLine("$plans | ConvertTo-Json -Depth 6 -Compress");
        string json = Pwsh(script.ToString());
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    [PwshTheory]
    [MemberData(nameof(FixtureNames))]
    public void Fixture_GivesTheExpectedPlan(string name)
    {
        Fixture fixture = Fixtures.Single(f => f.Name == name);
        JsonElement plan = Plans.Value[name];
        output.WriteLine(plan.ToString());

        Assert.Equal(fixture.Action, plan.GetProperty("Action").GetString());
        string[] steps = [.. plan.GetProperty("Steps").EnumerateArray().Select(s => s.GetProperty("Command").GetString()!)];
        if (fixture.Action == "Hot")
        {
            Assert.Equal(fixture.Steps, steps);
        }

        if (fixture.Reason is { } reason)
        {
            string[] reasons = [.. plan.GetProperty("RestartReasons").EnumerateArray().Concat(plan.GetProperty("BlockedReasons").EnumerateArray()).Select(r => r.GetString()!)];
            Assert.Contains(reasons, r => r.Contains(reason, StringComparison.Ordinal));
        }

        if (fixture.Action is "None" or "Hot")
        {
            Assert.Empty(plan.GetProperty("RestartReasons").EnumerateArray());
            Assert.Empty(plan.GetProperty("BlockedReasons").EnumerateArray());
        }
    }

    /// <summary>The planner's reloadable names are exactly the World's reloadables except config (pinned there by ReloadAllMembershipTests).</summary>
    [PwshFact]
    public void ReloadableContent_IsTheWorldsReloadables()
    {
        string json = Pwsh($". '{Quote(Library)}'; $script:ReloadableContent | ConvertTo-Json -Compress");
        string[] listed = JsonSerializer.Deserialize<string[]>(json)!;
        Assert.Equal(
            [.. ReloadAllMembershipTests.Expected.Keys.Where(k => k != "config").Order(StringComparer.Ordinal)],
            [.. listed.Order(StringComparer.Ordinal)]);
    }

    /// <summary>The real repository: the daemon closure is computed from the .csproj references plus the built-in packs, and only the gateway is left out.</summary>
    [PwshFact]
    public void DeployedProjects_AreTheWorldAndRealmClosure()
    {
        string json = Pwsh($". '{Quote(Library)}'; $d = Get-DeployedProjectDirs -Src '{Quote(Root)}'; @{{ d = @($d); o = @(Get-OtherProjectDirs -Src '{Quote(Root)}' -DeployedProjectDirs $d) }} | ConvertTo-Json -Compress");
        using JsonDocument document = JsonDocument.Parse(json);
        string[] deployed = [.. document.RootElement.GetProperty("d").EnumerateArray().Select(e => e.GetString()!)];
        string[] other = [.. document.RootElement.GetProperty("o").EnumerateArray().Select(e => e.GetString()!)];
        Assert.Contains("src/ArcaneCore.World/", deployed);
        Assert.Contains("src/ArcaneCore.Realm/", deployed);
        Assert.Contains("src/ArcaneCore.Kernel/", deployed);
        Assert.Contains("src/ArcaneCore.World.Contracts/", deployed);
        // The Playerbots pack is no ProjectReference of either daemon, but the World loads it from beside its executable.
        Assert.Contains("src/ArcaneCore.Playerbots/", deployed);
        Assert.DoesNotContain("src/ArcaneCore.Gateway/", deployed);
        Assert.Contains("src/ArcaneCore.Gateway/", other);
    }

    /// <summary>
    /// The changed paths since a recorded deploy: committed changes, uncommitted ones, untracked files, and the paths that were dirty when
    /// the record was written. No record, or a SHA the repository lacks, is no baseline at all.
    /// </summary>
    [PwshFact]
    public void ChangedPaths_CoverCommittedDirtyUntrackedAndRecordedDirtyPaths()
    {
        using var box = new TempDir();
        string repo = box.Dir("repo");
        Git(repo, "init", "-q");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "1");
        File.WriteAllText(Path.Combine(repo, "b.txt"), "1");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "ignored.txt\n");
        Git(repo, "add", ".");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "one");
        string deployed = Git(repo, "rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(repo, "b.txt"), "2");
        Git(repo, "add", ".");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "two");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "dirty");
        File.WriteAllText(Path.Combine(repo, "new.txt"), "untracked");
        File.WriteAllText(Path.Combine(repo, "ignored.txt"), "ignored");

        string json = Pwsh($$"""
            . '{{Quote(Library)}}'
            $known = Get-ChangedPaths -Src '{{Quote(repo)}}' -Record @{ sha = '{{deployed}}'; dirtyPaths = @('c.txt') }
            $none = Get-ChangedPaths -Src '{{Quote(repo)}}' -Record $null
            $missing = Get-ChangedPaths -Src '{{Quote(repo)}}' -Record @{ sha = '0123456789abcdef0123456789abcdef01234567' }
            $state = Get-WorktreeState -Src '{{Quote(repo)}}'
            @{ known = $known; none = $none.Baseline; missing = $missing.Baseline; sha = $state.Sha; dirty = @($state.DirtyPaths) } | ConvertTo-Json -Depth 4 -Compress
            """);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement known = document.RootElement.GetProperty("known");
        Assert.Equal("known", known.GetProperty("Baseline").GetString());
        Assert.Equal(["a.txt", "b.txt", "c.txt", "new.txt"], known.GetProperty("Paths").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal("none", document.RootElement.GetProperty("none").GetString());
        Assert.Equal("unreachable", document.RootElement.GetProperty("missing").GetString());
        Assert.Equal(Git(repo, "rev-parse", "HEAD").Trim(), document.RootElement.GetProperty("sha").GetString());
        Assert.Equal(["a.txt", "new.txt"], document.RootElement.GetProperty("dirty").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    /// <summary>
    /// The configuration fingerprint covers appsettings.json, appsettings.Development.json and world.conf (later ones win), stores no value,
    /// and a changed value, an added key and a removed key each show up as changed.
    /// </summary>
    [PwshFact]
    public void ConfigFingerprint_HashesTheEffectiveValues_AndFindsChangedKeys()
    {
        using var box = new TempDir();
        string profile = box.Dir("profile");
        File.WriteAllText(Path.Combine(profile, "appsettings.json"), """{ "World": { "Motd": "json", "Port": 8085 }, "Database": { "World": { "Password": "s3cret-value" } }, "List": [ "a", "b" ] }""");
        File.WriteAllText(Path.Combine(profile, "appsettings.Development.json"), """{ "World": { "AllowTwoSideChat": true } }""");
        File.WriteAllText(Path.Combine(profile, "world.conf"), "# comment\nWorld.Motd = \"conf\"\n");

        string json = Pwsh($$"""
            . '{{Quote(Library)}}'
            $before = Get-ConfigFingerprint -Configuration (Get-ProfileConfiguration -LiveProfile '{{Quote(profile)}}')
            $effective = Get-ProfileConfiguration -LiveProfile '{{Quote(profile)}}'
            Set-Content -LiteralPath '{{Quote(Path.Combine(profile, "world.conf"))}}' -Value "World.Motd = changed`nWorld.ListenRangeSay = 30"
            Set-Content -LiteralPath '{{Quote(Path.Combine(profile, "appsettings.Development.json"))}}' -Value '{}'
            $after = Get-ConfigFingerprint -Configuration (Get-ProfileConfiguration -LiveProfile '{{Quote(profile)}}')
            @{ record = ($before | ConvertTo-Json -Compress); motd = $effective['World:Motd']; list = $effective['List:1']; changed = @(Compare-ConfigFingerprint -Old $before -New $after); same = @(Compare-ConfigFingerprint -Old $before -New $before) } | ConvertTo-Json -Depth 4 -Compress
            """);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal("conf", root.GetProperty("motd").GetString());
        Assert.Equal("b", root.GetProperty("list").GetString());
        Assert.DoesNotContain("s3cret-value", root.GetProperty("record").GetString(), StringComparison.Ordinal);
        Assert.Contains("Database:World:Password", root.GetProperty("record").GetString(), StringComparison.Ordinal);
        Assert.Equal(["World:AllowTwoSideChat", "World:ListenRangeSay", "World:Motd"], root.GetProperty("changed").EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(root.GetProperty("same").EnumerateArray());
    }

    /// <summary>
    /// End to end over a miniature repository, live home and profile: a module source change with an approved build plans a hot module
    /// reload; the same change with an unapproved build is Blocked; a changed live key in world.conf adds .reload config; a server code
    /// change turns all of it into a restart; and a missing deploy record is a restart.
    /// </summary>
    [PwshFact]
    public void LivePlan_FromARepositoryAProfileAndADeployRecord()
    {
        using var box = new TempDir();
        string repo = box.Dir("repo");
        WriteProject(repo, "src/ArcaneCore.Kernel/ArcaneCore.Kernel.csproj", []);
        WriteProject(repo, "src/ArcaneCore.World/ArcaneCore.World.csproj", ["..\\ArcaneCore.Kernel\\ArcaneCore.Kernel.csproj"]);
        WriteProject(repo, "src/ArcaneCore.Realm/ArcaneCore.Realm.csproj", ["..\\ArcaneCore.Kernel\\ArcaneCore.Kernel.csproj"]);
        WriteProject(repo, "src/ArcaneCore.Gateway/ArcaneCore.Gateway.csproj", ["..\\ArcaneCore.World\\ArcaneCore.World.csproj"]);
        WriteProject(repo, "mods/Hello/Hello.csproj", []);
        File.WriteAllText(Path.Combine(repo, "mods", "Hello", "Hello.cs"), "// v1");
        File.WriteAllText(Path.Combine(repo, "src", "ArcaneCore.World", "World.cs"), "// v1");
        Directory.CreateDirectory(Path.Combine(repo, "src", "ArcaneCore.Kernel", "Configuration", "Conf"));
        File.Copy(Catalog, Path.Combine(repo, "src", "ArcaneCore.Kernel", "Configuration", "Conf", "conf-catalog.json"));
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "bin/\nobj/\n");
        Git(repo, "init", "-q");
        Git(repo, "add", ".");
        Git(repo, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "deployed");
        string deployedSha = Git(repo, "rev-parse", "HEAD").Trim();

        string profile = box.Dir("profile");
        string modules = box.Dir("profile/modules");
        Directory.CreateDirectory(Path.Combine(modules, "Hello"));
        File.WriteAllText(Path.Combine(profile, "appsettings.json"), """
            { "HotReload": { "Commands": true },
              "World": { "Motd": "v1", "Control": { "Pipe": { "Enabled": true } },
                         "HotCode": { "Modules": { "Enabled": true, "Directory": "modules", "Allowlist": "allow.txt" } } } }
            """);
        string liveHome = box.Dir("live");
        Directory.CreateDirectory(Path.Combine(liveHome, "bin"));

        // The module changes and is rebuilt after the source change.
        File.WriteAllText(Path.Combine(repo, "mods", "Hello", "Hello.cs"), "// v2");
        string dll = Path.Combine(repo, "mods", "Hello", "bin", "Debug", "net11.0", "Hello.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        File.WriteAllText(dll, "module v2 bytes");
        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddMinutes(1));
        string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(dll)));

        string Plan(string record) => Pwsh($$"""
            . '{{Quote(Library)}}'
            {{record}}
            $live = Get-LiveRedeployPlan -Src '{{Quote(repo)}}' -LiveHome '{{Quote(liveHome)}}' -LiveProfile '{{Quote(profile)}}'
            @{ action = $live.Plan.Action; steps = @($live.Plan.Steps | ForEach-Object Command); reasons = @($live.Plan.RestartReasons) + @($live.Plan.BlockedReasons); module = $live.ModuleDirectory } | ConvertTo-Json -Compress
            """);

        string writeRecord = $$"""
            $fp = Get-ConfigFingerprint -Configuration (Get-ProfileConfiguration -LiveProfile '{{Quote(profile)}}')
            Write-DeployRecord -LiveHome '{{Quote(liveHome)}}' -Record ([ordered]@{ schema = 1; sha = '{{deployedSha}}'; dirtyPaths = @(); config = $fp })
            """;

        // Not approved yet.
        File.WriteAllText(Path.Combine(profile, "allow.txt"), "# nothing approved\n");
        using (JsonDocument blocked = JsonDocument.Parse(Plan(writeRecord)))
        {
            Assert.Equal("Blocked", blocked.RootElement.GetProperty("action").GetString());
            Assert.Contains(blocked.RootElement.GetProperty("reasons").EnumerateArray(), r => r.GetString()!.Contains($"sha256 {sha} is not on the module allowlist", StringComparison.Ordinal));
        }

        // Approved: a hot module reload; plus a changed live key in world.conf: .reload config first.
        File.WriteAllText(Path.Combine(profile, "allow.txt"), sha + "  Hello v2\n");
        using (JsonDocument hot = JsonDocument.Parse(Plan(writeRecord)))
        {
            Assert.Equal("Hot", hot.RootElement.GetProperty("action").GetString());
            Assert.Equal(["hotmodule reload Hello"], hot.RootElement.GetProperty("steps").EnumerateArray().Select(e => e.GetString()!).ToArray());
        }

        string record = Path.Combine(liveHome, "bin", "arcanecore-deploy.json");
        Pwsh($". '{Quote(Library)}'\n{writeRecord}");
        File.WriteAllText(Path.Combine(profile, "world.conf"), "World.Motd = v2\n");
        using (JsonDocument both = JsonDocument.Parse(Plan(string.Empty)))
        {
            Assert.Equal("Hot", both.RootElement.GetProperty("action").GetString());
            Assert.Equal(["reload config", "hotmodule reload Hello"], both.RootElement.GetProperty("steps").EnumerateArray().Select(e => e.GetString()!).ToArray());
        }

        // Server code changed too: restart.
        File.WriteAllText(Path.Combine(repo, "src", "ArcaneCore.World", "World.cs"), "// v2");
        using (JsonDocument restart = JsonDocument.Parse(Plan(string.Empty)))
        {
            Assert.Equal("Restart", restart.RootElement.GetProperty("action").GetString());
            Assert.Contains(restart.RootElement.GetProperty("reasons").EnumerateArray(), r => r.GetString()!.Contains("src/ArcaneCore.World/World.cs", StringComparison.Ordinal));
        }

        // No record: restart.
        File.Delete(record);
        using JsonDocument none = JsonDocument.Parse(Plan(string.Empty));
        Assert.Equal("Restart", none.RootElement.GetProperty("action").GetString());
        Assert.Contains(none.RootElement.GetProperty("reasons").EnumerateArray(), r => r.GetString()!.Contains("no deployed SHA is recorded", StringComparison.Ordinal));
    }

    /// <summary>
    /// The client half of the hot path against a real World's control pipe: Invoke-HotReloadStep sends .reload, waits for the result that
    /// arrives after the command's done line, and confirms Applied in .reload status; a pipe served by any process but the expected World
    /// is refused before a command is sent.
    /// </summary>
    [PwshFact]
    public async Task HotReloadStep_ThroughARealControlPipe_AppliesAndConfirms()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var host = WorldTestHost.Start();
        var logs = new WorldControlPipeTests.CapturingLoggerProvider();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(logs));
        WorldControlPipe pipe = WorldControlPipeTests.NewPipe(host, new WorldControlPipeOptions { Enabled = true, Name = WorldControlPipeTests.UniqueName() }, loggers);
        await pipe.StartAsync(CancellationToken.None);
        try
        {
            string json = await Task.Run(() => Pwsh($$"""
                . '{{Quote(Library)}}'
                $session = Connect-WorldControl -PipeName '{{pipe.PipeName}}' -ExpectedPid {{Environment.ProcessId}}
                try { $result = Invoke-HotReloadStep -Session $session -Name spell_template -Reason test } finally { Close-WorldControl $session }
                $refused = $null
                try { $s = Connect-WorldControl -PipeName '{{pipe.PipeName}}' -ExpectedPid 4 -TimeoutMs 5000; Close-WorldControl $s } catch { $refused = $_.Exception.Message }
                @{ result = $result; refused = $refused } | ConvertTo-Json -Compress
                """));
            using JsonDocument document = JsonDocument.Parse(json);
            Assert.StartsWith("spell_template reloaded:", document.RootElement.GetProperty("result").GetString(), StringComparison.Ordinal);
            Assert.Contains("not the World (4): refusing to send commands", document.RootElement.GetProperty("refused").GetString(), StringComparison.Ordinal);
            Assert.Contains(logs.Entries, e => e.Message.StartsWith("Control pipe command: reload spell_template test", StringComparison.Ordinal));
        }
        finally
        {
            await pipe.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>The hot step reports a reloadable the World does not have as unavailable (so redeploy falls back to the restart).</summary>
    [PwshFact]
    public async Task HotReloadStep_ForAnUnknownReloadable_IsUnavailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var host = WorldTestHost.Start();
        using var loggers = LoggerFactory.Create(b => b.AddProvider(new WorldControlPipeTests.CapturingLoggerProvider()));
        WorldControlPipe pipe = WorldControlPipeTests.NewPipe(host, new WorldControlPipeOptions { Enabled = true, Name = WorldControlPipeTests.UniqueName() }, loggers);
        await pipe.StartAsync(CancellationToken.None);
        try
        {
            string json = await Task.Run(() => Pwsh($$"""
                . '{{Quote(Library)}}'
                $plan = [pscustomobject]@{ Steps = @([pscustomobject]@{ Kind = 'content'; Name = 'no_such_table'; Command = 'reload no_such_table' }) }
                $config = @{ 'World:Control:Pipe:Name' = '{{pipe.PipeName}}' }
                Invoke-HotRedeploy -Plan $plan -WorldPid {{Environment.ProcessId}} -Configuration $config | ConvertTo-Json -Compress
                """));
            using JsonDocument document = JsonDocument.Parse(json);
            Assert.Equal("Unavailable", document.RootElement.GetProperty("Outcome").GetString());
            Assert.Contains("There is no reloadable 'no_such_table'", document.RootElement.GetProperty("Detail").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            await pipe.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>redeploy.ps1 plans before it touches anything, and -Plan / -WhatIf stop before the first side effect.</summary>
    [Fact]
    public void Redeploy_PlansFirst_AndStopsOnPlanOrWhatIf()
    {
        string script = File.ReadAllText(Path.Combine(Root, "tools", "ops", "live", "redeploy.ps1"));
        int plan = script.IndexOf("Get-LiveRedeployPlan", StringComparison.Ordinal);
        int stop = script.IndexOf("if ($Plan -or $WhatIfPreference) { exit 0 }", StringComparison.Ordinal);
        int firstStop = script.IndexOf("Stop-Graceful World $world.Id", StringComparison.Ordinal);
        int firstCopy = script.IndexOf("robocopy", StringComparison.Ordinal);
        int hot = script.IndexOf("Invoke-HotRedeploy", StringComparison.Ordinal);
        Assert.True(plan > 0 && stop > plan, "redeploy.ps1 must plan, then stop on -Plan/-WhatIf");
        Assert.True(firstStop > stop && firstCopy > stop && hot > stop, "nothing may touch the live server before the -Plan/-WhatIf exit");
        Assert.Contains("[CmdletBinding(SupportsShouldProcess)]", script, StringComparison.Ordinal);
        Assert.Contains("Write-DeployRecord -LiveHome $LiveHome -Record (New-DeployRecordData -Live $live -Src $Src -Method restart", script, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------------------

    private static void WriteProject(string repo, string relative, string[] references)
    {
        string path = Path.Combine(repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string items = string.Concat(references.Select(r => $"<ProjectReference Include=\"{r}\" />"));
        File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>{items}</ItemGroup></Project>");
    }

    private static string Quote(string text) => text.Replace("'", "''", StringComparison.Ordinal);

    private static string Git(string repo, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(repo);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error.Result}");
        return text;
    }

    private static string Pwsh(string script)
    {
        string file = Path.Combine(Path.GetTempPath(), "arcane-redeploy-plan-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(file, "$ErrorActionPreference = 'Stop'\n" + script, new UTF8Encoding(true));
        try
        {
            var start = new ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-File", file])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using Process process = Process.Start(start)!;
            Task<string> error = process.StandardError.ReadToEndAsync();
            string text = process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(120_000), "pwsh did not finish within 120 s");
            Assert.True(process.ExitCode == 0, $"pwsh exit {process.ExitCode}: {error.Result}\n{text}");
            return text;
        }
        finally
        {
            File.Delete(file);
        }
    }

    private sealed class TempDir : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "arcane-redeploy-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(_root);

        public string Dir(string relative)
        {
            string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal); // git object files are read-only
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    private static class PwshAvailable
    {
        public static readonly Lazy<bool> Value = new(() =>
        {
            try
            {
                var start = new ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "$PSVersionTable.PSVersion.Major"])
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using Process process = Process.Start(start)!;
                string text = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return process.ExitCode == 0 && int.TryParse(text, out int major) && major >= 7;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
        });
    }

    private sealed class PwshFactAttribute : FactAttribute
    {
        public PwshFactAttribute()
        {
            if (!PwshAvailable.Value.Value)
            {
                Skip = "pwsh (PowerShell 7) is not on PATH on this host.";
            }
        }
    }

    private sealed class PwshTheoryAttribute : TheoryAttribute
    {
        public PwshTheoryAttribute()
        {
            if (!PwshAvailable.Value.Value)
            {
                Skip = "pwsh (PowerShell 7) is not on PATH on this host.";
            }
        }
    }
}
