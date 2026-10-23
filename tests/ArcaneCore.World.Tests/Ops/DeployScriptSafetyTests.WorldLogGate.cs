using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>
/// Bug #169: deploy-live.py and deploy-post-checks.py built <c>world_log_checks</c> (the expected world-log lines) and then set
/// <c>healthy=True</c> unconditionally, so a world that never loaded terrain, navmeshes or transports still ended the deploy healthy and had
/// its PIDs recorded in standard-processes.json. Both scripts now gate on one shared list (tools/ops, not tools/ops/deploy: the tests run
/// <c>python -I</c>), matched by prefix pattern rather than exact content counts, and validate <c>--set</c> before anything is touched.
/// </summary>
public sealed partial class DeployScriptSafetyTests
{
    private static readonly string[] BlockingLines =
    [
        "Terrain: reading .map files from D:/terrain",
        "Collision: vmaps from D:/terrain/vmaps",
        "Collision: navmeshes from D:/terrain/mmaps",
        "loaded 44 maps, 1081 areas",
        "loaded 122 graveyards and 191 graveyard-zone links",
    ];

    private static readonly string[] ElevatorLines = ["elevators and trams: 12 loaded"];

    private static readonly string[] BotLines = ["Started managed playerbot 1"];

    private static readonly string[] TransportLines = ["Transports: 9 routes, 9 ships sailing"];

    private static readonly string[] OptionalDataLines =
    [
        "Loaded 172 item sets",
        "Loaded 2012 random properties and 772 item_enchantment_template entries",
        "Loaded 1427 page texts",
        "Enchanting: 1460 enchantments",
        "appearance is checked against 3603 CharSections and 136 CharacterFacialHairStyles rows",
    ];

    [Fact]
    public void DeployScripts_ShareOneExpectedLogLineList_UnderToolsOps()
    {
        // Both scripts must import the same module that sits directly in tools/ops (importable under -I once tools/ops is on sys.path),
        // and neither may carry its own copy of the list.
        RequirePython();
        string opsDir = Path.Combine(Root, "tools", "ops");
        var shared = new List<HashSet<string>>();
        foreach (string script in new[] { DeployLive, PostChecks })
        {
            (int code, string output) = Python(["-c",
                "import ast,sys; t=ast.parse(open(sys.argv[1],encoding='utf-8').read()); n=set()\n"
                + "for x in ast.walk(t):\n"
                + "  if isinstance(x, ast.Import): n.update(a.name.split('.')[0] for a in x.names)\n"
                + "  elif isinstance(x, ast.ImportFrom) and x.module and x.level == 0: n.add(x.module.split('.')[0])\n"
                + "print(','.join(sorted(n)))",
                script]);
            Assert.True(code == 0, output);
            shared.Add(output.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Where(m => !m.Equals("opsenv", StringComparison.Ordinal) && File.Exists(Path.Combine(opsDir, m + ".py")))
                .ToHashSet(StringComparer.Ordinal));
        }

        string[] common = [.. shared[0].Intersect(shared[1])];
        Assert.True(common.Length > 0, "deploy-live.py and deploy-post-checks.py import no common helper module from tools/ops");

        string body = string.Concat(common.Select(m => File.ReadAllText(Path.Combine(opsDir, m + ".py"))));
        Assert.Contains("Terrain: reading .map files from", body);
        Assert.Contains("Collision: navmeshes from", body);
        foreach (string script in new[] { DeployLive, PostChecks })
        {
            string text = File.ReadAllText(script);
            Assert.DoesNotContain("Terrain: reading .map files from", text);
            Assert.DoesNotContain("expected_lines", text);
        }
    }

