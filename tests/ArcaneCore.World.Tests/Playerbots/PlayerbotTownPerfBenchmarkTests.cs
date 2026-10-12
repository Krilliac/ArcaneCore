using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The town phase's cost per think (live-w34-r1, 2026-10-12: 618 of 773 slow bot thinks had "town" as their slowest phase, 114 path
/// queries and 48 ms per such think on average, 0.6 ms of it not path search). The repair trip (<see cref="PlayerbotTownGoals"/>) is
/// what runs there: a bot with badly worn gear and money plans toward the map's repair NPC spawns.
/// <para>
/// Each scenario drives the town controller of one bot like the brain does (BeginThink, Update, half a second of game time per think,
/// on the manual clock) for <see cref="SimulatedSeconds"/> of game time on the real terrain and navigation meshes, with the repair
/// NPCs of map 0 at their real spawns (world database snapshot), and prints one line: path queries, A* nodes and milliseconds per think
/// (mean, worst), thinks over 50 ms, bytes allocated per think. Not a correctness test: the numbers are reported, the assertions only
/// bound them from above after a change. Runs only with <c>ARCANECORE_TEST_TERRAIN_DIR</c> set (reported Skipped otherwise).
/// </para>
/// </summary>
public sealed class PlayerbotTownPerfBenchmarkTests(ITestOutputHelper output)
{
    /// <summary>Game time simulated per scenario: several repair-trip rounds and rests.</summary>
    private const int SimulatedSeconds = 600;

    private const uint ThinkMs = 500;

    /// <summary>The repair NPCs of map 0 in the world database snapshot (entry, x, y, z).</summary>
    private static readonly (uint Entry, float X, float Y, float Z)[] RepairSpawns =
    [
        (8670, -8824.28f, 665.71f, 97.47f), (8671, -4948.01f, -901.53f, 505.17f), (8672, 1542.40f, 225.16f, -56.79f),
        (8719, -8821.53f, 659.89f, 97.46f), (8720, -4963.19f, -904.62f, 505.17f), (8721, 1542.45f, 255.20f, -56.79f),
        (9858, -14446.50f, 448.41f, 4.13f), (9859, -4967.86f, -917.62f, 505.17f), (14624, -6524.04f, -1188.72f, 309.34f),
        (15659, -8814.55f, 660.32f, 96.68f), (15675, 1579.11f, 187.98f, -56.79f), (15676, 1609.71f, 187.18f, -56.79f),
        (15677, -14398.50f, 406.92f, 9.51f), (15681, -14416.80f, 524.10f, 7.67f), (15682, 1648.17f, 224.17f, -56.79f),
        (15683, 1648.73f, 255.38f, -56.79f), (15684, 1610.68f, 292.85f, -56.79f), (15686, 1580.66f, 293.06f, -56.79f),
    ];

    /// <summary>
    /// The starts of two of the bots that spent the live w34 run in the town phase (the most frequent "in town from" points of the
    /// log), and the Northshire start: the Swamp of Sorrows and the Hillsbrad foothills.
    /// </summary>
    [RealTerrainBotFact]
    public async Task RepairTrip_SwampOfSorrows_ReportsCostPerThink()
        => await Measure("swamp-of-sorrows", new Vector3(-11031.5f, -1833.6f, 80.3f));

    [RealTerrainBotFact]
    public async Task RepairTrip_Hillsbrad_ReportsCostPerThink()
        => await Measure("hillsbrad", new Vector3(307.3f, -2174.0f, 131.3f));

    /// <summary>
    /// The worst start: ground the mesh joins to nothing (the Molsen Farm wall in Westfall, bot QA #180), so that every plan toward a repair
    /// NPC is refused after its fallbacks (the steep escape, the starts beside the bot) and the trip rests and starts over.
    /// </summary>
    [RealTerrainBotFact]
    public async Task RepairTrip_MeshIsland_ReportsCostPerThink()
        => await Measure("mesh-island", new Vector3(-10283.04f, 1379.96f, 40.49f));

    [RealTerrainBotFact]
    public async Task RepairTrip_Northshire_ReportsCostPerThink()
        => await Measure("northshire", new Vector3(-8949.95f, -132.49f, 83.53f));

