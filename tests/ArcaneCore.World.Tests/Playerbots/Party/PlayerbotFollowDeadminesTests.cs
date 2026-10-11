using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Party;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// The Deadmines report (live 2026-10-10, build a88f6135, docs/areas/playbots.md "Following and catch-up"): the master dropped into
/// the Moonbrook mine just short of the instance portal and his bots, following, ran round through the mine's Defias and died or
/// looped. The World log has him landing at (-11138.04, 1506.90, 24.41) on map 0 (<c>.gm on</c> at 18:38:36). The ground he can
/// have dropped from is the mine's ledge 11.9 yards above, 9 to 16 yards across (the navigation mesh's polygons at z 36.3); the
/// surface of Moonbrook lies another 10 yards up (z 46.9) with no hole in the terrain within 35 yards. Measured on the real
/// mmaps: the follow planner's way from the ledge down is 89.5 yards for 16.4 yards of straight distance, and from the surface
/// 320.6 yards for 22.6; no off-mesh connection (no drop link) exists in the nine tiles round the mine, and the ledge way passes
/// within 20 yards of 11 Defias spawns (Diggers, a Conjurer and Henchmen, levels 15-16, the Conjurer and Henchmen elite).
/// The spawns placed here are the world database's (creature_spawn guids in the comments).
/// </summary>
public sealed class PlayerbotFollowDeadminesTests(ITestOutputHelper output)
{
    /// <summary>Where the master landed (World log, .gm on at 18:38:36).</summary>
    internal static readonly Vector3 MineBottom = new(-11138.042f, 1506.8999f, 24.40697f);

    /// <summary>The mine's ledge above the landing (navigation mesh polygon centre).</summary>
    internal static readonly Vector3 MineLedge = new(-11128.9f, 1500.3f, 36.3f);

    /// <summary>Moonbrook's surface straight above the landing (navigation mesh polygon centre).</summary>
    internal static readonly Vector3 MoonbrookSurface = new(-11136.5f, 1508f, 46.9f);

    internal static readonly CreatureTemplate Henchman = Defias(594, "Defias Henchman", 1, 1068, 22, 31);
    internal static readonly CreatureTemplate Conjurer = Defias(619, "Defias Conjurer", 1, 921, 16, 23);
    internal static readonly CreatureTemplate Digger = Defias(824, "Defias Digger", 0, 356, 23, 30);

    /// <summary>The spawns within 40 yards of the landing and the ledge (creature_spawn, map 0).</summary>
    internal static readonly (CreatureTemplate Template, Vector3 At)[] MineSpawns =
    [
        (Digger, new(-11144.5f, 1512.24f, 22.915f)), // 90051
        (Digger, new(-11141.6f, 1498.99f, 24.392f)), // 45558
        (Digger, new(-11132.0f, 1514.27f, 24.562f)), // 90131
        (Digger, new(-11120.7f, 1513.22f, 23.552f)), // 89885
        (Digger, new(-11131.7f, 1489.31f, 34.649f)), // 89886
        (Conjurer, new(-11134.2f, 1487.29f, 34.401f)), // 89542
        (Digger, new(-11147.4f, 1530.6f, 20.024f)), // 90048
        (Digger, new(-11114.5f, 1487.35f, 24.503f)), // 45561
        (Digger, new(-11126.9f, 1476.99f, 34.633f)), // 90045
        (Henchman, new(-11138.2f, 1539.52f, 19.869f)), // 89546
        (Henchman, new(-11115.3f, 1478.69f, 32.734f)), // 89541
    ];

    private static CreatureTemplate Defias(uint entry, string name, uint rank, uint health, float min, float max) => new()
    {
        Entry = entry, Name = name, MinLevel = 16, MaxLevel = 16, Faction = FollowTerrain.HostileFaction, CreatureType = 7, Rank = rank,
        UnitClass = 1, MinLevelHealth = health, MaxLevelHealth = health, Armor = 713, MinMeleeDamage = min, MaxMeleeDamage = max,
        MeleeBaseAttackTime = 2000,
    };

    /// <summary>The live bots were boosted to level 14 (<c>.character boost</c>, World log 18:31).</summary>
    internal static void AsLevel14(Player bot)
    {
        bot.Level = 14;
        bot.MaxHealth = 450;
        bot.Health = 450;
    }

