using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Game.Tests.Locomotion;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.AntiCheat;

/// <summary>
/// Pins the FallingFar gate of the undermap recall (vmangos MovementHandler.cpp:1115-1128, Player.h:1789-1801): a player 120 yards
/// below a model-convicted ground with an armed, in-range safe position is recalled only while MovementFlags.FallingFar is set.
/// Without the flag the observer must neither teleport nor ack, must stay armed, and must not rewrite the player's Z.
/// </summary>
public sealed class UndermapObserverFallingFarTests
{
    private const float Ground = HazardTestKit.Ground;

    private sealed class LoadedNoFloorModels : ILineOfSight, IModelCoverage
    {
        public bool Enabled => true;

        public ModelCoverage CoverageAt(uint mapId, int tileX, int tileY) => ModelCoverage.Loaded;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => null;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }

    private static void Move(HazardTestKit kit, Player p, float z, MovementFlags flags)
    {
        MovementInfo previous = p.Movement;
        var incoming = new MovementInfo { Flags = flags, Time = 1, X = HazardTestKit.X, Y = HazardTestKit.Y, Z = z };
        var context = new MovementObserverContext(p, kit.Kit.World, WorldOpcode.MsgMoveHeartbeat);
        MovementObservers.Before(context, in previous, ref incoming);
        p.ApplyClientMovement(incoming, 1);
        MovementObservers.After(context, in previous);
    }

    [Fact]
    public void WithoutFallingFar_ADeepPlayerWithAnArmedSafePosition_IsNotRecalled_AndStaysArmed()
    {
        (uint, float, float, float, float) safe = (0u, HazardTestKit.X + 10f, HazardTestKit.Y, Ground + 5f, 1.5f);
        using var kit = new HazardTestKit(liquid: null);
        WorldCollision.Of(kit.Kit.World).Install(lineOfSight: new LoadedNoFloorModels());
        var teleports = new TeleportService(kit.Kit.World, _ => { }, _ => { });
        Player p = kit.AddPlayer(4_107_001, 20f, out FakeSession session);
        p.LastSafePosition = safe;
        p.UndermapRecallArmed = true;

        Move(kit, p, Ground - 120f, MovementFlags.None);

        Assert.False(teleports.IsBeingTeleported(p));
        Assert.DoesNotContain(session.Sent, x => x.Opcode == WorldOpcode.MsgMoveTeleportAck);
        Assert.True(p.UndermapRecallArmed);
        Assert.Equal(safe, p.LastSafePosition);
        Assert.Equal(Ground - 120f, p.Movement.Z);
        Assert.Equal(Ground - 120f, p.CreateSnapshot(1).Z);
    }
}