    /// <summary>
    /// A bot standing in a city with nothing to do (no money, so no visible vendor is of use): every think the controller looks at every
    /// visible creature and asks each service NPC whether it has anything for the bot (the scan, not the walk). 40 vendors of 30 rows,
    /// 10 class trainers and 70 plain creatures within sight.
    /// </summary>
    [Fact]
    public async Task DenseCity_NothingToDo_ReportsCostPerUpdate()
    {
        var items = new ItemTestContent();
        for (uint item = 5000; item < 5030; item++)
            items.Templates.Templates.Add(new ItemTemplate { Entry = item, Name = $"ware {item}", Class = (uint)ItemClass.Armor, SubClass = ItemSubClasses.ArmorMisc,
                InventoryType = (uint)InventoryType.Feet, Armor = 5, ItemLevel = 5, Quality = 1, BuyPrice = 100, SellPrice = 10, Stackable = 1,
                AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue });
        using IDisposable itemScope = items.Use();
        DenseCityServices.Current.Value = new DenseCityFixture();
        try
        {
            await using WorldTestHost host = WorldTestHost.Start();
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
            try
            {
                await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Count >= DenseCityFixture.Total, "the city is visible");
                var probe = new PlayerbotThinkProbe();
                const int updates = 2000;
                (double ms, long bytes, int visible) = await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    player.Money = 0;
                    for (int warm = 0; warm < 50; warm++) goals.Update(player, 1500);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long started = Stopwatch.GetTimestamp();
                    probe.Begin();
                    for (int i = 0; i < updates; i++)
                    {
                        goals.BeginThink();
                        goals.Update(player, 1500);
                    }

                    probe.End();
                    return ((Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency, GC.GetAllocatedBytesForCurrentThread() - before,
                        player.VisibleObjects.Count);
                });
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"BENCH dense-city: {visible} visible objects, {updates} updates; ms/update {ms / updates:F4}, path queries {probe.Queries}, "
                    + $"allocated {bytes / updates} B/update"));
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { DenseCityServices.Current.Value = null; }
    }

    private async Task Measure(string label, Vector3 start)
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(Boots(9212, durability: 20));
        using IDisposable itemScope = items.Use();
        RealRepairServices.Current.Value = new RealRepairFixture(RepairSpawns);
        try
        {
            string root = Environment.GetEnvironmentVariable(RealTerrainBotFactAttribute.Variable)!;
            await using WorldTestHost host = WorldTestHost.Start(configure: options => options.Maps.DataDirectory = root,
                configureServices: services =>
                {
                    services.AddSingleton(new RepairCostTable([(10u, Enumerable.Repeat(1u, RepairCostTable.MultiplierCount).ToArray())], [(4u, 1f)]));
                    services.AddSingleton<IWorldFeature, ManualClock>();
                });
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
            try
            {
                TeleportService teleports = host.WorldServices.GetRequiredService<TeleportFeature>().Teleports;
                Player player = session.Player!;
                Assert.True(await host.OnWorldAsync(() => teleports.TeleportTo(player, 0, start.X, start.Y, start.Z, 0)), "teleport refused");
                for (int tick = 0; ; tick++)
                {
                    Assert.True(tick < 200, "the bot did not arrive");
                    bool there = await host.OnWorldAsync(() =>
                    {
                        PlayerbotMovementControl.Update(session, player);
                        return player.IsInWorld && teleports.StageOf(player) is null
                            && Vector2.Distance(new(player.X, player.Y), new(start.X, start.Y)) < 1f;
                    });
                    if (there) break;
                    await host.World.AdvanceClockAsync(50);
                }

                await host.OnWorldAsync(() =>
                {
                    PlayerbotEquipmentTests.EquipFromBags(player, 9212);
                    player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Durability = 0;
                    player.Money = 100;
                    return true;
                });

                var probe = new PlayerbotThinkProbe();
                var millis = new List<double>();
                long queries = 0, nodes = 0, allocated = 0;
                int over50 = 0, moved = 0;
                for (int think = 0; think < SimulatedSeconds * 1000 / ThinkMs; think++)
                {
                    await host.World.AdvanceClockAsync(ThinkMs);
                    // Tiles the plans wait for are read off the world thread: let the reads finish between thinks.
                    await Task.Delay(1);
                    (double ms, int q, long n, long bytes, bool stepped) = await host.OnWorldAsync(() =>
                    {
                        PlayerbotMotion.ElapseForTests(player, ThinkMs);
                        session.ManagedBudget = new ManagedActionBudget(4);
                        long before = GC.GetAllocatedBytesForCurrentThread();
                        probe.Begin();
                        goals.BeginThink();
                        bool step = goals.Update(player, ThinkMs);
                        probe.End();
                        return (probe.ElapsedMs, probe.Queries, probe.Search.Nodes, GC.GetAllocatedBytesForCurrentThread() - before, step);
                    });
                    millis.Add(ms);
                    if (Environment.GetEnvironmentVariable("BENCH_TRACE") == label && (think < 80 || think % 50 == 0)) output.WriteLine($"TRACE {think} q={q} stepped={stepped} target={goals.TargetEntry} ms={ms:F2}");
                    queries += q;
                    nodes += n;
                    allocated += bytes;
                    if (ms > 50) over50++;
                    if (stepped) moved++;
                }

                millis.Sort();
                double total = millis.Sum();
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"BENCH repair-trip {label}: {millis.Count} thinks over {SimulatedSeconds} s game time; "
                    + $"path queries {queries} ({(double)queries / millis.Count:F2}/think), nodes {nodes} ({(double)nodes / millis.Count:F0}/think); "
                    + $"ms/think mean {total / millis.Count:F3} p99 {millis[(int)(millis.Count * 0.99)]:F2} max {millis[^1]:F2}, total {total:F0} ms; "
                    + $"thinks over 50 ms {over50}; thinks that stepped {moved}; allocated {allocated / millis.Count} B/think"));
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { RealRepairServices.Current.Value = null; }
    }

    private sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    private static ItemTemplate Boots(uint entry, uint durability) => new()
    {
        Entry = entry, Name = $"boots-{entry}", Class = (uint)ItemClass.Armor, SubClass = ItemSubClasses.ArmorMisc,
        InventoryType = (uint)InventoryType.Feet, Armor = 10, MaxDurability = durability, ItemLevel = 10, Quality = 1, Stackable = 1,
        AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue,
    };
}