    /// <summary>
    /// The mine as the follow planner sees it (the measurement behind the fix): from the ledge the way down is more than three times
    /// the straight distance and passes the elite Conjurer; from the surface it is the whole mine; the 100-yard rule never fires
    /// (16.4 and 22.6 yards), and the nine navigation tiles round the mine hold no off-mesh connection (no drop link).
    /// </summary>
    [RealTerrainBotFact]
    public async Task TheMine_AsTheFollowPlannerSeesIt()
    {
        await using FollowTerrain world = await FollowTerrain.StartAsync();
        await world.PlaceMasterAsync(MineBottom);
        var measured = new List<(float Straight, float Route)>();
        foreach (Vector3 start in (Vector3[])[MineLedge, MoonbrookSurface])
        {
            await world.PlaceBotAsync(start);
            measured.Add(await world.Host.OnWorldAsync(() =>
            {
                Assert.True(PlayerbotNavigation.TryPlan(world.Bot, MineBottom, world.Options, out PlayerbotRoute? route), "no route");
                return (Vector3.Distance(FollowTerrain.Position(world.Bot), MineBottom), route!.Distance);
            }));
            Assert.Equal(PlayerbotFollowAction.Move, PlayerbotParty.DecideFollow(new PlayerbotFollowFacts(PlayerbotPartyMode.Follow, true, true,
                measured[^1].Straight, TeleportToLeader: true, MasterMapAllowed: true)));
        }

        output.WriteLine($"ledge: straight {measured[0].Straight:F1} yd, route {measured[0].Route:F1} yd; surface: straight {measured[1].Straight:F1} yd, route {measured[1].Route:F1} yd");
        Assert.InRange(measured[0].Straight, 16f, 17f);
        Assert.InRange(measured[0].Route, 80f, 100f);
        Assert.InRange(measured[1].Straight, 22f, 23.5f);
        Assert.InRange(measured[1].Route, 300f, 340f);
        Assert.True(PlayerbotFollowRules.IsDetour(measured[0].Straight, measured[0].Route, new PlayerbotFollowOptions()));
        Assert.True(PlayerbotFollowRules.IsDetour(measured[1].Straight, measured[1].Route, new PlayerbotFollowOptions()));

        string mmaps = Path.Combine(Environment.GetEnvironmentVariable(RealTerrainBotFactAttribute.Variable)!, "mmaps");
        string[] tiles = [.. from x in (int[])[51, 52, 53] from y in (int[])[27, 28, 29] select Path.Combine(mmaps, $"000{x:00}{y:00}.mmtile")];
        int offMesh = tiles.Sum(file => Game.Maps.Collision.MMaps.NavMeshTile.ParseFile(File.ReadAllBytes(file)).Polys.Count(p => p.IsOffMeshConnection));
        Assert.Equal(0, offMesh);
    }

    /// <summary>
    /// The report itself: the master drops from the ledge to the mine floor 16 yards away. The follower must reach him without
    /// running the mine: by the drop (11.9 yards, a fall that does no damage) or a teleport, never by the 89.5-yard way round
    /// past the Defias.
    /// </summary>
    [RealTerrainBotFact]
    public async Task AFollowerOnTheMineLedge_ReachesItsMasterBelow_WithoutRunningTheMine()
    {
        await using FollowTerrain world = await FollowTerrain.StartAsync();
        await world.PlaceBotAsync(MineLedge);
        await world.PlaceMasterAsync(MineBottom);
        Creature[] hostiles = await world.Host.OnWorldAsync(() =>
        {
            AsLevel14(world.Bot);
            return MineSpawns.Select(s => world.SpawnHostile(s.Template, s.At)).ToArray();
        });

        FollowRun run = await world.RunAsync(30_000, hostiles);
        output.WriteLine(run.ToString());

        Assert.True(run.ArrivedMs >= 0, $"the follower never stood beside its master: {run}");
        Assert.True(run.Walked < 40f, $"the follower ran the mine: {run}");
        Assert.False(run.Died, $"the follower died: {run}");
        // The drop (11.9 yards: no damage, vmangos HandleFall starts at 14.57) is preferred to the teleport.
        Assert.Equal(1, run.DropsTaken);
        Assert.Equal(0, run.Teleports);
    }

    /// <summary>
    /// Fighting through, the follower on the ledge also takes the drop: it is a walk off the edge, not a teleport. Down on the mine's
    /// floor it engages the Diggers whose aggro reaches its way to the master (they stand 8.5 to 9.4 yards from him).
    /// </summary>
    [RealTerrainBotFact]
    public async Task FightThrough_AFollowerOnTheMineLedge_DropsToItsMaster_AndNeverTeleports()
    {
        await using FollowTerrain world = await FollowTerrain.StartAsync(o => o.Follow.CatchUpMode = PlayerbotCatchUpMode.FightThrough);
        await world.PlaceBotAsync(MineLedge);
        await world.PlaceMasterAsync(MineBottom);
        Creature[] hostiles = await world.Host.OnWorldAsync(() =>
        {
            AsLevel14(world.Bot);
            return MineSpawns.Select(s => world.SpawnHostile(s.Template, s.At)).ToArray();
        });

        FollowRun run = await world.RunAsync(30_000, hostiles);
        output.WriteLine(run.ToString());

        // Down on the master's floor beside him; fighting through, it may then be fighting the Diggers there.
        Assert.True(MathF.Abs(run.HeightToMaster) < 3f && run.MasterDistance < 15f, $"the follower is not down beside its master: {run}");
        Assert.True(run.Walked < 40f, $"the follower ran the mine: {run}");
        Assert.Equal(1, run.DropsTaken);
        Assert.Equal(0, run.Teleports);
        Assert.Empty(run.Jumps);
    }

