using System.Numerics;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Playerbots;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    /// A failed tram trip gives up only this bot's goal, but that reporting choice must not suppress escape when the bot itself stands
    /// on the Molsen mesh island. Both recorded trapped positions must leave the enclosure after that failure.
    /// </summary>
    [RealTerrainBotFact]
    public async Task OnTheMolsenFarmIsland_AnUnsharedStallStillMovesBothBotsHome()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        foreach (Vector3 trapped in new[] { MolsenIslandSouth, MolsenIslandNorth })
        {
            await terrain.PlaceAsync(trapped);
            var brain = new PlayerbotBrain(terrain.Session, new PlayerbotOptions { Enabled = true })
            {
                Home = new PlayerbotHomeArea(0, SentinelHillCentre, 750f, "SentinelHill"),
            };
            await terrain.Host.OnWorldAsync(() =>
            {
                brain.HoldGoalForTests(PlayerbotGoalKind.Quest, 266, 65);
                brain.GiveUpStalledGoal(terrain.Player, shareable: false);
            });
            await terrain.WaitAsync(() => Vector2.Distance(new(terrain.Player.X, terrain.Player.Y),
                new(SentinelHillCentre.X, SentinelHillCentre.Y)) < 5f, $"escape from {trapped}");
            Assert.Equal(PlayerbotGoalKind.Home, brain.Goal);
        }
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

    // A bind point for the unhomed cases: the Sentinel Hill inn's square, 400 yards from the farm.
    private static readonly Vector3 SentinelHillBind = new(-10628.3f, 1036.7f, 34.1f);

    /// <summary>
    /// An unhomed bot with a bind point whose stall gives its goal up on the island is teleported to its bind point and explores from
    /// there (the way out of an unsafe landing too).
    /// </summary>
    [RealTerrainBotFact]
    public async Task OnTheMolsenFarmIsland_AStalledUnhomedBotGoesToItsBindPoint_AndExplores()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(MolsenIslandSouth);
        var brain = new PlayerbotBrain(terrain.Session, new PlayerbotOptions { Enabled = true });
        await terrain.Host.OnWorldAsync(() =>
        {
            terrain.Player.Home = new HomeBind(0, 40, SentinelHillBind.X, SentinelHillBind.Y, SentinelHillBind.Z);
            brain.HoldGoalForTests(PlayerbotGoalKind.Quest, 266, 65);
            brain.GiveUpStalledGoal(terrain.Player);
        });
        await terrain.WaitAsync(() => Vector2.Distance(new(terrain.Player.X, terrain.Player.Y), new(SentinelHillBind.X, SentinelHillBind.Y)) < 5f,
            "the teleport off the island to the bind point");
        Assert.Equal(PlayerbotGoalKind.Explore, brain.Goal);
    }

    /// <summary>
    /// A grouped bot can run this brain (PlayerbotPartyAI.Drives is false while it never engaged and no real member is online): its stall
    /// on the island must not pull it away from its group. The bind-point teleport is refused as the return teleport home is.
    /// </summary>
    [RealTerrainBotFact]
    public async Task OnTheMolsenFarmIsland_AGroupedUnhomedBotIsNotTeleported()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(MolsenIslandSouth);
        var brain = new PlayerbotBrain(terrain.Session, new PlayerbotOptions { Enabled = true });
        await terrain.Host.OnWorldAsync(() =>
        {
            terrain.Player.Home = new HomeBind(0, 40, SentinelHillBind.X, SentinelHillBind.Y, SentinelHillBind.Z);
            PlayerbotTestGroups.JoinOfflinePartner(terrain.Session, terrain.Player);
            brain.HoldGoalForTests(PlayerbotGoalKind.Quest, 266, 65);
            brain.GiveUpStalledGoal(terrain.Player);
        });
        for (int tick = 0; tick < 40; tick++)
        {
            await terrain.Host.OnWorldAsync(() => PlayerbotMovementControl.Update(terrain.Session, terrain.Player));
            await terrain.Host.World.AdvanceClockAsync(50);
        }
        float moved = await terrain.Host.OnWorldAsync(() => Vector2.Distance(new(terrain.Player.X, terrain.Player.Y), new(MolsenIslandSouth.X, MolsenIslandSouth.Y)));
        Assert.True(moved < 1f, $"the grouped bot was moved {moved:F1} yards");
    }

    /// <summary>
    /// A homed bot whose home teleport is held back by its cooldown (it teleported home moments ago) still leaves the island, by its bind
    /// point, as from an unsafe landing; the refused home teleport does not set its walk home aside.
    /// </summary>
    [RealTerrainBotFact]
    public async Task OnTheMolsenFarmIsland_AHomedBotWithinItsHomeTeleportCooldown_GoesToItsBindPoint()
    {
        await using Terrain terrain = await Terrain.StartAsync();
        await terrain.PlaceAsync(MolsenIslandSouth);
        var brain = new PlayerbotBrain(terrain.Session, new PlayerbotOptions { Enabled = true })
        {
            Home = new PlayerbotHomeArea(0, SentinelHillCentre, 750f, "SentinelHill"),
        };
        await terrain.Host.OnWorldAsync(() =>
        {
            terrain.Player.Home = new HomeBind(0, 40, SentinelHillBind.X, SentinelHillBind.Y, SentinelHillBind.Z);
            brain.HoldGoalForTests(PlayerbotGoalKind.Quest, 266, 65);
            brain.GiveUpStalledGoal(terrain.Player);
        });
        await terrain.WaitAsync(() => Vector2.Distance(new(terrain.Player.X, terrain.Player.Y), new(SentinelHillCentre.X, SentinelHillCentre.Y)) < 5f,
            "the first teleport home");

        await terrain.PlaceAsync(MolsenIslandSouth);
        await terrain.Host.OnWorldAsync(() =>
        {
            brain.HoldGoalForTests(PlayerbotGoalKind.Quest, 266, 65);
            brain.GiveUpStalledGoal(terrain.Player);
        });
        await terrain.WaitAsync(() => Vector2.Distance(new(terrain.Player.X, terrain.Player.Y), new(SentinelHillBind.X, SentinelHillBind.Y)) < 5f,
            "the teleport to the bind point within the home teleport's cooldown");
        Assert.DoesNotContain("blocked", brain.HomeReport ?? "");
    }
}

/// <summary>Groups for bot tests.</summary>
internal static class PlayerbotTestGroups
{
    /// <summary>Put <paramref name="player"/> in a party with an offline partner (a stored group, as at a restart; GroupManager.Restore). World thread.</summary>
    internal static void JoinOfflinePartner(WorldSession session, Player player)
    {
        const int partner = 900_001;
        session.Services.GetRequiredService<ArcaneCore.World.Characters.CharacterDirectory>().Add(
            new CharacterIdentity(partner, 1, "Partnerone", 1, 0, 1, 14));
        var groups = session.Services.GetRequiredService<ArcaneCore.World.Social.SocialFeature>().Context.Groups;
        var restored = groups.Restore([new ArcaneCore.Kernel.Social.GroupRecord(900, partner, 0, partner, 2, false, [],
            [new ArcaneCore.Kernel.Social.GroupMemberRecord(partner, 0, false), new ArcaneCore.Kernel.Social.GroupMemberRecord((int)player.Guid.Low, 0, false)])]);
        Assert.Single(restored.Restored);
        Assert.NotNull(groups.GetGroup(player.Guid));
    }
}
