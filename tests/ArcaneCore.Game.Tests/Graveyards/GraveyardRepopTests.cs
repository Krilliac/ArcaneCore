using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Tests.Death;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Graveyards;

/// <summary>
/// A released spirit goes to its graveyard (vmangos Player::RepopAtGraveyard, Player.cpp:4988-5025), scheduled until its
/// water-walk order is answered (Player.cpp:1329-1334), with real (default) combat hooks, the real teleport service and no
/// timers.
/// </summary>
public sealed class GraveyardRepopTests
{
    private const long T = 1_700_000_000;

    // Map 0's linked zone is area 12, so every position of map 0 is in zone/area 12 (no terrain data in tests).
    private static readonly MapContent Content = new(
        [
            new MapTemplate(0, 0, MapType.Common, 12, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
            new MapTemplate(1, 0, MapType.Common, 14, 0, 0, -1, 0, 0, "Kalimdor", ""),
            new MapTemplate(30, 0, MapType.Battleground, 0, 40, 0, -1, 0, 0, "Alterac Valley", ""),
        ],
        [
            new AreaTemplate(12, 0, 0, 0, 0, 0, "Elwynn", 0, 0),
            new AreaTemplate(14, 1, 0, 0, 0, 0, "Durotar", 0, 0),
        ],
        [], [], []);

    private sealed class Rig : IDisposable
    {
        public Rig(GraveyardContent graveyards, bool fallbackToDefaults = false)
        {
            World = TestWorld.CreateRuntime();
            DeathHooks.Register(World, new DeathHooks(new DeathOptions { GraveyardFallbackToDefaults = fallbackToDefaults }, new FixedDeathClock(T)));
            WorldMaps.Of(World).Load(Content);
            Teleports = new TeleportService(World, _ => { }, _ => { });
            World.PlayerLoggingOut += Teleports.Forget;
            WorldGraveyards.Of(World).Load(graveyards, null);
            Service = new GraveyardRepopService(World, () => Teleports);
            Assert.True(DeathSeams.Of(World).TryRegisterGraveyards(Service));
            Session = new FakeSession(5);
            Player = CombatTestKit.AddPlayer(World, 5, 100, 100, Session);
            World.RunTick(1);
            Session.Clear();
        }

        public WorldRuntime World { get; }

        public TeleportService Teleports { get; }

        public GraveyardRepopService Service { get; }

        public FakeSession Session { get; }

        public Player Player { get; }

        public MapCombat Combat => World.GetMap(0).Combat;

        /// <summary>Die and release the spirit; the trip waits for the water-walk ack.</summary>
        public void DieAndRelease()
        {
            Player.Health = 0;
            Combat.KillPlayer(Player);
            Assert.True(Combat.RepopPlayer(Player));
        }

        public void AnswerMovementOrders()
        {
            CombatTestKit.AckPendingMovement(Player);
            World.RunTick(100);
        }

        public void Dispose() => World.Dispose();
    }

    private static WorldSafeLoc Loc(uint id, uint map, float x, float y, float z, float o = 0f) => new(id, map, x, y, z, o, "loc" + id);

    private static GraveyardContent Elwynn(params WorldSafeLoc[] locs)
        => new(locs, [.. locs.Select(l => new GraveyardLink(l.Id, l.MapId == 0 ? 12u : 14u, 0))]);

    [Fact]
    public void AReleasedSpirit_IsTeleportedToItsGraveyard_AfterItsWaterWalkOrderIsAnswered_FacingTheSafeLocsFacing()
    {
        using var rig = new Rig(Elwynn(Loc(1, 0, 500, 600, 90, 2.5f)));
        rig.DieAndRelease();

        rig.World.RunTick(100);
        Assert.False(rig.Teleports.IsBeingTeleported(rig.Player)); // deferred: the order is still pending

        rig.AnswerMovementOrders();
        Assert.True(rig.Teleports.IsBeingTeleportedNear(rig.Player));
        Assert.Equal(TeleportStage.Near, rig.Teleports.StageOf(rig.Player));
        Assert.Equal(new TeleportDestination(0, 500, 600, 90, 2.5f), rig.Teleports.DestinationOf(rig.Player));
        Assert.Contains(rig.Session.Sent, p => p.Opcode == WorldOpcode.MsgMoveTeleportAck);

        Assert.True(rig.Teleports.HandleTeleportAck(rig.Player, rig.Player.Guid.Value));
        Assert.Equal((500f, 600f, 90f, 2.5f), (rig.Player.X, rig.Player.Y, rig.Player.Z, rig.Player.Orientation));
        Assert.NotEqual(0u, (uint)(rig.Player.Flags & PlayerFlags.Ghost)); // still a ghost, still has its body behind
        Assert.Equal((100f, 100f), (rig.Player.Combat.Corpse!.X, rig.Player.Combat.Corpse.Y));
    }

    [Fact]
    public void AMissingFacing_IsZero_NotThePlayersOwn()
    {
        // vmangos GetWorldSafeLocFacing returns 0 for an id without a row (ObjectMgr.cpp:7697-7704).
        using var rig = new Rig(Elwynn(Loc(1, 0, 500, 600, 90)));
        rig.Player.Relocate(100, 100, 83.5f, 4f, 0);
        rig.DieAndRelease();

        rig.AnswerMovementOrders();

        Assert.Equal(0f, rig.Teleports.DestinationOf(rig.Player)!.Value.Orientation);
    }

    [Fact]
    public void NoLinkedGraveyard_LeavesTheGhostWhereItIs_AndNothingRepeats()
    {
        using var rig = new Rig(new GraveyardContent([Loc(1, 0, 500, 600, 90)], [new GraveyardLink(1, 99, 0)]));
        rig.DieAndRelease();

        rig.AnswerMovementOrders();
        rig.World.RunTick(100);

        Assert.False(rig.Teleports.IsBeingTeleported(rig.Player));
        Assert.Equal((100f, 100f), (rig.Player.X, rig.Player.Y));
        Assert.False(rig.Player.Combat.RepopPending); // cleared first, so a miss is not retried every tick
    }

    [Fact]
    public void OnlyTheEnemyHasAGraveyardHere_TheGhostStays_UnlessTheFallbackDeviationIsOn()
    {
        var onlyHorde = new GraveyardContent(
            [Loc(1, 0, 500, 600, 90), Loc(GraveyardCatalog.DefaultAllianceGraveyard, 0, 7, 8, 9)],
            [new GraveyardLink(1, 12, GraveyardCatalog.TeamHorde)]);

        using (var retail = new Rig(onlyHorde))
        {
            retail.DieAndRelease();
            retail.AnswerMovementOrders();
            Assert.False(retail.Teleports.IsBeingTeleported(retail.Player));
        }

        using var deviation = new Rig(onlyHorde, fallbackToDefaults: true);
        deviation.DieAndRelease();
        deviation.AnswerMovementOrders();
        Assert.Equal(new TeleportDestination(0, 7, 8, 9, 0), deviation.Teleports.DestinationOf(deviation.Player));
    }

    [Fact]
    public void AGraveyardOnAnotherMap_IsAFarTeleport_AndTheGhostKeepsItsStateAfterArriving()
    {
        using var rig = new Rig(new GraveyardContent([Loc(2, 1, 1000, 2000, 30, 1f)], [new GraveyardLink(2, 12, 0)]));
        rig.DieAndRelease();
        rig.AnswerMovementOrders();

        Assert.Equal(TeleportStage.Far, rig.Teleports.StageOf(rig.Player)); // SMSG_NEW_WORLD sent after the map update
        Assert.True(rig.Teleports.HandleWorldportAck(rig.Player));
        rig.World.RunTick(100);

        Assert.False(rig.Teleports.IsBeingTeleported(rig.Player));
        Assert.Equal(1u, rig.Player.MapId);
        Assert.Equal((1000f, 2000f, 30f), (rig.Player.X, rig.Player.Y, rig.Player.Z));
        Assert.NotEqual(0u, (uint)(rig.Player.Flags & PlayerFlags.Ghost));
        Assert.False(rig.Player.IsAlive);
        Assert.NotNull(rig.Player.Combat.Corpse);
        Assert.Equal(0u, rig.Player.Combat.Corpse.MapId); // the body stays on the map it was left on
    }

    [Fact]
    public void AnAlivePlayerThatFellUnderTheMap_IsSentToTheGraveyardToo()
    {
        using var rig = new Rig(Elwynn(Loc(1, 0, 500, 600, 90)));

        Assert.True(rig.Combat.Hooks.RepopAtGraveyard(rig.Player)); // the immediate hook the undermap observer calls

        Assert.True(rig.Teleports.IsBeingTeleportedNear(rig.Player));
        Assert.True(rig.Player.IsAlive);
    }

    [Fact]
    public void ASpiritReleasedOnATransport_ComesBackAliveAtFullHealth_AtTheGraveyard()
    {
        using var rig = new Rig(Elwynn(Loc(1, 0, 500, 600, 90)));
        MovementInfo movement = rig.Player.Movement;
        movement.Flags |= MovementFlags.OnTransport;
        movement.TransportGuid = 77;
        rig.Player.ApplyMovement(movement, 1);
        rig.DieAndRelease();

        rig.AnswerMovementOrders();

        Assert.True(rig.Player.IsAlive);
        Assert.Equal(rig.Player.MaxHealth, rig.Player.Health);
        Assert.Equal(0u, (uint)(rig.Player.Flags & PlayerFlags.Ghost));
        Assert.Null(rig.Player.Combat.Corpse);
        Assert.True(rig.Teleports.IsBeingTeleportedNear(rig.Player));
        Assert.Empty(rig.Combat.Corpses);
    }

    [Fact]
    public void ASpiritThatLogsOutWhileDying_IsSavedAtTheGraveyard_ButItsBodyStaysWhereItDied()
    {
        using var rig = new Rig(new GraveyardContent([Loc(1, 1, 1000, 2000, 30, 1f)], [new GraveyardLink(1, 12, 0)]));
        rig.Player.Health = 0;
        rig.Combat.KillPlayer(rig.Player);

        rig.World.RemovePlayer(rig.Player); // the logout save

        CharacterState saved = rig.Player.CreateSnapshot(0);
        Assert.Equal((1u, 1000f, 2000f, 30f, 1f), (saved.MapId, saved.X, saved.Y, saved.Z, saved.Orientation));
        Assert.True(saved.Life!.IsGhost);
        Assert.Equal((0u, 100f, 100f), (saved.Life.Corpse!.MapId, saved.Life.Corpse.X, saved.Life.Corpse.Y));
        Assert.False(rig.Teleports.IsBeingTeleported(rig.Player)); // a leaving client is never sent a teleport
        Assert.DoesNotContain(rig.Session.Sent, p => p.Opcode == WorldOpcode.MsgMoveTeleportAck);
    }

    [Fact]
    public void InABattleground_OnlyAnOverrideDecides_AndWithoutOneTheSpiritStays()
    {
        using var rig = new Rig(Elwynn(Loc(1, 0, 500, 600, 90)));
        Map bg = rig.World.GetMap(30);
        var session = new FakeSession(6);
        Player player = CombatTestKit.AddPlayer(rig.World, 6, 5, 5, session, mapId: 30);

        Assert.Null(rig.Service.Choose(player, bg)); // no override: stay

        var chosen = Loc(77, 0, 1, 2, 3);
        rig.Service.AddOverride(new FixedOverride(handles: true, chosen));
        Assert.Same(chosen, rig.Service.Choose(player, bg));
    }

    [Fact]
    public void ABattlegroundOverride_ThatDoesNotHandleThePlayer_IsSkipped()
    {
        using var rig = new Rig(Elwynn(Loc(1, 0, 500, 600, 90)));
        Map bg = rig.World.GetMap(30);
        Player player = CombatTestKit.AddPlayer(rig.World, 6, 5, 5, new FakeSession(6), mapId: 30);
        var second = Loc(78, 0, 4, 5, 6);
        rig.Service.AddOverride(new FixedOverride(handles: false, Loc(77, 0, 1, 2, 3)));
        rig.Service.AddOverride(new FixedOverride(handles: true, second));

        Assert.Same(second, rig.Service.Choose(player, bg));
    }

    private sealed class FixedOverride(bool handles, WorldSafeLoc? graveyard) : IGraveyardOverride
    {
        public bool TryChoose(Player player, out WorldSafeLoc? chosen)
        {
            chosen = graveyard;
            return handles;
        }
    }
}
