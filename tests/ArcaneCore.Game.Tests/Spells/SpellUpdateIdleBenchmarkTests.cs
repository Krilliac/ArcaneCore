using System.Diagnostics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using Xunit.Abstractions;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Benchmark of the idle spell update: 30,000 in-world players that each hold three permanent passive auras (like a bot's
/// talents and racials), with no casts, cooldowns or timed holders. It calls <c>SpellSystem.Update(diffMs: 100)</c> directly
/// (not <c>World.RunTick</c>), so the measured number is the spell update alone. The median must stay at or below 1 ms.
/// </summary>
public sealed class SpellUpdateIdleBenchmarkTests(ITestOutputHelper output)
{
    private const int UnitCount = 30_000;
    private const int WarmupCalls = 20;
    private const int MeasuredCalls = 200;
    private const double MaxMedianMs = 1.0;
    private const uint PassiveA = 971001;
    private const uint PassiveB = 971002;
    private const uint PassiveC = 971003;

    private static SpellInfo Passive(uint id) => Spell(id, Effect(SpellEffectName.ApplyAura, 3, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
    {
        Attributes = SpellAttributes.Passive,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    [Fact]
    [Trait("Category", "Timing")]
    public void IdleSpellUpdate_With30000UnitsHoldingPermanentPassives_MedianIsUnderOneMillisecond()
    {
        using var kit = new SpellTestKit(Passive(PassiveA), Passive(PassiveB), Passive(PassiveC));
        kit.System.SpellBatchWindowMs = 0;
        // The kit audits every dormant state each tick (a stale-set detector for the other tests); that scan would dominate this measurement.
        // 64 is the production default, so the benchmark measures what a live run pays.
        kit.System.ActiveSetAuditBudget = 64;

        // Spread the players on a grid wider than the visibility range so adding them does not build O(n^2) visibility sets.
        const int columns = 174;
        const float spacing = 190f;
        var players = new List<Player>(UnitCount);
        for (uint i = 1; i <= UnitCount; i++)
        {
            float x = -16_000f + ((int)(i - 1) % columns) * spacing;
            float y = -16_000f + ((int)(i - 1) / columns) * spacing;
            var session = new FakeSession((int)i);
            Player player = TestWorld.CreatePlayer(i, x, y, session);
            kit.World.AddPlayer(player);
            players.Add(player);
        }

        kit.World.RunTick(0);
        foreach (Player player in players)
        {
            Assert.True(player.IsInWorld);
            foreach (uint spell in new[] { PassiveA, PassiveB, PassiveC })
            {
                Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, spell, SpellCastTargets.ForSelf(), triggered: true));
            }
        }

        // Let any queued casts apply, then settle until nothing is left to do.
        kit.Advance(200);
        Assert.Equal(UnitCount, kit.System.TrackedUnitCount);

        for (int i = 0; i < WarmupCalls; i++)
        {
            kit.Now += 100;
            kit.System.Update(diffMs: 100);
        }

        var samples = new double[MeasuredCalls];
        for (int i = 0; i < MeasuredCalls; i++)
        {
            kit.Now += 100;
            long start = Stopwatch.GetTimestamp();
            kit.System.Update(diffMs: 100);
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        double mean = samples.Average();
        double[] sorted = [.. samples.Order()];
        double median = (sorted[MeasuredCalls / 2 - 1] + sorted[MeasuredCalls / 2]) / 2;
        double p95 = sorted[(int)Math.Ceiling(MeasuredCalls * 0.95) - 1];
        double max = sorted[^1];
        string summary = $"SpellSystem.Update(100) over {UnitCount} units x3 passives, {MeasuredCalls} calls: mean={mean:F3} ms median={median:F3} ms p95={p95:F3} ms max={max:F3} ms";
        output.WriteLine(summary);

        Assert.True(median <= MaxMedianMs, $"median must be <= {MaxMedianMs} ms. {summary}");
    }
}
