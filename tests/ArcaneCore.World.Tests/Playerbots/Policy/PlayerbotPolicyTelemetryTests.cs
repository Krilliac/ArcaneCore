using System.Text.Json;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Policy;
using ArcaneCore.World.Tests.Playerbots.Risk;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Policy;

public sealed class PlayerbotPolicyTelemetryTests
{
    [Fact]
    public void Telemetry_IsOffByDefault()
    {
        var options = new PlayerbotOptions();
        Assert.False(options.Policy.Telemetry.Enabled);
        // The engine on, its telemetry left at the default: none is made.
        var engine = new PlayerbotPolicyEngine(new PlayerbotPolicyOptions { Enabled = true });
        Assert.Null(engine.Telemetry);
    }

    [Fact]
    public async Task TheFiles_StayWithinTheirSizeBound()
    {
        string directory = Directory.CreateTempSubdirectory("policy-telemetry-").FullName;
        try
        {
            const long maxFileBytes = 4096;
            const int maxFiles = 3;
            var options = new PlayerbotPolicyTelemetryOptions { Enabled = true, MaxFileBytes = maxFileBytes, MaxFiles = maxFiles };
            await using (var telemetry = new PlayerbotPolicyTelemetry(options, directory))
            {
                for (int i = 0; i < 500; i++) telemetry.Enqueue(Record(i));
                await telemetry.FlushAsync();
                Assert.Equal(500, telemetry.Counts.Written);
            }

            string[] files = Directory.GetFiles(directory);
            Assert.InRange(files.Length, 1, maxFiles);
            Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, maxFileBytes));
            // 500 records are far more than three files hold: the oldest went, the newest is kept, whole lines only.
            string newest = files.OrderBy(f => f, StringComparer.Ordinal).Last();
            string[] lines = File.ReadAllLines(newest);
            Assert.Equal(499, JsonDocument.Parse(lines[^1]).RootElement.GetProperty("t").GetInt64());
            Assert.All(files.SelectMany(File.ReadAllLines), line => JsonDocument.Parse(line).Dispose());

