using System.Text.Json;
using ArcaneCore.Game.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Policy;
using ArcaneCore.World.Tests.Playerbots.Risk;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Policy;

/// <summary>The combat-decision records: a real fight's engage, retreat and rest decisions and its outcome, as the rules made them.</summary>
public sealed class PlayerbotCombatTelemetryTests
{
    [Fact]
    public async Task AFight_IsWrittenWithItsDecisionsAndOutcome_WhenTheNextFightStarts()
    {
        string directory = Directory.CreateTempSubdirectory("policy-telemetry-").FullName;
        try
        {
            var telemetry = new PlayerbotPolicyTelemetry(new PlayerbotPolicyTelemetryOptions { Enabled = true }, directory);
            await using (RiskTestWorld world = await RiskTestWorld.StartAsync())
            {
                Creature first = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(993_951, health: 60), 15f, 0f));
                Creature second = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(993_952, health: 60), -25f, 0f));
                await world.SeeAsync(first, second);
                // As the feature does when it makes a brain (ManagedPlayerbotFeature.NewBrain).
                await world.OnWorldAsync(() => world.Brain.Risk.CombatSink = telemetry.Combat);

                // The first fight's record is finished when the second fight starts (its downtime ends there).
                bool written = await world.ThinkUntilAsync(180_000, () => telemetry.Combat.Fights >= 1,
                    each: () => telemetry.AfterThink(world.Brain, world.Player, world.Host.World.NowMs));
                Assert.True(written, $"fights {telemetry.Combat.Fights}, first alive {await world.OnWorldAsync(() => first.IsAlive)}");
                await telemetry.FlushAsync();
            }
            await telemetry.DisposeAsync();

            JsonElement[] fights = Directory.GetFiles(directory).SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line).RootElement)
                .Where(root => root.GetProperty("k").GetString() == "fight").ToArray();
            JsonElement fight = Assert.Single(fights);
            Assert.Equal(["v", "k", "t", "bot", "map", "zone", "engage", "targets", "retreat", "rest", "o"], fight.EnumerateObject().Select(p => p.Name));

            JsonElement engage = fight.GetProperty("engage");
            Assert.Equal("engage", engage.GetProperty("decision").GetString());
            Assert.Equal(993_951u, engage.GetProperty("entry").GetUInt32());
            Assert.Equal(PlayerbotCombatTelemetry.CandidateFeatureNames.Length, engage.GetProperty("f").GetArrayLength());
            // Risk scoring is on, so the fight was judged: the retreat decision (stayed) with its features.
            JsonElement retreat = fight.GetProperty("retreat");
            Assert.Equal(0, retreat.GetProperty("retreat").GetInt32());
            Assert.Equal(PlayerbotCombatTelemetry.FightFeatureNames.Length, retreat.GetProperty("f").GetArrayLength());
            Assert.Equal(2, fight.GetProperty("rest").GetProperty("f").GetArrayLength());

            JsonElement outcome = fight.GetProperty("o");
            Assert.Equal(["died", "killed", "ttk", "fight", "damage", "damage_pct", "downtime"], outcome.EnumerateObject().Select(p => p.Name));
            Assert.Equal(0, outcome.GetProperty("died").GetInt32());
            Assert.Equal(1, outcome.GetProperty("killed").GetInt32());
            Assert.InRange(outcome.GetProperty("ttk").GetDouble(), 0.1, 120);
            Assert.True(outcome.GetProperty("damage").GetInt64() >= 0);
            Assert.InRange(outcome.GetProperty("downtime").GetDouble(), 0, 600);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ARecordOfEachKind_StartsWithItsKind()
    {
        string directory = Directory.CreateTempSubdirectory("policy-telemetry-").FullName;
        try
        {
            await using (var telemetry = new PlayerbotPolicyTelemetry(new PlayerbotPolicyTelemetryOptions { Enabled = true }, directory))
            {
                var verdict = new PlayerbotEngagement(7, 0.9f, 0.2f, PlayerbotEngageDecision.Avoid, "too-strong", 2);
                var choice = new PlayerbotCombatTelemetry.Choice(0, 1234, 99, new float[PlayerbotCombatTelemetry.CandidateFeatureNames.Length], verdict);
                telemetry.Enqueue(new PlayerbotSkipRecord(choice, 0xABC, 0, 12, WaitMs: 4500));
                telemetry.Enqueue(new PlayerbotSkipRecord(choice, 0xABC, 0, 12, WaitMs: null));
                await telemetry.FlushAsync();
            }

            JsonElement[] lines = Directory.GetFiles(directory).SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line).RootElement).ToArray();
            Assert.Equal(2, lines.Length);
            JsonElement skip = lines[0];
            Assert.Equal(["v", "k", "t", "bot", "map", "zone", "f", "decision", "reason", "entry", "o"], skip.EnumerateObject().Select(p => p.Name));
            Assert.Equal("skip", skip.GetProperty("k").GetString());
            Assert.Equal("avoid", skip.GetProperty("decision").GetString());
            Assert.Equal("too-strong", skip.GetProperty("reason").GetString());
            Assert.Equal(4.5, skip.GetProperty("o").GetProperty("wait").GetDouble(), 3);
            Assert.Equal(JsonValueKind.Null, lines[1].GetProperty("o").GetProperty("wait").ValueKind);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
