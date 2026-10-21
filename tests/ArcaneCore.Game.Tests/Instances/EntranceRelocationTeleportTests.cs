using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Instances.InstanceFixture;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// A character saved inside a dungeon whose instance is gone logs in at the entrance (vmangos Player::LoadFromDB; InstanceManager
/// RelocateToEntrance). The login first places it at the saved dungeon position with a movement block (CharacterHandlers
/// HandlePlayerLogin), so the relocation must move the movement block too: every teleport start rewrites the position from it
/// (TeleportService ResetMovementForTeleport), and a stale block put the player at the dungeon-interior X/Y on the continent map.
/// </summary>
public sealed class EntranceRelocationTeleportTests
{
    private const float DungeonX = -16.4f, DungeonY = -383.07f, DungeonZ = 61.78f, DungeonO = 1.86f;
    private const float EntranceX = -11209.6f, EntranceY = 1666.54f, EntranceZ = 24.69f;

    [Fact]
    public void LoginRelocatedToTheEntrance_ThenNearTeleport_NeverStandsAtTheDungeonInterior_AndArrives()
    {
        using var f = new InstanceFixture();
        Player player = LoginInsideAResetDungeon(f, 1);
        AssertPositionAtEntrance(player);

        Assert.True(f.Teleports.TeleportTo(player, 0, -8833f, 628f, 94f, 0)); // a hearthstone, a summon or a GM tele on the same map
        Assert.Equal(TeleportStage.Near, f.Teleports.StageOf(player));
        AssertAtEntrance(player);

        f.Tick();
        AssertAtEntrance(player);
        Assert.True(f.Teleports.HandleTeleportAck(player, player.Guid.Value));
        Assert.Equal((0u, -8833f, 628f, 94f), (player.Map!.MapId, player.X, player.Y, player.Z));
        AssertMovementBlockMatches(player);
    }

    [Fact]
    public void LoginRelocatedToTheEntrance_ThenFarTeleport_NeverStandsAtTheDungeonInterior_AndArrives()
    {
        using var f = new InstanceFixture();
        Player player = LoginInsideAResetDungeon(f, 2);
        AssertPositionAtEntrance(player);

        Assert.True(f.Teleports.TeleportTo(player, 1, 1000f, 1000f, 50f, 0));
        Assert.Equal(TeleportStage.FarScheduled, f.Teleports.StageOf(player));
        AssertAtEntrance(player);

        f.Tick(); // the scheduled far teleport runs after the map update: the player leaves map 0
        Assert.Equal(TeleportStage.Far, f.Teleports.StageOf(player));
        Assert.False(player.MapId == 0 && player.X == DungeonX && player.Y == DungeonY,
            $"player stood at the dungeon interior ({player.X}, {player.Y}) on the continent map while loading");

        Assert.True(f.Teleports.HandleWorldportAck(player));
        f.Tick();
        Assert.Equal((1u, 1000f, 1000f, 50f), (player.Map!.MapId, player.X, player.Y, player.Z));
        AssertMovementBlockMatches(player);
    }

    /// <summary>The login of CharacterHandlers.HandlePlayerLogin: the saved position goes into the movement block, then the map add.</summary>
    private static Player LoginInsideAResetDungeon(InstanceFixture f, uint guid)
    {
        var session = new FakeSession((int)guid);
        Player player = TestWorld.CreatePlayer(guid, DungeonX, DungeonY, session, mapId: Dungeon);
        player.Level = 60;
        player.Home = new HomeBind(0, 1519, -8833f, 628f, 94f);
        player.Relocate(DungeonX, DungeonY, DungeonZ, DungeonO, f.World.NowMs);
        f.World.AddPlayer(player); // no bind for the dungeon: InstanceManager.ResolveLoginMap relocates to the exit trigger's target
        return player;
    }

    private static void AssertAtEntrance(Player player)
    {
        AssertPositionAtEntrance(player);
        AssertMovementBlockMatches(player);
    }

    private static void AssertPositionAtEntrance(Player player)
        => Assert.Equal((0u, EntranceX, EntranceY, EntranceZ), (player.MapId, player.X, player.Y, player.Z));

    private static void AssertMovementBlockMatches(Player player)
    {
        MovementInfo movement = player.Movement;
        Assert.Equal((player.X, player.Y, player.Z, player.Orientation), (movement.X, movement.Y, movement.Z, movement.Orientation));
    }
}
