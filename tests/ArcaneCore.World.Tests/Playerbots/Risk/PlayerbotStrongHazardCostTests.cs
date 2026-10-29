using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Risk;

/// <summary>
/// The cost of one hazard scan (<see cref="PlayerbotRisk.ScanHazards"/>) with 64 visible creatures beyond the bot's strength: about a
/// thousand bots scan every 2 seconds on the one world thread. Prints microseconds and bytes per scan (run with a detailed console
/// logger to see them); asserts only generous bounds, so a regression by an order of magnitude fails.
/// </summary>
public sealed class PlayerbotStrongHazardCostTests(ITestOutputHelper output)
{
    private const int Creatures = 64;
    private const int Scans = 2_000;

    /// <param name="botLevel">2: all 64 are beyond the bot's strength; 20: none is (the scan's cost without strong hazards).</param>
    [Trait("Category", "Timing")]
    [Theory]
    [InlineData((byte)2)]
    [InlineData((byte)20)]
    public async Task AScanOf64StrongCreatures_StaysCheap(byte botLevel)
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        CreatureTemplate cutpurse = RiskTestWorld.Template(991294, level: 5, health: 102, minDamage: 7, maxDamage: 10);
        Creature[] spawned = await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            player.Level = botLevel;
            player.MaxHealth = botLevel == 2 ? 70u : 800u;
            player.Health = player.MaxHealth;
            var list = new Creature[Creatures];
            for (int i = 0; i < Creatures; i++)
            {
                float angle = i * MathF.Tau / Creatures;
                float distance = 50f + (i % 4) * 10f;
                list[i] = world.Spawn(cutpurse, MathF.Cos(angle) * distance, MathF.Sin(angle) * distance);
            }

