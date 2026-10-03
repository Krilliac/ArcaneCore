using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.GameObjects.GameObjectTestKit;

namespace ArcaneCore.Game.Tests.GameObjects;

/// <summary>
/// GO5: chairs and cameras. References: D:\refs\vmangos\src\game\Objects\GameObject.cpp:1515-1533 (chair Use), :1613-1634 (camera Use),
/// :2229-2236 (PlayerCanUse chair), :2536-2582 (GetClosestChairSlotPosition), GameObjectDefines.h:799, ObjectMgr.cpp:8108-8118,
/// Player.cpp:6049-6056 and Server/Packets/Misc.cpp:789-797 (TriggerCinematic).
/// </summary>
public sealed class ChairCameraTests
{
    private const uint ChairEntry = 400;
    private const uint ThreeSeatEntry = 401;
    private const uint HighChairEntry = 402;
    private const uint BadHeightEntry = 403;
    private const uint CameraEntry = 404;
    private const uint SilentCameraEntry = 405;
    private const uint BinderEntry = 406;
    private const float ChairZ = 83.5f;

    private sealed class Rig
    {
        public Rig(params GameObjectSpawn[] spawns)
        {
            GameObjectTemplate[] templates =
            [
                GoTemplate(ChairEntry, GameObjectType.Chair, (0, 2), (1, 0)),
                GoTemplate(ThreeSeatEntry, GameObjectType.Chair, (0, 3), (1, 0)) with { Size = 1.5f },
                GoTemplate(HighChairEntry, GameObjectType.Chair, (0, 1), (1, 2)),
                GoTemplate(BadHeightEntry, GameObjectType.Chair, (0, 1), (1, 3)),
                GoTemplate(CameraEntry, GameObjectType.Camera, (1, 17), (2, 5)),
                GoTemplate(SilentCameraEntry, GameObjectType.Camera, (1, 0)),
                GoTemplate(BinderEntry, GameObjectType.Binder),
            ];
            World = TestWorld.CreateRuntime();
            Map map = World.GetMap(0);
            System = new GameObjectMapSystem(map, new GameObjectContent(templates, spawns, [], [], []));
            map.AddUpdater(System);
        }

        public WorldRuntime World { get; }

        public GameObjectMapSystem System { get; }

        public (Player Player, FakeSession Session) Join(uint guid, float x, float y)
        {
            (Player player, FakeSession session) = Player(guid, x, y);
            World.AddPlayer(player);
            World.RunTick(50);
            player.Relocate(x, y, ChairZ, 0, 0);
            session.Clear();
            return (player, session);
        }

        public GameObject Single(uint entry) => System.GameObjects.Single(g => g.Entry == entry);
    }

    private static GameObject Chair(uint entry, float x, float y, float orientation, uint slots, float scale = 1.0f)
        => new(1, GoTemplate(entry, GameObjectType.Chair, (0, (uint)slots)) with { Size = scale }, GoSpawn(1, entry, x, y, orientation: orientation));

    private static GameObject Chair(uint entry, float x, float y, float orientation, float scale = 1.0f)
    {
        return Chair(entry, x, y, orientation, slots: 3, scale);
    }

    // --- geometry ----------------------------------------------------------------------------------

    [Fact]
    public void ClosestSlot_PutsTheSlotsOnTheLinePerpendicularToTheOrientation_AndPicksTheNearest()
    {
        // Three slots, template size 1.5, orientation 0: the slot line is the y axis, at relative -1.5, 0, +1.5 from the centre.
        GameObject chair = Chair(ThreeSeatEntry, 10, 20, 0, scale: 1.5f);

        (float x, float y) = GameObjectChairs.ClosestSlot(chair, 12, 22);
        Assert.Equal(10f, x, 4);
        Assert.Equal(21.5f, y, 4);
        (x, y) = GameObjectChairs.ClosestSlot(chair, 12, 18);
        Assert.Equal(10f, x, 4);
        Assert.Equal(18.5f, y, 4);
        (x, y) = GameObjectChairs.ClosestSlot(chair, 12, 20.2f);
        Assert.Equal(10f, x, 4);
        Assert.Equal(20f, y, 4);
    }