    /// <summary>
    /// Fighting through from the surface, where no drop leads down: the follower never teleports; it walks the mine and engages the
    /// Defias whose aggro reaches its way instead of running past them.
    /// </summary>
    [RealTerrainBotFact]
    public async Task FightThrough_AFollowerOnTheSurface_RunsTheMineAndEngages_WithoutTeleporting()
    {
        await using FollowTerrain world = await FollowTerrain.StartAsync(o => o.Follow.CatchUpMode = PlayerbotCatchUpMode.FightThrough);
        await world.PlaceBotAsync(MoonbrookSurface);
        await world.PlaceMasterAsync(MineBottom);
        Creature[] hostiles = await world.Host.OnWorldAsync(() =>
        {
            AsLevel14(world.Bot);
            return MineSpawns.Select(s => world.SpawnHostile(s.Template, s.At)).ToArray();
        });

        FollowRun run = await world.RunAsync(90_000, hostiles, stop: r => r.Engaged.Count > 0 || r.Died);
        output.WriteLine(run.ToString());

        Assert.Equal(0, run.Teleports);
        Assert.Empty(run.Jumps);
        Assert.True(run.Walked > 100f, $"the follower did not run the mine: {run}");
        Assert.NotEmpty(run.Engaged);
    }

    /// <summary>
    /// Without a hostile on the way the surface case is still no way: 320.6 yards for 22.6 straight is a detour (more than three
    /// times, and more than 40 yards longer), and the follower teleports for that reason.
    /// </summary>
    [RealTerrainBotFact]
    public async Task AFollowerOnTheSurface_TeleportsForTheDetourAlone()
    {
        await using FollowTerrain world = await FollowTerrain.StartAsync();
        await world.PlaceBotAsync(MoonbrookSurface);
        await world.PlaceMasterAsync(MineBottom);

        FollowRun run = await world.RunAsync(5_000);
        output.WriteLine(run + "; judged " + await world.Host.OnWorldAsync(() => world.AI.Follower.LastJudgement.ToString()));

        Assert.Equal(1, run.Teleports);
        Assert.Equal(PlayerbotCatchUpReason.Detour, run.CatchUp);
    }

    /// <summary>Auto teleports only when the mesh has no way: the 320.6-yard mine is a way, so the follower walks it.</summary>
    [RealTerrainBotFact]
    public async Task Auto_AFollowerOnTheSurface_WalksTheMineBecauseTheMeshHasAWay()
    {
        await using FollowTerrain world = await FollowTerrain.StartAsync(o => o.Follow.CatchUpMode = PlayerbotCatchUpMode.Auto);
        await world.PlaceBotAsync(MoonbrookSurface);
        await world.PlaceMasterAsync(MineBottom);

        FollowRun run = await world.RunAsync(10_000);
        output.WriteLine(run.ToString());

        Assert.Equal(0, run.Teleports);
        Assert.True(run.Walked > 40f, $"the follower did not walk: {run}");
    }

    /// <summary>
    /// The master below the surface of Moonbrook, 22.5 yards down with the terrain between: no drop leads there, and the only way
    /// is the 320.6-yard mine. With the default catch-up the follower teleports to him instead of running it.
    /// </summary>
    [RealTerrainBotFact]
    public async Task AFollowerOnTheSurface_WithNoDropToItsMaster_TeleportsInsteadOfRunningTheMine()
    {
        await using FollowTerrain world = await FollowTerrain.StartAsync();
        await world.PlaceBotAsync(MoonbrookSurface);
        await world.PlaceMasterAsync(MineBottom);
        Creature[] hostiles = await world.Host.OnWorldAsync(() =>
        {
            AsLevel14(world.Bot);
            return MineSpawns.Select(s => world.SpawnHostile(s.Template, s.At)).ToArray();
        });

        FollowRun run = await world.RunAsync(20_000, hostiles);
        output.WriteLine(run.ToString());

        Assert.True(run.ArrivedMs >= 0, $"the follower never stood beside its master: {run}");
        Assert.True(run.Walked < 40f, $"the follower ran the mine: {run}");
        Assert.Equal(1, run.Teleports);
        Assert.Equal(0, run.DropsTaken);
    }
}
