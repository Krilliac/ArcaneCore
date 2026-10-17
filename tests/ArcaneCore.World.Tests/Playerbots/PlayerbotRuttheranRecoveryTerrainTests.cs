using System.Numerics;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Live 2026-10-16 (world.debug.log 20:33-20:40, Eliaslial; 39 faults of six bots that day, all at graveyard 129): the bot died over
/// the Veiled Sea between Auberdine and Rut'theran, its body left at (7666.7, 1040.9, 438.5) map 1, 438 yards above the water, and its
/// ghost was released at graveyard 129, Rut'theran Village (8706, 965, 13.27). No spirit healer stands at that graveyard in the 1.12
/// world database (classic-db z2815: the nearest creature 6491 is Dolanaar's, 1,609 yards away on top of Teldrassil; the Darnassus one
/// is reached through the Rut'theran portal, area trigger 542). The ghost "walked" to the graveyard it stood on (0.6 yd), the walk could
/// not close, it went back to its body (hundreds of refused plans: "region components bot -1, goal -1"), gave up, threw
/// <see cref="PlayerbotRecovery.SpiritHealerFailed"/> and was quarantined three times, then disabled. Now the corpse run gives up a body
/// no plan reaches after <see cref="PlayerbotRecovery.MaxRefusedPlans"/> refusals (or walks into a same-map teleport trigger that leads
/// to it), the fallback looks for the spirit healer by the world's spawns rather than the graveyard point, takes the portal when that
/// is the shorter way to one, and never throws. Spawns and triggers below are classic-db z2815's; terrain and navigation are the real
/// 1.12 extraction.
/// </summary>
public sealed partial class PlayerbotRealTerrainNavigationTests
{
    private const uint SpiritHealerEntry = 6491;

    // classic-db z2815 creature 6491 spawns on Teldrassil (guid, x, y, z): Dolanaar, Darnassus, Aldrassil.
    private static readonly (uint Guid, Vector3 At)[] TeldrassilHealers =
    [
        (87048, new Vector3(9683.39f, 952.225f, 1291.87f)),
        (87049, new Vector3(10055.4f, 2116.32f, 1329.71f)),
        (87050, new Vector3(10394.7f, 825.111f, 1317.52f)),
    ];

    // WorldSafeLocs.dbc (5875).
    private static readonly WorldSafeLoc Ruttheran = new(129, 1, 8706.0f, 965.0f, 13.27f, 0f, "Teldrassil, Rut'theran Village");
    private static readonly WorldSafeLoc[] TeldrassilTop =
    [
        new(90, 1, 10054.3f, 2117.12f, 1329.63f, 0f, "Teldrassil, Darnassus"),
        new(91, 1, 9701.255f, 945.6204f, 1291.3551f, 0f, "Teldrassil, Dolanaar"),
        new(93, 1, 10384.81f, 811.5311f, 1317.5382f, 0f, "Teldrassil, Aldrassil"),
    ];

    // AreaTrigger.dbc 542 (Rut'theran, the portal up) and 527 (Darnassus, the portal down); classic-db areatrigger_teleport rows.
    private static readonly AreaTriggerTemplate PortalUp = new(542, 1, 8799.41f, 969.787f, 30.2409f, 10f, 0, 0, 0, 0, "Teddrassil - Darnassus");
    private static readonly AreaTriggerTemplate PortalDown = new(527, 1, 9947.48f, 2630.04f, 1318.6f, 10f, 0, 0, 0, 0, "Teddrassil - Ruth Theran");
    private static readonly Vector3 DarnassusPortalExit = new(9946.25f, 2612.97f, 1316.49f);

    private static readonly Vector3 EliaslialBody = new(7666.7f, 1040.9f, 438.5f);
    private static readonly Vector3 EliaslialGhost = new(8706.0f, 965.0f, 12.7f);