    [Fact]
    public void ClosestSlot_RotatesWithTheOrientation_AndTheLaterSlotWinsATie()
    {
        // Two slots of size 1 at orientation pi/2: the line is the x axis (orthogonal = pi); slot 0 is at x + 0.5 and slot 1 at x - 0.5.
        GameObject go = Chair(ChairEntry, 10, 20, MathF.PI / 2, slots: 2);

        (float x, float y) = GameObjectChairs.ClosestSlot(go, 3, 20);
        Assert.Equal(9.5f, x, 4);
        Assert.Equal(20f, y, 3);
        (x, y) = GameObjectChairs.ClosestSlot(go, 30, 20);
        Assert.Equal(10.5f, x, 4);

        // Equally near both slots (user on the perpendicular bisector): the later slot (i = 1, at x = 9.5) wins because the comparison is <=.
        (x, _) = GameObjectChairs.ClosestSlot(go, 10, 25);
        Assert.Equal(9.5f, x, 4);
    }

    [Fact]
    public void ClosestSlot_UsesTheCentre_WithNoSlots_OrNoSlotWithinAHundredYards()
    {
        GameObject noSlots = Chair(ChairEntry, 10, 20, 0, slots: 0);
        Assert.Equal((10f, 20f), GameObjectChairs.ClosestSlot(noSlots, 11, 21));

        GameObject chair = Chair(ThreeSeatEntry, 10, 20, 0, scale: 1.5f);
        Assert.Equal((10f, 20f), GameObjectChairs.ClosestSlot(chair, 500, 500));
    }

    [Theory]
    [InlineData(0u, (byte)0)]
    [InlineData(1u, (byte)1)]
    [InlineData(2u, (byte)2)]
    [InlineData(3u, (byte)0)]
    [InlineData(99u, (byte)0)]
    public void Height_IsZeroToTwo_AndAnythingElseIsAReportedDataErrorFixedToZero(uint data1, byte expected)
    {
        GameObjectTemplate t = GoTemplate(1, GameObjectType.Chair, (1, data1));
        Assert.Equal(expected, GameObjectChairs.Height(t));
        Assert.Equal(StandState.SitLowChair + expected, GameObjectChairs.SeatedState(t));
    }

    // --- use ---------------------------------------------------------------------------------------