            // A restart continues the numbering and keeps the bound.
            await using (var again = new PlayerbotPolicyTelemetry(options, directory))
            {
                for (int i = 500; i < 600; i++) again.Enqueue(Record(i));
                await again.FlushAsync();
            }
            Assert.InRange(Directory.GetFiles(directory).Length, 1, maxFiles);
            Assert.Contains(Directory.GetFiles(directory).SelectMany(File.ReadAllLines),
                line => JsonDocument.Parse(line).RootElement.GetProperty("t").GetInt64() == 599);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [WindowsFileLockFact]
    public void AnOldFileHeldOpen_StopsNewFiles_RatherThanBreakTheBound()
    {
        string directory = Directory.CreateTempSubdirectory("policy-telemetry-").FullName;
        try
        {
            byte[] line = new byte[200];
            line.AsSpan().Fill((byte)'x');
            line[^1] = (byte)'\n';
            using var writer = new PlayerbotPolicyTelemetryWriter(directory, maxFileBytes: 1024, maxFiles: 2);
            for (int i = 0; i < 10; i++) Assert.True(writer.Write(line)); // five lines a file: two files
            Assert.Equal(2, writer.Files().Count());

            string oldest = writer.Files().OrderBy(f => f, StringComparer.Ordinal).First();
            using (new FileStream(oldest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) // no delete sharing: the oldest cannot go
            {
                Assert.False(writer.Write(line));
                Assert.Equal(1, writer.Refused);
                Assert.Equal(2, writer.Files().Count());
                Assert.All(writer.Files(), file => Assert.InRange(new FileInfo(file).Length, 1, 1024));
            }
            // Released: the oldest goes and writing resumes within the bound.
            Assert.True(writer.Write(line));
            Assert.Equal(2, writer.Files().Count());
            Assert.DoesNotContain(oldest, writer.Files());

            writer.Dispose();
            Assert.False(writer.Write(line)); // a late line after disposal opens no file
            Assert.Equal(2, writer.Files().Count());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void LevelProgress_CountsTheFraction_AcrossALevelUp_AndNoneAtTheCap()
    {
        var before = new PlayerbotPolicyProgress(10, 7_500, 10_000, 0, 0, 0); // three quarters of level 10
        var after = new PlayerbotPolicyProgress(11, 2_750, 11_000, 0, 0, 0); // a quarter of level 11
        Assert.Equal(10.75, before.Levels, 6);
        Assert.Equal(0.5, after.Levels - before.Levels, 6); // the rest of 10 and a quarter of 11
        Assert.Equal(60.0, new PlayerbotPolicyProgress(60, 0, 0, 0, 0, 0).Levels, 6); // capped: no next level, no fraction
        Assert.Equal(12.0, new PlayerbotPolicyProgress(11, 12_000, 11_000, 0, 0, 0).Levels, 6); // over the next level's: at most a whole one
    }

    [Fact]
    public async Task ARecord_HasTheDocumentedShape()
    {
        string directory = Directory.CreateTempSubdirectory("policy-telemetry-").FullName;
        try
        {
            await using (var telemetry = new PlayerbotPolicyTelemetry(new PlayerbotPolicyTelemetryOptions { Enabled = true }, directory))
            {
                telemetry.Enqueue(Record(42));
                await telemetry.FlushAsync();
            }

            string line = Assert.Single(Directory.GetFiles(directory).SelectMany(File.ReadAllLines));
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            Assert.Equal(["v", "k", "t", "bot", "map", "zone", "f", "open", "ladder", "pick", "src", "x", "arm", "goal", "kind", "w", "o"],
                root.EnumerateObject().Select(p => p.Name));
            Assert.Equal(3, root.GetProperty("v").GetInt32());
            Assert.Equal("goal", root.GetProperty("k").GetString());
            Assert.Equal(42, root.GetProperty("t").GetInt64());
            Assert.Equal("ABC", root.GetProperty("bot").GetString());
            Assert.Equal(PlayerbotPolicySchema.FeatureCount, root.GetProperty("f").GetArrayLength());
            Assert.Equal(0.25, root.GetProperty("f")[0].GetDouble(), 4);
            Assert.Equal(0b1000_0010, root.GetProperty("open").GetInt32());
            Assert.Equal("Town", root.GetProperty("ladder").GetString());
            Assert.Equal("Explore", root.GetProperty("pick").GetString());
            Assert.Equal("explored", root.GetProperty("src").GetString());
            Assert.Equal(1, root.GetProperty("x").GetInt32());
            Assert.Equal("policy", root.GetProperty("arm").GetString());
            Assert.Equal("Explore", root.GetProperty("goal").GetString());
            Assert.Equal("Explore", root.GetProperty("kind").GetString());
            Assert.Equal(300, root.GetProperty("w").GetInt32());
            JsonElement outcome = root.GetProperty("o");
            Assert.Equal(["lvl", "lp", "xp", "money", "items", "quests", "stalls", "deaths", "anomalies"], outcome.EnumerateObject().Select(p => p.Name));
            Assert.Equal(0.125, outcome.GetProperty("lp").GetDouble(), 4);
            Assert.Equal(120, outcome.GetProperty("xp").GetInt64());
            Assert.Equal(-5, outcome.GetProperty("money").GetInt64());
            Assert.Equal(1, outcome.GetProperty("deaths").GetInt32());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ABrainDecision_IsWrittenWithItsOutcome_AfterTheWindow()
    {
        string directory = Directory.CreateTempSubdirectory("policy-telemetry-").FullName;
        try
        {
            var options = new PlayerbotPolicyOptions { Enabled = true, Telemetry = { Enabled = true, OutcomeMinutes = 1 } };
            var engine = new PlayerbotPolicyEngine(options);
            engine.Telemetry = new PlayerbotPolicyTelemetry(options.Telemetry, directory);
            await using (RiskTestWorld world = await RiskTestWorld.StartAsync(o => o.Risk.Enabled = false))
            {
                world.Brain.PolicyEngine = engine;
                Creature mob = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(993_902, health: 400), 50f, 0f));
                await world.SeeAsync(mob);
                // A minute and a few seconds of world time, a think every 100 ms, each followed by the feature's end-of-think steps.
                for (int i = 0; i < 650; i++)
                {
                    await world.ThinkAsync();
                    await world.OnWorldAsync(() =>
                    {
                        engine.Telemetry!.AfterThink(world.Brain, world.Player, world.Host.World.NowMs);
                        engine.Dispatch();
                        return true;
                    });
                    await engine.WhenIdleAsync();
                }
                await engine.Telemetry!.FlushAsync();
                Assert.True(engine.Telemetry.Counts.Written > 0, $"queued {engine.Telemetry.Counts.Queued}");
            }
            await engine.DisposeAsync();

            string first = Directory.GetFiles(directory).SelectMany(File.ReadAllLines).First();
            using JsonDocument document = JsonDocument.Parse(first);
            JsonElement root = document.RootElement;
            Assert.Equal(60, root.GetProperty("w").GetInt32());
            Assert.Equal("agreed", root.GetProperty("src").GetString());
            Assert.Equal(0, root.GetProperty("x").GetInt32());
            Assert.Equal(PlayerbotPolicySchema.FeatureCount, root.GetProperty("f").GetArrayLength());
            Assert.True(Enum.TryParse<PlayerbotGoalKind>(root.GetProperty("kind").GetString(), out _));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task HealthRecords_AreWrittenAtTheInterval_AndCountTheOverrunsOfTheirWindow()
    {
        string directory = Directory.CreateTempSubdirectory("policy-telemetry-").FullName;
        try
        {
            var time = new ManualTime(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
            long overruns = 5; // already counted before telemetry looked: not this interval's
            await using (var telemetry = new PlayerbotPolicyTelemetry(new PlayerbotPolicyTelemetryOptions { Enabled = true }, directory, time))
            {
                telemetry.TickHealth = () => new PlayerbotTickHealth(overruns, 76_000);
                telemetry.Sweep(1_000); // the baseline only
                overruns += 3;
                time.Advance(TimeSpan.FromSeconds(10));
                telemetry.Sweep(5_000); // the interval is not over in world time
                telemetry.Sweep(11_000);
                time.Advance(TimeSpan.FromSeconds(10));
                telemetry.Sweep(21_000); // a quiet interval
                await telemetry.FlushAsync();
                Assert.Equal(2, telemetry.Counts.Written);
            }

            JsonElement[] records = [.. Directory.GetFiles(directory).SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line).RootElement)];
            Assert.Equal(2, records.Length);
            Assert.Equal(["v", "k", "t", "ms", "over", "budget"], records[0].EnumerateObject().Select(p => p.Name));
            Assert.Equal("health", records[0].GetProperty("k").GetString());
            Assert.Equal(3, records[0].GetProperty("v").GetInt32());
            Assert.Equal(time.GetUtcNow().ToUnixTimeMilliseconds() - 10_000, records[0].GetProperty("t").GetInt64());
            Assert.Equal(10_000, records[0].GetProperty("ms").GetInt64());
            Assert.Equal(3, records[0].GetProperty("over").GetInt64());
            Assert.Equal(76, records[0].GetProperty("budget").GetInt64());
            Assert.Equal(0, records[1].GetProperty("over").GetInt64());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Health_CostsNothing_WithoutATickSource_OrBeforeTheMonitorRuns()
    {
        string directory = Directory.CreateTempSubdirectory("policy-telemetry-").FullName;
        try
        {
            await using (var telemetry = new PlayerbotPolicyTelemetry(new PlayerbotPolicyTelemetryOptions { Enabled = true }, directory))
            {
                for (uint now = 0; now < 100_000; now += 1_000) telemetry.Sweep(now);
                int calls = 0;
                telemetry.TickHealth = () => { calls++; return null; }; // the monitor is not running yet
                for (uint now = 100_000; now < 200_000; now += 1_000) telemetry.Sweep(now);
                await telemetry.FlushAsync();
                Assert.Equal(0, telemetry.Counts.Queued);
                Assert.InRange(calls, 1, 10); // asked once per interval, never per tick
            }
            Assert.Empty(Directory.GetFiles(directory).SelectMany(File.ReadAllLines));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void WithTelemetryOff_TheEngineHasNoTelemetry_SoNoHealthIsSampled()
    {
        var engine = new PlayerbotPolicyEngine(new PlayerbotPolicyOptions { Enabled = true });
        Assert.Null(engine.Telemetry);
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static PlayerbotPolicyRecord Record(long unixMs)
    {
        float[] features = new float[PlayerbotPolicySchema.FeatureCount];
        features[0] = 0.25f;
        var open = default(PlayerbotPolicyGoalMask).With(PlayerbotPolicyGoal.Town).With(PlayerbotPolicyGoal.Explore);
        return new PlayerbotPolicyRecord(unixMs, 0xABC, 0, 12, features, open, PlayerbotPolicyGoal.Town, PlayerbotPolicyGoal.Explore,
            PlayerbotPolicySource.Explored, Explored: true, PlayerbotPolicyGoal.Explore, PlayerbotGoalKind.Explore, 300_000,
            new PlayerbotPolicyOutcome(0, 0.125, 120, -5, 2, 0, 0, 1, 0));
    }
}

/// <summary>
/// Runs only on Windows: there an open file without delete sharing cannot be deleted, which is the state the test needs. On Linux and
/// macOS the delete succeeds whatever is held open, so the writer never refuses and the test's premise does not exist.
/// </summary>
internal sealed class WindowsFileLockFactAttribute : FactAttribute
{
    public WindowsFileLockFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "needs Windows file locking: elsewhere an open file can still be deleted";
        }
    }
}
