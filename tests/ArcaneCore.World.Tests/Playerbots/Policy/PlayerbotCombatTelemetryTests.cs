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
                Assert.True(written, $"fights {telemetry.Combat.Fights}, first alive {await world.OnWorldAsync(() => first.IsAlive)}; " + await world.OnWorldAsync(() =>
                    $"goal{world.Brain.Goal} target {world.Brain.TargetEntry} hp {world.Player.Health}/{world.Player.MaxHealth} at {world.Player.X:F1},{world.Player.Y:F1}; second alive {second.IsAlive} in world {second.IsInWorld} at {second.X:F1},{second.Y:F1} hp {second.Health} combat {second.Combat.IsInCombat}; sees {world.Player.VisibleObjects.Contains(second.Guid)}; first at {first.X:F1},{first.Y:F1}"));
                await telemetry.FlushAsync();
            }
            await telemetry.DisposeAsync();

            JsonElement[] fights = Directory.GetFiles(directory).SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line).RootElement)
                .Where(root => root.GetProperty("k").GetString() == "fight").ToArray();
            JsonElement fight = Assert.Single(fights);
            Assert.Equal(["v", "k", "t", "bot", "map", "zone", "engage", "targets", "retreat", "rest", "o"], fight.EnumerateObject().Select(p => p.Name));
            Assert.Equal(PlayerbotPolicyRecord.SchemaVersion, fight.GetProperty("v").GetInt32());

            JsonElement engage = fight.GetProperty("engage");
            Assert.Equal("engage", engage.GetProperty("decision").GetString());
            Assert.Equal(993_951u, engage.GetProperty("entry").GetUInt32());
            Assert.Equal(PlayerbotCombatTelemetry.CandidateFeatureNames.Length, engage.GetProperty("f").GetArrayLength());
            // Risk scoring is on, so the fight was judged: the retreat decision (stayed) with its features.
            JsonElement retreat = fight.GetProperty("retreat");
            Assert.Equal(0, retreat.GetProperty("retreat").GetInt32());
            Assert.Equal(PlayerbotCombatTelemetry.FightFeatureNames.Length, retreat.GetProperty("f").GetArrayLength());
            Assert.Equal(PlayerbotCombatTelemetry.RestFeatureNames.Length, fight.GetProperty("rest").GetProperty("f").GetArrayLength());
            // The first target pick: the candidate set the bot fought among and the one it chose, with its own time to kill.
            JsonElement pick = Assert.Single(fight.GetProperty("targets").EnumerateArray());
            JsonElement candidates = pick.GetProperty("c");
            Assert.InRange(candidates.GetArrayLength(), 1, PlayerbotCombatTelemetry.MaxCandidates);
            Assert.All(candidates.EnumerateArray(), c => Assert.Equal(PlayerbotCombatTelemetry.TargetFeatureNames.Length, c.GetArrayLength()));
            Assert.InRange(pick.GetProperty("pick").GetInt32(), 0, candidates.GetArrayLength() - 1);
            Assert.InRange(pick.GetProperty("ttk").GetDouble(), 0, 120);

            JsonElement outcome = fight.GetProperty("o");
            Assert.Equal(["died", "killed", "ttk", "fight", "damage", "damage_pct", "downtime"], outcome.EnumerateObject().Select(p => p.Name));
            Assert.Equal(0, outcome.GetProperty("died").GetInt32());
            Assert.Equal(1, outcome.GetProperty("killed").GetInt32());
            Assert.InRange(outcome.GetProperty("ttk").GetDouble(), 0.1, 120);
            long damage = outcome.GetProperty("damage").GetInt64();
            Assert.InRange(damage, 0, 10_000);
            if (damage > 0) Assert.True(outcome.GetProperty("damage_pct").GetDouble() > 0);
            Assert.InRange(outcome.GetProperty("downtime").GetDouble(), 0, 600);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ATurnedDownPull_IsASkip_OncePerMinute_WrittenAfterTenMinutesWithoutAFight()
    {
        string directory = Directory.CreateTempSubdirectory("policy-telemetry-").FullName;
        try
        {
            await using RiskTestWorld world = await RiskTestWorld.StartAsync();
            // Record times follow the world's manual clock, so ten world minutes are ten minutes apart in the records too.
            var telemetry = new PlayerbotPolicyTelemetry(new PlayerbotPolicyTelemetryOptions { Enabled = true }, directory,
                new WorldClock(() => world.Host.World.NowMs));
            {
                // An elite of the bot's level that hits far harder than it can take: the risk model turns it down every think.
                Creature elite = await world.OnWorldAsync(() =>
                    world.Spawn(RiskTestWorld.Template(993_953, health: 20_000, minDamage: 200, maxDamage: 300, rank: 1), 25f, 0f));
                await world.SeeAsync(elite);
                await world.OnWorldAsync(() => world.Brain.Risk.CombatSink = telemetry.Combat);

                bool written = await world.ThinkUntilAsync(PlayerbotCombatTelemetry.DowntimeCapMs + 30_000, () => telemetry.Combat.Skips >= 1,
                    each: () => telemetry.AfterThink(world.Brain, world.Player, world.Host.World.NowMs));
                Assert.True(written, $"skips {telemetry.Combat.Skips}, in combat {await world.OnWorldAsync(() => world.Player.Combat.IsInCombat)}");
                Assert.False(await world.OnWorldAsync(() => world.Player.Combat.IsInCombat || elite.Combat.IsInCombat), "the bot pulled the elite");
                await telemetry.FlushAsync();
            }
            await telemetry.DisposeAsync();

            JsonElement[] skips = Directory.GetFiles(directory).SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line).RootElement)
                .Where(root => root.GetProperty("k").GetString() == "skip").ToArray();
            // Over ten minutes of thinks the one creature is written about once a minute, never once a think, and at most the pending cap.
            // Turned down on many thinks while in sight, written once a minute at most (the bot moves on; the spacing is pinned below).
            Assert.InRange(skips.Length, 1, PlayerbotCombatTelemetry.MaxPendingSkips);
            Assert.All(skips, skip =>
            {
                Assert.Equal(993_953u, skip.GetProperty("entry").GetUInt32());
                Assert.NotEqual("engage", skip.GetProperty("decision").GetString());
                Assert.Equal(PlayerbotCombatTelemetry.CandidateFeatureNames.Length, skip.GetProperty("f").GetArrayLength());
                Assert.Equal(JsonValueKind.Null, skip.GetProperty("o").GetProperty("wait").ValueKind); // no fight followed within the cap
            });
            long[] times = [.. skips.Select(skip => skip.GetProperty("t").GetInt64())];
            Assert.All(times.Zip(times.Skip(1)), pair => Assert.InRange(pair.Second - pair.First, PlayerbotCombatTelemetry.SkipRepeatMs,
                PlayerbotCombatTelemetry.SkipRepeatMs + 1_000));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ASkip_IsRecordedOncePerMinutePerCreature_UpToThePendingCap_AndATakenDetourIsNoSkip()
    {
        string directory = Directory.CreateTempSubdirectory("policy-telemetry-").FullName;
        try
        {
            await using RiskTestWorld world = await RiskTestWorld.StartAsync();
            uint clockMs = 0;
            var telemetry = new PlayerbotPolicyTelemetry(new PlayerbotPolicyTelemetryOptions { Enabled = true }, directory,
                new WorldClock(() => clockMs));
            await world.OnWorldAsync(() =>
            {
                Creature elite = world.Spawn(RiskTestWorld.Template(993_956, rank: 1), 20f, 0f);
                Creature other = world.Spawn(RiskTestWorld.Template(993_957), -20f, 0f);
                var avoid = new PlayerbotEngagement(993_956, 0.9f, 0.2f, PlayerbotEngageDecision.Avoid, "elite", 0);
                var detour = new PlayerbotEngagement(993_957, 0.3f, 0.5f, PlayerbotEngageDecision.Detour, "path-adds-1", 1);
                // Every second for 130 s: the elite is written at 0, 60 and 120 s. A routeless detour (not taken) is a skip too.
                for (uint s = 0; s <= 130; s++)
                {
                    clockMs = s * 1000;
                    telemetry.Combat.Assessed(world.Player, elite, avoid, taken: false, clockMs);
                }
                telemetry.Combat.Assessed(world.Player, other, detour, taken: false, 1000);
                // A taken detour is the bot's pull, not a skip.
                telemetry.Combat.Assessed(world.Player, other, detour with { Entry = 1 }, taken: true, 2000);
                // Past the cap: nine distinct creatures cannot all wait.
                for (int i = 0; i < 9; i++)
                    telemetry.Combat.Assessed(world.Player, world.Spawn(RiskTestWorld.Template((uint)(993_960 + i)), 30f, i),
                        avoid with { Entry = (uint)(993_960 + i) }, taken: false, 5000);
                // No fight for ten minutes: the waiting skips are written with no wait.
                telemetry.Combat.AfterThink(world.Brain, world.Player, PlayerbotCombatTelemetry.DowntimeCapMs + 10_000);
                return true;
            });
            await telemetry.FlushAsync();
            await telemetry.DisposeAsync();

            JsonElement[] skips = Directory.GetFiles(directory).SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line).RootElement)
                .Where(root => root.GetProperty("k").GetString() == "skip").ToArray();
            Assert.Equal(PlayerbotCombatTelemetry.MaxPendingSkips, skips.Length);
            long[] eliteTimes = [.. skips.Where(skip => skip.GetProperty("reason").GetString() == "elite" && skip.GetProperty("entry").GetUInt32() == 993_956)
                .Select(skip => skip.GetProperty("t").GetInt64())];
            Assert.Equal([0L, 60_000L, 120_000L], eliteTimes);
            Assert.Single(skips, skip => skip.GetProperty("decision").GetString() == "detour");
            Assert.DoesNotContain(skips, skip => skip.GetProperty("entry").GetUInt32() == 1);
            Assert.All(skips, skip => Assert.Equal(JsonValueKind.Null, skip.GetProperty("o").GetProperty("wait").ValueKind));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task TheSink_ChangesNoDecision()
    {
        (IReadOnlyList<string> without, IReadOnlyList<string> healthWithout) = await TraceAsync(withSink: false);
        (IReadOnlyList<string> with, IReadOnlyList<string> healthWith) = await TraceAsync(withSink: true);
        // Damage rolls are random, so the two runs' fights part once a blow (the bot's or the mob's) lands differently: decisions are
        // compared up to there.
        int same = 0;
        while (same < healthWithout.Count && healthWithout[same] == healthWith[same]) same++;
        Assert.Equal(without.Take(same), with.Take(same));
        // Not vacuous: that stretch covers the turned-down elite and the pull (the first think in combat; the bot's own blows are
        // random, so the fight itself may part right after).
        Assert.True(without.Take(same).Any(line => line.StartsWith("Combat", StringComparison.Ordinal)), $"the runs parted after {same} thinks");
    }

    private static async Task<(IReadOnlyList<string> Decisions, IReadOnlyList<string> Health)> TraceAsync(bool withSink)
    {
        string directory = Directory.CreateTempSubdirectory("policy-telemetry-").FullName;
        try
        {
            await using var telemetry = new PlayerbotPolicyTelemetry(new PlayerbotPolicyTelemetryOptions { Enabled = true }, directory);
            await using RiskTestWorld world = await RiskTestWorld.StartAsync();
            Creature first = await world.OnWorldAsync(() => world.Spawn(RiskTestWorld.Template(993_954, health: 60, minDamage: 3, maxDamage: 3), 15f, 0f));
            Creature elite = await world.OnWorldAsync(() =>
                world.Spawn(RiskTestWorld.Template(993_955, health: 20_000, minDamage: 200, maxDamage: 300, rank: 1), -25f, 0f));
            await world.SeeAsync(first, elite);
            if (withSink) await world.OnWorldAsync(() => world.Brain.Risk.CombatSink = telemetry.Combat);
            var trace = new List<string>();
            var health = new List<string>();
            for (int i = 0; i < 150; i++)
            {
                await world.ThinkAsync();
                trace.Add(await world.OnWorldAsync(() =>
                {
                    if (withSink) telemetry.AfterThink(world.Brain, world.Player, world.Host.World.NowMs);
                    health.Add($"{world.Player.Health}/{first.Health}");
                    return $"{world.Brain.Goal}/{world.Brain.TargetEntry}/{world.Brain.InspectionTarget?.Entry ?? 0}/{world.Player.X:F1},{world.Player.Y:F1}";
                }));
            }
            return (trace, health);
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

    /// <summary>Wall time that follows the world's manual clock (from the Unix epoch), for record times in tests.</summary>
    private sealed class WorldClock(Func<uint> nowMs) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(nowMs());
    }
}
