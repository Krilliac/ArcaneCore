using System.Diagnostics;
using System.Reflection;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
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
        (double micros, double bytes, int hazards) = await world.OnWorldAsync(() =>
        {
            PlayerbotRisk risk = world.Brain.Risk;
            Player player = world.Player;
            for (int i = 0; i < 200; i++) { next.SetValue(risk, 0u); risk.ScanHazards(player); } // warm-up
            long before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < Scans; i++)
            {
                next.SetValue(risk, 0u);
                risk.ScanHazards(player);
            }

            watch.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            return (watch.Elapsed.TotalMicroseconds / Scans, (double)allocated / Scans,
                risk.Hazards.Active(player.MapId, world.Host.World.NowMs).Count);
        });
        output.WriteLine($"strong-hazard scan (bot level {botLevel}): {micros:F1} us, {bytes:F0} bytes per scan, {hazards} hazards, {Creatures} creatures");
        Console.WriteLine($"strong-hazard scan (bot level {botLevel}): {micros:F1} us, {bytes:F0} bytes per scan, {hazards} hazards, {Creatures} creatures");
        Assert.Equal(botLevel == 2 ? Creatures : 0, hazards);
        Assert.True(micros < 2_000, $"{micros:F1} us per scan");
    }
}
