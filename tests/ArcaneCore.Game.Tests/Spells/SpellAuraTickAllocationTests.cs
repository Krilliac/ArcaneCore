using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The allocation per tick of the player, spell and aura paths: 120 players spread over a zone, each holding a passive aura,
/// a periodic heal and a periodic damage aura (so health moves and the update builder sends value updates), ticked at 50 ms
/// through the world and the spell system. Deterministic (manual clock, seeded random); bytes are measured with
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>. Wall time is output only.
/// </summary>
[Collection("World tick load")]
public sealed class SpellAuraTickAllocationTests(ITestOutputHelper output)
{
    private const int Players = 120;
    private const uint PeriodicHeal = 9001;
    private const uint PeriodicDamage = 9002;

    /// <summary>
    /// The steady-state budget (bytes per 50 ms tick). Wave 18 measured 198,382 bytes/tick (193.7 KiB) before its cuts and 41,094 (40.1 KiB)
    /// after; most of what is left is the packets themselves and the test session's copy of each payload.
    /// </summary>
    public const long BudgetBytesPerTick = 56 * 1024;

    private static SpellInfo Permanent(uint id, AuraType type, int value, uint amplitude) => SpellTestKit.Spell(id,
        SpellTestKit.Effect(SpellEffectName.ApplyAura, value, aura: type, amplitude: amplitude)) with
    {
        Duration = new SpellDuration(-1, 0, -1),
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    [Fact]
    public void AuraHoldingPlayers_SteadyTick_ReportsAllocationPerTick()
    {
        using var kit = new SpellTestKit(
            Permanent(PeriodicHeal, AuraType.PeriodicHeal, 5, 1000),
            Permanent(PeriodicDamage, AuraType.PeriodicDamage, 4, 2000));
        var random = new Random(5875);
        var players = new List<Player>();
        for (int i = 0; i < Players; i++)
        {
            (Player player, _) = kit.AddPlayer((uint)i + 1, (random.NextSingle() - .5f) * 400, (random.NextSingle() - .5f) * 400);
            players.Add(player);
            Assert.True(kit.System.AddAura(player, SpellTestKit.Passive, permanent: true));
            Assert.True(kit.System.AddAura(player, PeriodicHeal, permanent: true));
            Assert.True(kit.System.AddAura(player, PeriodicDamage, permanent: true));
        }

        void Step()
        {
            kit.Now += 50;
            kit.System.Update(50);
            kit.World.RunTick(50);
        }

        const int warmup = 200, samples = 800;
        for (int tick = 0; tick < warmup; tick++)
        {
            Step();
        }

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int tick = 0; tick < samples; tick++)
        {
            Step();
        }

        long perTick = (GC.GetAllocatedBytesForCurrentThread() - before) / samples;
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds / samples;
        Assert.All(players, p => Assert.Equal(3, kit.System.GetAuras(p).Count));
        Assert.All(players, p => Assert.True(p.IsAlive));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{Players} players x 3 auras; allocated {perTick} bytes/tick ({perTick / 1024.0:F1} KiB), {ms:F3} ms/tick over {samples} ticks"));
        Assert.True(perTick <= BudgetBytesPerTick, $"{perTick} bytes/tick, budget {BudgetBytesPerTick}");
    }
}
