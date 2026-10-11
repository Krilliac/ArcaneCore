using System.Numerics;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Bot QA issue #180 and its live-w23 sequel (live-w23-r1, main 85023136): inside the round wall of the Molsen Farm in Westfall
/// (x -10305..-10274, y 1355..1385) the navigation mesh is an island, joined to nothing round it, and the collision data closes it too
/// (the wall stands 1.7 to 3 yards above the ground inside; no floor within a step and in sight leads over it). Wfbotac and Wfbotaj,
/// once inside, stalled there whatever goal they took (quest 65 to Wiley the Black, quest 2039 to Bingles Blastenheimer, the Harvest
/// Watchers beside the farm) for hours and across restarts: every route ended at the island's edge nearest the goal and the next one was
/// refused. Wfbotac's saved position is on the island. Now a stall that gives a goal up there moves the bot off (its home, or its bind
/// point), as an unsafe landing does.
/// </summary>
public sealed partial class PlayerbotRealTerrainNavigationTests
{
    // Wfbotac's saved position (live-w23-r1 characters; it logged in there and stalled within two minutes) and Wfbotaj's stall spot.
    private static readonly Vector3 MolsenIslandSouth = new(-10281.6f, 1356.533f, 40.484f);
    private static readonly Vector3 MolsenIslandNorth = new(-10283.04f, 1379.96f, 40.49f);

    // Outside the wall: north of the farm (the explore stalls of live-w22-r4) and east of it (issue #180's first evidence).
    private static readonly Vector3 MolsenNorth = new(-10282.1f, 1417.1f, 38.6f);
    private static readonly Vector3 MolsenEast = new(-10233.5f, 1362.6f, 40.3f);

    // The Sentinel Hill home of the live bots (PlayerbotHome teleport target in the live logs).
    private static readonly Vector3 SentinelHillCentre = new(-10624.5f, 1096.7f, 33.8f);

    /// <summary>
    /// The island test answers yes on the Molsen Farm island and no beside it, in the Goldshire inn, in the Deathknell crypt and in
    /// Coldridge Valley (rooms and open ground the mesh joins to the world).
    /// </summary>
    [RealTerrainBotFact]
    public async Task IsOnMeshIsland_IsTheMolsenFarmIsland_AndNotRoomsOrOpenGround()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        (string Place, Vector3 At, uint Map, bool Island)[] places =
        [
            ("Molsen Farm island, south (Wfbotac)", MolsenIslandSouth, 0, true),
            ("Molsen Farm island, north (Wfbotaj)", MolsenIslandNorth, 0, true),
            ("north of the farm", MolsenNorth, 0, false),
            ("east of the farm", MolsenEast, 0, false),
            ("Goldshire inn", InnBesidePestle, 0, false),
            ("Coldridge Valley", ColdridgeAfterRevive, 0, false),
            ("Deathknell crypt", CryptStart, 0, false),
        ];
        var options = new PlayerbotOptions();
        foreach ((string place, Vector3 at, uint map, bool island) in places)
        {
            await terrain.PlaceAsync(at, map);
            bool answer = await terrain.Host.OnWorldAsync(() => PlayerbotNavigation.IsOnMeshIsland(terrain.Player, options));
            Assert.True(answer == island, $"{place}: IsOnMeshIsland answered {answer}");
        }
    }

    /// <summary>
    /// A homed bot whose stall gives its goal up on the island (Wfbotac's: quest 65 to Wiley the Black) is teleported to its home's
    /// centre. Before, it gave the goal up and stayed: the next goal stalled there the same, for hours.
    /// </summary>
    [RealTerrainBotFact]
    public async Task OnTheMolsenFarmIsland_AStalledHomedBotIsMovedHome()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(MolsenIslandSouth);
        var brain = new PlayerbotBrain(terrain.Session, new PlayerbotOptions { Enabled = true })
        {
            Home = new PlayerbotHomeArea(0, SentinelHillCentre, 750f, "SentinelHill"),
        };
        await terrain.Host.OnWorldAsync(() =>
        {
            brain.HoldGoalForTests(PlayerbotGoalKind.Quest, 266, 65);
            brain.GiveUpStalledGoal(terrain.Player);
        });
        await terrain.WaitAsync(() => Vector2.Distance(new(terrain.Player.X, terrain.Player.Y), new(SentinelHillCentre.X, SentinelHillCentre.Y)) < 5f,
            "the teleport off the island to the home's centre");
        Assert.Equal(PlayerbotGoalKind.Home, brain.Goal);
    }

    /// <summary>The same stall beside the farm, on ground the mesh joins to the world, moves nobody: the goal alone is given up.</summary>
    [RealTerrainBotFact]
    public async Task BesideTheMolsenFarm_AStalledHomedBotStaysWhereItIs()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(MolsenNorth);
        var brain = new PlayerbotBrain(terrain.Session, new PlayerbotOptions { Enabled = true })
        {
            Home = new PlayerbotHomeArea(0, SentinelHillCentre, 750f, "SentinelHill"),
        };
        await terrain.Host.OnWorldAsync(() =>
        {
            brain.HoldGoalForTests(PlayerbotGoalKind.Quest, 266, 65);
            brain.GiveUpStalledGoal(terrain.Player);
        });
        for (int tick = 0; tick < 40; tick++) await terrain.Host.World.AdvanceClockAsync(50);
        float moved = await terrain.Host.OnWorldAsync(() => Vector2.Distance(new(terrain.Player.X, terrain.Player.Y), new(MolsenNorth.X, MolsenNorth.Y)));
        Assert.True(moved < 1f, $"moved {moved:F1} yards");
        Assert.NotEqual(PlayerbotGoalKind.Home, brain.Goal);
    }
}