    /// <summary>
    /// The live case: the body out of every reach, the ghost on graveyard 129 where no healer stands. The ghost gives the body up
    /// after a bounded number of refused plans, takes the Rut'theran portal (the shorter way to a spirit healer than Dolanaar's,
    /// straight up the tree), walks to the Darnassus healer and is resurrected by CMSG_SPIRIT_HEALER_ACTIVATE; nothing throws.
    /// </summary>
    [RealTerrainBotFact]
    public async Task AGhostAtRuttheranGraveyard_WithItsBodyOverTheSea_TakesThePortalToASpiritHealer()
    {
        var clock = new RevivalClock(1_000);
        await using Terrain terrain = await Terrain.StartAsync(services: TeldrassilWorld);
        await terrain.Host.OnWorldAsync(() => DeathHooks.Register(terrain.Host.World, new DeathHooks(new DeathOptions(), clock)));
        await terrain.PlaceAsync(EliaslialBody, map: 1);
        await terrain.KillAndReleaseAsync();
        await terrain.PlaceAsync(EliaslialGhost, map: 1);
        clock.Seconds += 125;

        RecoveryRun run = await RunRecoveryAsync(terrain, maxThinks: 900);

        Assert.True(run.Fault is null, $"the recovery threw ({run.Fault}): {run.Path}");
        // The mesh ends in the sea 230 yards out and reaches neither the body (438 yards up, 1,000 out) nor any way to it. That partial route
        // closes on the body flat but leaves it 438 yards out of vertical reach, so the corpse run refuses it
        // (PlayerbotRecovery.LeavesGoalOutOfVerticalReach) instead of walking it out to sea; the refusals give the body up.
        Assert.True(run.CorpseThinks <= PlayerbotRecovery.MaxRefusedPlans + 6,
            $"{run.CorpseThinks} thinks on a corpse run no plan reaches: {run.Path}");
        Assert.True(run.TookPortal, "the ghost never took the Rut'theran portal: " + run.Path);
        Assert.True(run.Alive, "the ghost was never resurrected: " + run.Path);
        await terrain.Host.OnWorldAsync(() => Assert.True(
            Vector3.Distance(new(terrain.Player.X, terrain.Player.Y, terrain.Player.Z), TeldrassilHealers[1].At) <= 10f,
            $"revived at ({terrain.Player.X:F1}, {terrain.Player.Y:F1}, {terrain.Player.Z:F1}), not at the Darnassus healer: {run.Path}"));
    }

    /// <summary>
    /// A body in Darnassus and the ghost at Rut'theran: no walk joins them (the tree is climbed only through the portal). The corpse run
    /// stops planning after <see cref="PlayerbotRecovery.MaxRefusedPlans"/> refusals and walks into area trigger 542, as a player does; on
    /// the far side the body is in reach and is reclaimed. Bounded: no hundreds of refused plans, nothing thrown.
    /// </summary>
    [RealTerrainBotFact]
    public async Task AGhostAtRuttheran_WithItsBodyInDarnassus_TakesThePortalToItsBody()
    {
        var clock = new RevivalClock(1_000);
        await using Terrain terrain = await Terrain.StartAsync(services: TeldrassilWorld);
        await terrain.Host.OnWorldAsync(() => DeathHooks.Register(terrain.Host.World, new DeathHooks(new DeathOptions(), clock)));
        await terrain.PlaceAsync(DarnassusPortalExit + new Vector3(0f, -8f, 0f), map: 1);
        await terrain.KillAndReleaseAsync();
        await terrain.PlaceAsync(EliaslialGhost, map: 1);
        clock.Seconds += 125;

        RecoveryRun run = await RunRecoveryAsync(terrain, maxThinks: 600);

        Assert.True(run.Fault is null, $"the recovery threw ({run.Fault}): {run.Path}");
        Assert.True(run.CorpseThinksBeforePortal <= PlayerbotRecovery.MaxRefusedPlans + 6,
            $"{run.CorpseThinksBeforePortal} corpse-run thinks at Rut'theran before the portal: {run.Path}");
        Assert.True(run.TookPortal, "the ghost never took the Rut'theran portal: " + run.Path);
        Assert.True(run.Alive, "the ghost was never revived: " + run.Path);
    }

    private sealed record RecoveryRun(string? Fault, bool Alive, bool TookPortal, int CorpseThinks, int CorpseThinksBeforePortal, string Path);