    [Fact]
    public void SharedExpectedLogLines_DoNotPinContentCounts()
    {
        // A content change (map, area or item-set count) must not turn a healthy deploy into a failed one once the lines gate.
        string opsDir = Path.Combine(Root, "tools", "ops");
        string[] pinned = ["1081 areas", "44 maps", "172 item sets", "1427 page texts", "9 routes", "3603 CharSections", "1460 enchantments"];
        foreach (string file in Directory.EnumerateFiles(opsDir, "*.py", SearchOption.TopDirectoryOnly))
        {
            string text = File.ReadAllText(file);
            if (!text.Contains("Terrain: reading .map files from", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string count in pinned)
            {
                Assert.DoesNotContain(count, text);
            }

            string module = Path.GetFileNameWithoutExtension(file);
            foreach (string script in new[] { DeployLive, PostChecks })
            {
                Assert.Contains("import " + module, File.ReadAllText(script));
            }

            return;
        }

        Assert.Fail("no shared module under tools/ops holds the expected world-log lines");
    }

    [Fact]
    public void DeployLive_MalformedSet_IsRejectedBeforeAnythingIsCreated()
    {
        // Valid DBC hashes and artifacts, so every earlier "nothing was stopped" check passes; the profile is empty, so a run that gets past
        // --set validation would die later on the missing appsettings.json instead.
        RequirePython();
        using var box = new Sandbox();
        string art = box.Dir("artifacts");
        foreach (string rel in new[]
                 {
                     "bin/ArcaneCore.World/release/ArcaneCore.World.dll", "bin/ArcaneCore.Realm/release/ArcaneCore.Realm.dll",
                     "bin/ArcaneCore.DbUpgrade/release/arcane-db.exe", "bin/ArcaneCore.AccountTool/release/arcane-account.exe",
                     "bin/ArcaneCore.MockClient/release/arcane-mock.exe", "bin/ArcaneCore.ContentImporter/release/arcane-content-importer.dll",
                     "tools/content/refresh-world-content.ps1", "tools/content/set-optional-data.ps1",
                 })
        {
            box.File(Path.Combine(art, rel), "x");
        }

        string dbc = box.Dir("dbc");
        string[] needed = ["AreaTrigger.dbc", "WorldSafeLocs.dbc", "Map.dbc", "AreaTable.dbc", "WMOAreaTable.dbc", "TaxiNodes.dbc", "TaxiPath.dbc", "TaxiPathNode.dbc"];
        var sums = new System.Text.StringBuilder();
        foreach (string n in needed)
        {
            string path = box.File(Path.Combine(dbc, n), "dbc " + n);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            sums.Append(hash).Append("  ").Append(n).Append('\n');
        }

        box.File(Path.Combine(dbc, "SHA256SUMS"), sums.ToString());
        string dump = box.File(Path.Combine(box.Dir("refs"), "dump.sql.gz"), "x");
        string outDir = Path.Combine(box.Root, "run");

        (int code, string output) = Python([DeployLive, "--profile", box.Dir("profile"), "--label", "t", "--out", outDir, "--artifacts", art,
            "--terrain", "", "--dbc-dir", dbc, "--dump", dump, "--no-optional-data", "--no-elevators", "--servers-stopped", "--set", "NoEqualsSign"]);

        Assert.NotEqual(0, code);
        Assert.Contains("--set expects", output);
        Assert.False(Directory.Exists(outDir), "the run directory was created before --set was validated\n" + output);
    }

    [WindowsFact]
    public void PostChecks_MissingBlockingLogLine_RefusesAndLeavesTheRunUnrecorded()
    {
        RequirePython();
        string[] all = [.. BlockingLines, .. ElevatorLines, .. TransportLines, .. OptionalDataLines];

        // Control: the complete log is accepted, marks the run healthy and records the new PIDs, so every refusal below is down to the one missing line.
        PostChecksOutcome complete = RunPostChecks(all, []);
        Assert.True(complete.Code == 0, complete.Output);
        Assert.True(complete.Healthy);
        Assert.NotEqual(complete.MetaBefore, complete.MetaAfter);

        foreach (string missing in all)
        {
            PostChecksOutcome r = RunPostChecks([.. all.Where(l => l != missing)], []);

            Assert.True(r.Code != 0, $"healthy despite the missing line '{missing}'\n{r.Output}");
            Assert.False(r.Healthy, $"report marked healthy despite the missing line '{missing}'");
            Assert.Equal(r.MetaBefore, r.MetaAfter);
        }
    }

    [WindowsFact]
    public void PostChecks_ChangedContentCounts_StillPass()
    {
        // Same lines, different numbers: a prefix match must accept them (exact counts would fail every content update).
        RequirePython();
        string[] changed =
        [
            "Terrain: reading .map files from E:/other",
            "Collision: vmaps from E:/other/vmaps",
            "Collision: navmeshes from E:/other/mmaps",
            "elevators and trams: 3 loaded",
            "Started managed playerbot 7",
            "loaded 45 maps, 1100 areas",
            "loaded 130 graveyards and 200 graveyard-zone links",
            "Transports: 10 routes, 10 ships sailing",
            "Loaded 180 item sets",
            "Loaded 2100 random properties and 800 item_enchantment_template entries",
            "Loaded 1500 page texts",
            "Enchanting: 1500 enchantments",
            "appearance is checked against 3700 CharSections and 140 CharacterFacialHairStyles rows",
        ];
        PostChecksOutcome r = RunPostChecks(changed, []);

        Assert.True(r.Code == 0, r.Output);
        Assert.True(r.Healthy);
        Assert.True(r.ChecksAllTrue, "an expected line was reported missing although only its counts changed");
        Assert.NotEqual(r.MetaBefore, r.MetaAfter);
    }

    [WindowsFact]
    public void PostChecks_NoTransportsFlag_ExcludesOnlyTheTransportLine()
    {
        RequirePython();
        string[] withoutTransports = [.. BlockingLines, .. ElevatorLines, .. OptionalDataLines];

        PostChecksOutcome flagged = RunPostChecks(withoutTransports, ["--no-transports"]);
        Assert.True(flagged.Code == 0, flagged.Output);
        Assert.True(flagged.Healthy);

        PostChecksOutcome unflagged = RunPostChecks(withoutTransports, []);
        Assert.NotEqual(0, unflagged.Code);
        Assert.False(unflagged.Healthy);
    }

    [WindowsFact]
    public void PostChecks_NoOptionalDataFlag_ExcludesOnlyTheOptionalDataLines()
    {
        RequirePython();
        string[] withoutOptional = [.. BlockingLines, .. ElevatorLines, .. TransportLines];

        PostChecksOutcome flagged = RunPostChecks(withoutOptional, ["--no-optional-data"]);
        Assert.True(flagged.Code == 0, flagged.Output);
        Assert.True(flagged.Healthy);

        PostChecksOutcome unflagged = RunPostChecks(withoutOptional, []);
        Assert.NotEqual(0, unflagged.Code);
        Assert.False(unflagged.Healthy);
    }

    [WindowsFact]
    public void PostChecks_NoElevatorsFlag_ExcludesOnlyTheElevatorLine()
    {
        // --no-elevators leaves GameObjects:TransportAnimationDbcPath unset, so the World never logs the elevators line.
        RequirePython();
        string[] withoutElevators = [.. BlockingLines, .. TransportLines, .. OptionalDataLines];

        PostChecksOutcome flagged = RunPostChecks(withoutElevators, ["--no-elevators"]);
        Assert.True(flagged.Code == 0, flagged.Output);
        Assert.True(flagged.Healthy);

        PostChecksOutcome unflagged = RunPostChecks(withoutElevators, []);
        Assert.NotEqual(0, unflagged.Code);
        Assert.False(unflagged.Healthy);
    }

    [WindowsFact]
    public void PostChecks_NoEnabledBots_NeedsNoPlayerbotLogLine()
    {
        // The sandbox run has no bots (min(enabled, MaxBots) == 0), so the World logs no "Started managed playerbot" line and the gate must
        // still pass; the started-bot count check, not a log line, is what enforces bots when some are expected.
        RequirePython();
        string[] noBotLine = [.. BlockingLines, .. ElevatorLines, .. TransportLines, .. OptionalDataLines];

        PostChecksOutcome r = RunPostChecks(noBotLine, []);
        Assert.True(r.Code == 0, r.Output);
        Assert.True(r.Healthy);
        Assert.NotEqual(r.MetaBefore, r.MetaAfter);

        PostChecksOutcome withBotLine = RunPostChecks([.. noBotLine, .. BotLines], []);
        Assert.True(withBotLine.Code == 0, withBotLine.Output);
    }

    /// <summary>
    /// Unlike the older tests in this class, a missing interpreter fails these instead of skipping them: a skipped test reports as passing,
    /// which would let the gates they pin go unchecked on a host without Python (CI's ubuntu-latest and the Windows dev hosts have it).
    /// </summary>
    private static void RequirePython() => Assert.True(PythonProbe.Interpreter is not null, "no Python 3.10+ interpreter on PATH; these deploy-gate tests cannot run");

    /// <summary>Skips only off Windows, where Get-NetTCPConnection (the listener probe) does not exist; never for a missing interpreter.</summary>
    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Windows PowerShell gate; not on a Windows host.";
            }
        }
    }

    private sealed record PostChecksOutcome(int Code, string Output, bool Healthy, string MetaBefore, string MetaAfter, bool ChecksAllTrue);

    /// <summary>
    /// Runs deploy-post-checks.py against a sandbox run whose "new" World and Realm are two real listeners (so the listener checks pass), no
    /// bots and no characters, and a world.log holding exactly <paramref name="logLines"/>.
    /// </summary>
    private static PostChecksOutcome RunPostChecks(string[] logLines, string[] extraArguments)
    {
        using var box = new Sandbox();
        using var world = Listener.Start();
        using var realm = Listener.Start();

        string profile = box.Dir("profile");
        string db = Path.Combine(box.Dir("db"), "characters.db");
        (int dbCode, string dbOutput) = Python(["-c",
            "import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); "
            + "c.execute('CREATE TABLE characters(Id INTEGER, AccountId INTEGER, Name TEXT, Race INTEGER, Class INTEGER, Gender INTEGER)'); "
            + "c.execute('CREATE TABLE managed_playerbot(BotId INTEGER, AccountId INTEGER, CharacterId INTEGER, DesiredEnabled INTEGER)'); "
            + "c.commit(); c.close()",
            db]);
        Assert.True(dbCode == 0, dbOutput);

        box.File(Path.Combine(profile, "appsettings.json"), JsonSerializer.Serialize(new
        {
            World = new { Port = world.Port },
            Auth = new { Port = realm.Port },
            Database = new { Characters = new { ConnectionString = "Data Source=" + db } },
        }));
        string meta = box.File(Path.Combine(profile, "standard-processes.json"),
            JsonSerializer.Serialize(new { services = new { World = new { pid = 1 }, Realm = new { pid = 2 } }, qualification = "old" }));

        string deployDir = box.Dir("deploy");
        string runDir = box.Dir("deploy/t");
        string reportPath = box.File(Path.Combine(runDir, "report.json"), JsonSerializer.Serialize(new
        {
            healthy = false,
            profile,
            artifacts = box.Dir("artifacts"),
            new_world = world.Pid,
            new_realm = realm.Pid,
            bots_stopped = Array.Empty<object>(),
            characters_stopped = Array.Empty<object>(),
            population_stopped = Array.Empty<object>(),
        }));
        box.File(Path.Combine(runDir, "world.log"), string.Join('\n', logLines.Select(l => "[12:00:00 INF] " + l)) + "\n");
        string metaBefore = File.ReadAllText(meta);

        (int code, string output) = Python([PostChecks, "--label", "t", "--deploy-dir", deployDir, .. extraArguments]);

        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(reportPath));
        bool healthy = report.RootElement.TryGetProperty("healthy", out JsonElement h) && h.ValueKind == JsonValueKind.True;
        bool checksAllTrue = report.RootElement.TryGetProperty("world_log_checks", out JsonElement checks)
                             && checks.ValueKind == JsonValueKind.Object
                             && checks.EnumerateObject().All(c => c.Value.ValueKind == JsonValueKind.True);
        return new PostChecksOutcome(code, output, healthy, metaBefore, File.ReadAllText(meta), checksAllTrue);
    }

    /// <summary>A Python process that listens on a free loopback port; its PID is what Get-NetTCPConnection reports as the owner.</summary>
    private sealed class Listener : IDisposable
    {
        private readonly Process _process;

        private Listener(Process process, int port, int pid)
        {
            _process = process;
            Port = port;
            Pid = pid;
        }

        public int Port { get; }

        public int Pid { get; }

        public static Listener Start()
        {
            (string exe, string[] prefix) = PythonProbe.Interpreter!.Value;
            var start = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in prefix)
            {
                start.ArgumentList.Add(a);
            }

            start.ArgumentList.Add("-I");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("import os,socket,time; s=socket.socket(); s.bind(('127.0.0.1',0)); s.listen(); "
                                   + "print(s.getsockname()[1], os.getpid(), flush=True); time.sleep(300)");
            Process process = Process.Start(start)!;
            string[] fields = process.StandardOutput.ReadLine()!.Split(' ');
            return new Listener(process, int.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture));
        }

        public void Dispose()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            _process.Dispose();
        }
    }
}
