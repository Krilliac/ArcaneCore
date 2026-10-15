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
            Assert.Equal(["v", "t", "bot", "map", "zone", "f", "open", "ladder", "pick", "src", "x", "goal", "kind", "w", "o"],
                root.EnumerateObject().Select(p => p.Name));
            Assert.Equal(1, root.GetProperty("v").GetInt32());
            Assert.Equal(42, root.GetProperty("t").GetInt64());
            Assert.Equal("ABC", root.GetProperty("bot").GetString());
            Assert.Equal(PlayerbotPolicySchema.FeatureCount, root.GetProperty("f").GetArrayLength());
            Assert.Equal(0.25, root.GetProperty("f")[0].GetDouble(), 4);
            Assert.Equal(0b100_0010, root.GetProperty("open").GetInt32());
            Assert.Equal("Town", root.GetProperty("ladder").GetString());
            Assert.Equal("Explore", root.GetProperty("pick").GetString());
            Assert.Equal("explored", root.GetProperty("src").GetString());
            Assert.Equal(1, root.GetProperty("x").GetInt32());
            Assert.Equal("Explore", root.GetProperty("goal").GetString());
            Assert.Equal("Explore", root.GetProperty("kind").GetString());
            Assert.Equal(300, root.GetProperty("w").GetInt32());
            JsonElement outcome = root.GetProperty("o");
            Assert.Equal(["lvl", "xp", "money", "items", "quests", "stalls", "deaths", "anomalies"], outcome.EnumerateObject().Select(p => p.Name));
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

    private static PlayerbotPolicyRecord Record(long unixMs)
    {
        float[] features = new float[PlayerbotPolicySchema.FeatureCount];
        features[0] = 0.25f;
        var open = default(PlayerbotPolicyGoalMask).With(PlayerbotPolicyGoal.Town).With(PlayerbotPolicyGoal.Explore);
        return new PlayerbotPolicyRecord(unixMs, 0xABC, 0, 12, features, open, PlayerbotPolicyGoal.Town, PlayerbotPolicyGoal.Explore,
            PlayerbotPolicySource.Explored, Explored: true, PlayerbotPolicyGoal.Explore, PlayerbotGoalKind.Explore, 300_000,
            new PlayerbotPolicyOutcome(0, 120, -5, 2, 0, 0, 1, 0));
    }
}