    private static async Task<RecoveryRun> RunRecoveryAsync(Terrain terrain, int maxThinks)
    {
        var recovery = new PlayerbotRecovery(terrain.Session, new PlayerbotOptions { Enabled = true })
        {
            GraveyardLookup = (_, at) => TeldrassilGraveyard(at),
        };
        string? fault = null;
        bool alive = false, portal = false;
        int corpseThinks = 0, corpseBeforePortal = 0;
        var trail = new List<string>();
        for (int think = 0; think < maxThinks && fault is null && !alive; think++)
        {
            (fault, alive) = await terrain.Host.OnWorldAsync<(string?, bool)>(() =>
            {
                PlayerbotMovementControl.Update(terrain.Session, terrain.Player);
                terrain.Session.ManagedBudget = new ManagedActionBudget(4);
                try { recovery.Update(terrain.Player, 500); }
                catch (InvalidOperationException e) { return (e.Message, false); }
                portal |= terrain.Player.Z > 1000f;
                if (recovery.LastStep == PlayerbotRecoveryStep.WalkToCorpse)
                {
                    corpseThinks++;
                    if (!portal) corpseBeforePortal++;
                }

                string step = $"{recovery.LastStep}.{recovery.LastSpiritHealerStep}";
                if (trail.Count == 0 || !trail[^1].StartsWith(step + " ", StringComparison.Ordinal))
                    trail.Add($"{step} ({terrain.Player.X:F0},{terrain.Player.Y:F0},{terrain.Player.Z:F0})");
                return (null, terrain.Player.IsAlive);
            });
            for (int tick = 0; tick < 10 && fault is null && !alive; tick++)
            {
                await terrain.Host.World.AdvanceClockAsync(50);
                await terrain.Host.OnWorldAsync(() =>
                {
                    PlayerbotMovementControl.Update(terrain.Session, terrain.Player);
                    PlayerbotMotion.Pump(terrain.Session, terrain.Player, terrain.Host.World.NowMs);
                });
            }
        }

        return new RecoveryRun(fault, alive, portal, corpseThinks, corpseBeforePortal, string.Join(" -> ", trail));
    }

    /// <summary>The graveyard links of classic-db for Teldrassil: 129 below the tree (area 702), else the nearest on top of it.</summary>
    private static WorldSafeLoc TeldrassilGraveyard(Vector3 at)
        => at.Z < 500f ? Ruttheran : TeldrassilTop.MinBy(g => Vector3.Distance(at, new Vector3(g.X, g.Y, g.Z)))!;

    private static void TeldrassilWorld(IServiceCollection services)
    {
        var healer = new CreatureTemplate
        {
            Entry = SpiritHealerEntry, Name = "Spirit Healer", NpcFlags = (uint)(NpcFlags.Gossip | NpcFlags.SpiritHealer), Faction = 35,
            CreatureType = 7, MinLevel = 60, MaxLevel = 60, MinLevelHealth = 4000, MaxLevelHealth = 4000,
        };
        CreatureSpawn[] spawns = [.. TeldrassilHealers.Select(h => new CreatureSpawn
        {
            Guid = h.Guid, Entry = SpiritHealerEntry, MapId = 1, X = h.At.X, Y = h.At.Y, Z = h.At.Z,
        })];
        services.AddScoped<ICreatureDataStore>(_ => new FixedCreatures(new CreatureContent([healer], spawns, [], [], [])));
        services.AddSingleton<IMapDataStore>(new TeldrassilMaps());
        services.AddSingleton(new FactionTemplateCatalog(
        [
            new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12),
            new FactionTemplateRecord(14, 14, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 1),
            new FactionTemplateRecord(35, 0, 0, OwnMask: 0, FriendlyMask: 0, HostileMask: 0),
        ]));
    }

    private sealed class FixedCreatures(CreatureContent content) : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }

    private sealed class TeldrassilMaps : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MapContent(
            [
                new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
                new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", ""),
            ],
            [],
            [PortalUp, PortalDown],
            [
                new AreaTriggerTeleport(542, "Teddrassil - Darnassus", "", 0, 1, DarnassusPortalExit.X, DarnassusPortalExit.Y, DarnassusPortalExit.Z, 4.71239f),
                new AreaTriggerTeleport(527, "Teddrassil - Ruth Theran", "", 0, 1, 8785.79f, 966.983f, 30.1999f, 3.40339f),
            ],
            []));
    }
}