/// <summary>Repair NPCs at the given spawns of map 0, friendly to the bot; the bot's start is wherever the test teleports it.</summary>
internal sealed class RealRepairFixture((uint Entry, float X, float Y, float Z)[] spawns) : ICreatureDataStore, INpcContentStore
{
    Task<NpcContent> INpcContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(NpcContent.Empty);

    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(new CreatureContent(
        spawns.Select(s => s.Entry).Distinct().Select(entry => new CreatureTemplate
            { Entry = entry, Name = $"armorer {entry}", Faction = 900011, NpcFlags = (uint)NpcFlags.Repair, DisplayIds = [49] }).ToList(),
        spawns.Select((s, i) => new CreatureSpawn
            { Guid = (uint)(97001 + i), Entry = s.Entry, MapId = 0, X = s.X, Y = s.Y, Z = s.Z }).ToList(),
        [], [], []));
}

internal sealed class RealRepairServices : IWorldTestServices
{
    public static readonly AsyncLocal<RealRepairFixture?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture) return;
        services.AddSingleton<ICreatureDataStore>(fixture);
        services.AddSingleton<INpcContentStore>(fixture);
        services.AddSingleton<IWorldDataStore>(new FarTrainerWorldData());
        services.AddSingleton(new FactionTemplateCatalog([new FactionTemplateRecord(1, 1, 0, 1, 0, 0), new FactionTemplateRecord(900011, 0, 0, 8, 0, 0)]));
    }
}


/// <summary>40 vendors of 30 rows, 10 class trainers and 70 plain creatures, all within a few yards of the Northshire start.</summary>
internal sealed class DenseCityFixture : ICreatureDataStore, INpcContentStore
{
    public const int Vendors = 40, Trainers = 10, Critters = 70, Total = Vendors + Trainers + Critters;

    Task<NpcContent> INpcContentStore.LoadAsync(CancellationToken cancellationToken) => Task.FromResult(NpcContent.Empty with
    {
        VendorItems = [.. Enumerable.Range(0, Vendors).SelectMany(v => Enumerable.Range(0, 30)
            .Select(slot => new VendorItem { Entry = (uint)(2000 + v), Item = (uint)(5000 + slot), Slot = (uint)slot }))],
    });

    Task<CreatureContent> ICreatureDataStore.LoadAsync(CancellationToken cancellationToken)
    {
        var templates = new List<CreatureTemplate>();
        var spawns = new List<CreatureSpawn>();
        for (int i = 0; i < Total; i++)
        {
            uint entry = (uint)(i < Vendors ? 2000 + i : i < Vendors + Trainers ? 2100 + i : 2200 + i);
            NpcFlags flags = i < Vendors ? NpcFlags.Vendor : i < Vendors + Trainers ? NpcFlags.Trainer : NpcFlags.None;
            templates.Add(new CreatureTemplate { Entry = entry, Name = $"npc {entry}", Faction = 900011, NpcFlags = (uint)flags, DisplayIds = [49],
                TrainerType = 0, TrainerClass = 1 });
            spawns.Add(new CreatureSpawn { Guid = (uint)(98001 + i), Entry = entry, MapId = 0, X = -8949.95f + (i % 12) * 3f, Y = -132.49f + (i / 12) * 3f, Z = 83.53f });
        }

        return Task.FromResult(new CreatureContent(templates, spawns, [], [], []));
    }
}

internal sealed class DenseCityServices : IWorldTestServices
{
    public static readonly AsyncLocal<DenseCityFixture?> Current = new();

    public void Register(IServiceCollection services)
    {
        if (Current.Value is not { } fixture) return;
        services.AddSingleton<ICreatureDataStore>(fixture);
        services.AddSingleton<INpcContentStore>(fixture);
        services.AddSingleton<IWorldDataStore>(new InMemoryWorldDataStore());
        services.AddSingleton(new FactionTemplateCatalog([new FactionTemplateRecord(1, 1, 0, 1, 0, 0), new FactionTemplateRecord(900011, 0, 0, 8, 0, 0)]));
    }
}