            return list;
        });
        await world.SeeAsync(spawned);
        FieldInfo next = typeof(PlayerbotRisk).GetField("_nextScanMs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        (double micros, double median, double bytes, int hazards) = await world.OnWorldAsync(() =>
        {
            PlayerbotRisk risk = world.Brain.Risk;
            Player player = world.Player;
            for (int i = 0; i < 200; i++) { next.SetValue(risk, 0u); risk.ScanHazards(player); } // warm-up
            // The cost is the fastest of ten windows: a preemption on a loaded runner lands in some windows, not in all.
            const int Windows = 10, PerWindow = Scans / Windows;
            var perScan = new double[Windows];
            long before = GC.GetAllocatedBytesForCurrentThread();
            var watch = new Stopwatch();
            for (int window = 0; window < Windows; window++)
            {
                watch.Restart();
                for (int i = 0; i < PerWindow; i++)
                {
                    next.SetValue(risk, 0u);
                    risk.ScanHazards(player);
                }

                watch.Stop();
                perScan[window] = watch.Elapsed.TotalMicroseconds / PerWindow;
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Array.Sort(perScan);
            return (perScan[0], (perScan[Windows / 2 - 1] + perScan[Windows / 2]) / 2, (double)allocated / Scans,
                risk.Hazards.Active(player.MapId, world.Host.World.NowMs).Count);
        });
        output.WriteLine($"strong-hazard scan (bot level {botLevel}): {micros:F1} us best window, {median:F1} us median window, {bytes:F0} bytes per scan, {hazards} hazards, {Creatures} creatures");
        Console.WriteLine($"strong-hazard scan (bot level {botLevel}): {micros:F1} us best window, {median:F1} us median window, {bytes:F0} bytes per scan, {hazards} hazards, {Creatures} creatures");
        Assert.Equal(botLevel == 2 ? Creatures : 0, hazards);
        Assert.True(micros < 2_000, $"{micros:F1} us per scan");
    }

    [Trait("Category", "Timing")]
    [Fact]
    public async Task OneHundredOrdinaryHostiles_ComputeEachMeleeExposureOncePerScan()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        CreatureTemplate wolf = RiskTestWorld.Template(991295, level: 5, health: 102, minDamage: 7, maxDamage: 10);
        Creature[] ring = await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            player.Level = 20;
            player.MaxHealth = player.Health = 800;
            return Enumerable.Range(0, 100).Select(i =>
            {
                float angle = i * MathF.Tau / 100;
                return world.Spawn(wolf, MathF.Cos(angle) * 50f, MathF.Sin(angle) * 50f);
            }).ToArray();
        });
        await world.SeeAsync(ring);
        FieldInfo next = typeof(PlayerbotRisk).GetField("_nextScanMs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        (int evaluations, double micros, double median) = await world.OnWorldAsync(() =>
        {
            PlayerbotRisk risk = world.Brain.Risk;
            Player player = world.Player;
            for (int i = 0; i < 50; i++) { next.SetValue(risk, 0u); risk.ScanHazards(player); }
            int evaluated = risk.PackExposureEvaluations;
            // The cost is the fastest of ten windows: a preemption on a loaded runner lands in some windows, not in all.
            const int Windows = 10, PerWindow = 30;
            var perScan = new double[Windows];
            var clock = new Stopwatch();
            for (int window = 0; window < Windows; window++)
            {
                clock.Restart();
                for (int i = 0; i < PerWindow; i++) { next.SetValue(risk, 0u); risk.ScanHazards(player); }
                clock.Stop();
                perScan[window] = clock.Elapsed.TotalMicroseconds / PerWindow;
            }

            Array.Sort(perScan);
            return (evaluated, perScan[0], (perScan[Windows / 2 - 1] + perScan[Windows / 2]) / 2);
        });
        output.WriteLine($"100-hostile scan: {evaluations} exposure calculations, {micros:F1} us best window, {median:F1} us median window");
        Console.WriteLine($"100-hostile scan: {evaluations} exposure calculations, {micros:F1} us best window, {median:F1} us median window");
        Assert.Equal(100, evaluations);
        Assert.True(micros < 5_000, $"{micros:F1} us per scan");
    }

    /// <summary>
    /// The escape fan (<see cref="PlayerbotHazards.TryFindGap"/>, one line, and <see cref="PlayerbotHazards.FindGaps"/>, the best
    /// <see cref="PlayerbotRisk.MaxEscapePlans"/> lines as one escape attempt asks) at its worst: 64 strong hazards closing a ring round the bot (no line
    /// open, so every line is scored) and 10 other hazards: no allocation, well under 2 ms a call. Allocation is read by
    /// <see cref="AllocationProbe"/>: under the full suite one bare window read 6240 bytes while the fan allocated nothing (the
    /// one-quantum jump that probe records alongside a collection run from another test's thread), so a window a collection ran in is retried.
    /// </summary>
    [Fact]
    public void TheEscapeFan_Over64StrongAnd10OtherHazards_AllocatesNothing()
    {
        var hazards = new PlayerbotHazards();
        var from = new Vector3(1000f, 1000f, 50f);
        hazards.BeginStrongScan();
        for (int i = 0; i < 64; i++)
        {
            float angle = i * MathF.Tau / 64f;
            hazards.SetStrong(0, from + new Vector3(MathF.Cos(angle) * 44f, MathF.Sin(angle) * 44f, 0), 12f, 1_000, 6_000,
                new ArcaneCore.Game.ObjectGuid(0xF130000000000000UL + (ulong)i));
        }

        hazards.EndStrongScan();
        for (int i = 0; i < 10; i++)
        {
            float angle = (i + 0.5f) * MathF.Tau / 10f;
            hazards.Add(0, from + new Vector3(MathF.Cos(angle) * 70f, MathF.Sin(angle) * 70f, 0), PlayerbotHazards.RetreatYards, 1_000, 300, "retreat");
        }

        for (int i = 0; i < 20; i++) hazards.TryFindGap(0, from, 2_000, 0, out _, out _); // warm-up
        const int Calls = 200, Windows = 10;
        var watch = new Stopwatch();
        int found = 0;
        // The cost is the least of ten windows of Calls / Windows calls: a preemption on a loaded machine lands in some windows, not in all
        // (one wall-clock average over every call was reported at 4-7 ms on loaded lanes against about 0.2 ms alone; a preemption cause is inferred, not shown).
        double micros = double.PositiveInfinity;
        for (int window = 0; window < Windows; window++)
        {
            watch.Restart();
            for (int i = 0; i < Calls / Windows; i++)
                if (hazards.TryFindGap(0, from, 2_000, 0, out _, out _)) found++;
            watch.Stop();
            micros = Math.Min(micros, watch.Elapsed.TotalMicroseconds / (Calls / Windows));
        }

        output.WriteLine($"escape fan: {micros:F1} us per call, gap found {found}/{Calls}");
        Console.WriteLine($"escape fan: {micros:F1} us per call, gap found {found}/{Calls}");
        Assert.True(micros < 2_000, $"{micros:F1} us per call");
        Action fan = () => { for (int i = 0; i < Calls; i++) hazards.TryFindGap(0, from, 2_000, 0, out _, out _); };
        AllocationProbe.AssertEveryCleanWindowAtMost(fan, maxBytes: 0, $"{Calls} escape fans");

        // One escape attempt as PlayerbotRisk.Refused makes it: one fan for its best MaxEscapePlans lines.
        // Warmed up as the single fan is (tiered compilation may still be at work); the time is the least of three repeats.
        var best = new PlayerbotGap[PlayerbotRisk.MaxEscapePlans];
        int lines = 0;
        for (int i = 0; i < 20; i++) lines = hazards.FindGaps(0, from, 2_000, 0, best);
        double attempt = double.PositiveInfinity;
        for (int repeat = 0; repeat < 3; repeat++)
        {
            watch.Restart();
            for (int i = 0; i < Calls; i++) lines = hazards.FindGaps(0, from, 2_000, 0, best);
            watch.Stop();
            attempt = Math.Min(attempt, watch.Elapsed.TotalMicroseconds / Calls);
        }

        output.WriteLine($"escape attempt (one fan, best {lines}): {attempt:F1} us");
        Console.WriteLine($"escape attempt (one fan, best {lines}): {attempt:F1} us");
        Assert.Equal(PlayerbotRisk.MaxEscapePlans, lines);
        Action attempts = () => { for (int i = 0; i < Calls; i++) hazards.FindGaps(0, from, 2_000, 0, best); };
        AllocationProbe.AssertEveryCleanWindowAtMost(attempts, maxBytes: 0, $"{Calls} escape attempts");
    }
}