    [Fact]
    public void UsingAChair_MovesTheUserToTheNearestSlot_FacesTheChair_AndSitsLow()
    {
        var rig = new Rig(GoSpawn(1, ChairEntry, 10, 0, orientation: 1.25f));
        (Player player, FakeSession session) = rig.Join(1, 10.4f, 0.8f);
        GameObject chair = rig.Single(ChairEntry);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, chair.Guid));

        // Two slots of size 1 along the line at orientation + pi/2: the slot nearer to the user is used.
        (float slotX, float slotY) = GameObjectChairs.ClosestSlot(chair, 10.4f, 0.8f);
        Assert.Equal(slotX, player.X, 3);
        Assert.Equal(slotY, player.Y, 3);
        Assert.Equal(ChairZ, player.Z, 3);
        Assert.Equal(1.25f, player.Orientation, 3);
        Assert.Equal(StandState.SitLowChair, player.StandState);
        Assert.Single(Packets(session, WorldOpcode.MsgMoveTeleportAck));
    }

    [Fact]
    public void TheChairHeightSelectsTheSittingPose_AndABadHeightFallsBackToLow()
    {
        var rig = new Rig(GoSpawn(1, HighChairEntry, 10, 0), GoSpawn(2, BadHeightEntry, 30, 0));
        (Player high, _) = rig.Join(1, 10, 0);
        (Player bad, _) = rig.Join(2, 30, 0);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(high, rig.Single(HighChairEntry).Guid));
        Assert.Equal(StandState.SitHighChair, high.StandState);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(bad, rig.Single(BadHeightEntry).Guid));
        Assert.Equal(StandState.SitLowChair, bad.StandState);
    }

    [Fact]
    public void TheUserMustBeWithinThreeYardsOfTheSlot_NotJustTheChair()
    {
        var rig = new Rig(GoSpawn(1, HighChairEntry, 10, 0));
        (Player player, FakeSession session) = rig.Join(1, 10 + 3.01f, 0);
        GameObject chair = rig.Single(HighChairEntry);

        Assert.Equal(GameObjectUseResult.TooFar, rig.System.Use(player, chair.Guid));
        Assert.Equal(StandState.Stand, player.StandState);
        Assert.Equal(13.01f, player.X, 3);
        Assert.Empty(Packets(session, WorldOpcode.MsgMoveTeleportAck));

        player.Relocate(10 + 2.99f, 0, ChairZ, 0, 0);
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, chair.Guid));
        Assert.Equal(StandState.SitHighChair, player.StandState);

        // The 3 yard rule is 3D: the same horizontal distance with a large height difference is refused.
        player.SetStandState(StandState.Stand);
        player.Relocate(10, 0, ChairZ + 3.5f, 0, 0);
        Assert.Equal(GameObjectUseResult.TooFar, rig.System.Use(player, chair.Guid));
    }

    [Fact]
    public void ABlockedLineOfSight_RefusesTheChair()
    {
        var rig = new Rig(GoSpawn(1, HighChairEntry, 10, 0));
        WorldCollision.Of(rig.World).Install(new ArcaneCore.Game.Tests.Collision.CollisionSeamTests.WallAtX(11));
        (Player player, FakeSession session) = rig.Join(1, 12, 0);

        Assert.Equal(GameObjectUseResult.LineOfSight, rig.System.Use(player, rig.Single(HighChairEntry).Guid));
        Assert.Equal(StandState.Stand, player.StandState);
        Assert.Equal(12f, player.X, 3);
        Assert.Empty(Packets(session, WorldOpcode.MsgMoveTeleportAck));
    }

    [Fact]
    public void TwoUsersTakeTheirOwnNearestSlots_AndStandingUpWorks()
    {
        var rig = new Rig(GoSpawn(1, ChairEntry, 10, 0, orientation: 0));
        (Player north, _) = rig.Join(1, 10, 2);
        (Player south, _) = rig.Join(2, 10, -2);
        GameObject chair = rig.Single(ChairEntry);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(north, chair.Guid));
        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(south, chair.Guid));
        Assert.Equal(0.5f, north.Y, 3);
        Assert.Equal(-0.5f, south.Y, 3);

        north.SetStandState(StandState.Stand);
        Assert.Equal(StandState.Stand, north.StandState);
        Assert.Equal(StandState.SitLowChair, south.StandState);
    }

    // --- camera and the other types ----------------------------------------------------------------

    [Fact]
    public void ACamera_SendsTriggerCinematic_OnlyWhenItHasACinematic()
    {
        var rig = new Rig(GoSpawn(1, CameraEntry, 3, 0), GoSpawn(2, SilentCameraEntry, 4, 0));
        (Player player, FakeSession session) = rig.Join(1, 0, 0);

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, rig.Single(CameraEntry).Guid));
        (WorldOpcode opcode, byte[] payload) = Assert.Single(NonUpdatePackets(session));
        Assert.Equal(WorldOpcode.SmsgTriggerCinematic, opcode);
        Assert.Equal(BitConverter.GetBytes(17u), payload);
        Assert.Equal(BitConverter.GetBytes(17u), CinematicPackets.TriggerCinematic(17));

        Assert.Equal(GameObjectUseResult.Ok, rig.System.Use(player, rig.Single(SilentCameraEntry).Guid));
        Assert.Empty(NonUpdatePackets(session));
    }

    [Fact]
    public void ABinder_StillAnswersTheDefaultBranch()
    {
        var rig = new Rig(GoSpawn(1, BinderEntry, 3, 0));
        (Player player, _) = rig.Join(1, 0, 0);

        Assert.Equal(GameObjectUseResult.NotUsable, rig.System.Use(player, rig.Single(BinderEntry).Guid));
    }
}
