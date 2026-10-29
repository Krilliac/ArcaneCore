using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>
/// The server never rewrites a living player's Z to the terrain surface behind the client's back (an underground city on a map
/// without vmaps was snapped to the surface and saved there). vmangos parity: MovementHandler.cpp:1115-1128, Player.h:1789-1801.
/// Recovery exists only while MovementFlags.FallingFar is set, more than 100 yards below a model-aware ground, with a
/// valid, armed LastSafePosition within 100 yards (2D) on the same map (vmangos m_lastSafePosition / m_undermapPosValid,
/// Player.h:1789-1801), and goes through the near teleport (MSG_MOVE_TELEPORT_ACK). A <see cref="TeleportService"/> constructed over
/// the world registers itself, which is how the observer finds it.
/// </summary>
public sealed class UndermapNoSilentSnapTests
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

    private static void Move(HazardTestKit kit, Player p, float z, MovementFlags flags = MovementFlags.None)
    {
        MovementInfo previous = p.Movement;
        var incoming = new MovementInfo { Flags = flags, Time = 1, X = HazardTestKit.X, Y = HazardTestKit.Y, Z = z };
        var context = new MovementObserverContext(p, kit.Kit.World, WorldOpcode.MsgMoveHeartbeat);
        MovementObservers.Before(context, in previous, ref incoming);
        p.ApplyClientMovement(incoming, 1);
        MovementObservers.After(context, in previous);
    }

    private static void AssertNotRewritten(Player p, float z)
    {
        Assert.Equal(z, p.Z);
        Assert.Equal(z, p.Movement.Z);
        Assert.Equal(z, p.CreateSnapshot(1).Z);
    }

    [Fact]
    public void ANonFallingPlayer_SixtyYardsUnderTheTerrain_IsNeverRewrittenToTheSurface_NoModels()
    {
        using var kit = new HazardTestKit(liquid: null);
        Player p = kit.AddPlayer(4_106_001, 20f, out _);

        Move(kit, p, Ground - 60f);

        AssertNotRewritten(p, Ground - 60f);
    }

    [Fact]
    public void ANonFallingPlayer_SixtyYardsUnderTheTerrain_IsNeverRewritten_EvenWhenModelsConvictIt()
    {
        using var kit = new HazardTestKit(liquid: null);
        WorldCollision.Of(kit.Kit.World).Install(lineOfSight: new LoadedNoFloorModels());
        Player p = kit.AddPlayer(4_106_002, 20f, out _);

        Move(kit, p, Ground - 60f);

        AssertNotRewritten(p, Ground - 60f);
    }

    [Fact]
    public void ANonFallingPlayer_TwelveYardsUnderTheTerrain_IsNotRewritten()
    {
        // 12 yd is past the old 8 yd recovery depth and below the 50 yd report threshold.
        using var kit = new HazardTestKit(liquid: null);
        Player p = kit.AddPlayer(4_106_003, 20f, out _);

        Move(kit, p, Ground - 12f);

        AssertNotRewritten(p, Ground - 12f);
    }

    private static (HazardTestKit Kit, TeleportService Teleports, Player Player, FakeSession Session) RecoveryRig(
        uint guid, bool models, (uint MapId, float X, float Y, float Z, float O)? safe)
    {
        var kit = new HazardTestKit(liquid: null);
        if (models)
        {
            WorldCollision.Of(kit.Kit.World).Install(lineOfSight: new LoadedNoFloorModels());
        }

        var teleports = new TeleportService(kit.Kit.World, _ => { }, _ => { });
        Player p = kit.AddPlayer(guid, 20f, out FakeSession session);
        p.LastSafePosition = safe;
        p.UndermapRecallArmed = safe is not null;
        return (kit, teleports, p, session);
    }

    private static bool SentTeleportAck(FakeSession s) => s.Sent.Any(x => x.Opcode == WorldOpcode.MsgMoveTeleportAck);

    [Fact]
    public void FallingFar_MoreThanAHundredYardsBelowModelAwareGround_RecoversToLastSafePositionViaNearTeleport()
    {
        (uint, float, float, float, float) safe = (0u, HazardTestKit.X + 10f, HazardTestKit.Y, Ground + 5f, 1.5f);
        var (kit, teleports, p, session) = RecoveryRig(4_106_010, models: true, safe);
        using (kit)
        {
            Move(kit, p, Ground - 120f, MovementFlags.FallingFar);

            Assert.True(teleports.IsBeingTeleportedNear(p));
            Assert.Equal(new TeleportDestination(0, safe.Item2, safe.Item3, safe.Item4, safe.Item5), teleports.DestinationOf(p));
            Assert.True(SentTeleportAck(session));

            Assert.True(teleports.HandleTeleportAck(p, p.Guid.Value));
            Assert.Equal((safe.Item2, safe.Item3, safe.Item4), (p.X, p.Y, p.Z));
            Assert.Equal(safe.Item4, p.Movement.Z);
            Assert.Equal(safe.Item4, p.CreateSnapshot(1).Z);
        }
    }

    [Fact]
    public void FallingFar_OnlySixtyYardsBelow_DoesNotRecover_AndIsNotRewritten()
    {
        (uint, float, float, float, float) safe = (0u, HazardTestKit.X + 10f, HazardTestKit.Y, Ground + 5f, 1.5f);
        var (kit, teleports, p, session) = RecoveryRig(4_106_011, models: true, safe);
        using (kit)
        {
            Move(kit, p, Ground - 60f, MovementFlags.FallingFar);

            Assert.False(teleports.IsBeingTeleported(p));
            Assert.False(SentTeleportAck(session));
            AssertNotRewritten(p, Ground - 60f);
        }
    }

    [Fact]
    public void FallingFar_WithoutALastSafePosition_DoesNotRecover_AndIsNotRewritten()
    {
        var (kit, teleports, p, session) = RecoveryRig(4_106_012, models: true, safe: null);
        using (kit)
        {
            Move(kit, p, Ground - 120f, MovementFlags.FallingFar);

            Assert.False(teleports.IsBeingTeleported(p));
            Assert.False(SentTeleportAck(session));
            AssertNotRewritten(p, Ground - 120f);
        }
    }

    [Fact]
    public void FallingFar_LastSafePositionBeyondAHundredYards_DoesNotRecover_AndIsNotRewritten()
    {
        var (kit, teleports, p, session) = RecoveryRig(4_106_013, models: true, (0u, HazardTestKit.X + 150f, HazardTestKit.Y, Ground + 5f, 0f));
        using (kit)
        {
            Move(kit, p, Ground - 120f, MovementFlags.FallingFar);

            Assert.False(teleports.IsBeingTeleported(p));
            Assert.False(SentTeleportAck(session));
            AssertNotRewritten(p, Ground - 120f);
        }
    }

    [Fact]
    public void FallingFar_LastSafePositionOnAnotherMap_DoesNotRecover_AndIsNotRewritten()
    {
        var (kit, teleports, p, session) = RecoveryRig(4_106_014, models: true, (1u, HazardTestKit.X + 10f, HazardTestKit.Y, Ground + 5f, 0f));
        using (kit)
        {
            Move(kit, p, Ground - 120f, MovementFlags.FallingFar);

            Assert.False(teleports.IsBeingTeleported(p));
            Assert.False(SentTeleportAck(session));
            AssertNotRewritten(p, Ground - 120f);
        }
    }

    [Fact]
    public void FallingFar_WithNoModelService_NeverRecovers_BecauseTerrainAloneDoesNotConvict()
    {
        (uint, float, float, float, float) safe = (0u, HazardTestKit.X + 10f, HazardTestKit.Y, Ground + 5f, 1.5f);
        var (kit, teleports, p, session) = RecoveryRig(4_106_015, models: false, safe);
        using (kit)
        {
            Move(kit, p, Ground - 120f, MovementFlags.FallingFar);

            Assert.False(teleports.IsBeingTeleported(p));
            Assert.False(SentTeleportAck(session));
            AssertNotRewritten(p, Ground - 120f);
        }
    }

    private static readonly (uint, float, float, float, float) NearSafe = (0u, HazardTestKit.X + 10f, HazardTestKit.Y, Ground + 5f, 1.5f);

    [Fact]
    public void ARecall_KeepsLastSafePositionForTheUnstuckSpell_AndASecondFarFallDoesNotRecallAgain()
    {
        var (kit, teleports, p, session) = RecoveryRig(4_106_020, models: true, NearSafe);
        using (kit)
        {
            Move(kit, p, Ground - 120f, MovementFlags.FallingFar);
            Assert.True(teleports.IsBeingTeleportedNear(p));
            Assert.True(teleports.HandleTeleportAck(p, p.Guid.Value));

            Assert.Equal(NearSafe, p.LastSafePosition); // vmangos keeps m_lastSafePosition; Spell::EffectStuck still reads it
            Assert.False(p.UndermapRecallArmed);

            session.Clear();
            Move(kit, p, Ground - 120f, MovementFlags.FallingFar); // no free move in between
            Assert.False(teleports.IsBeingTeleported(p));
            Assert.False(SentTeleportAck(session));
        }
    }

    [Fact]
    public void ASafePositionBeyondAHundredYards_IsDisarmed_ButStaysForTheUnstuckSpell()
    {
        (uint, float, float, float, float) far = (0u, HazardTestKit.X + 150f, HazardTestKit.Y, Ground + 5f, 0f);
        var (kit, teleports, p, _) = RecoveryRig(4_106_021, models: true, far);
        using (kit)
        {
            Move(kit, p, Ground - 120f, MovementFlags.FallingFar);

            Assert.False(teleports.IsBeingTeleported(p));
            Assert.Equal(far, p.LastSafePosition);
            Assert.False(p.UndermapRecallArmed);
        }
    }

    [Fact]
    public void ASafePositionOnAnotherMap_IsDisarmed_ButStaysForTheUnstuckSpell()
    {
        (uint, float, float, float, float) other = (1u, HazardTestKit.X + 10f, HazardTestKit.Y, Ground + 5f, 0f);
        var (kit, teleports, p, _) = RecoveryRig(4_106_022, models: true, other);
        using (kit)
        {
            Move(kit, p, Ground - 120f, MovementFlags.FallingFar);

            Assert.False(teleports.IsBeingTeleported(p));
            Assert.Equal(other, p.LastSafePosition);
            Assert.False(p.UndermapRecallArmed);
        }
    }

    [Fact]
    public void AnUnarmedSafePosition_IsNotUsed()
    {
        var (kit, teleports, p, _) = RecoveryRig(4_106_023, models: true, NearSafe);
        using (kit)
        {
            p.UndermapRecallArmed = false;
            Move(kit, p, Ground - 120f, MovementFlags.FallingFar);

            Assert.False(teleports.IsBeingTeleported(p));
            Assert.Equal(NearSafe, p.LastSafePosition);
        }
    }

    [Fact]
    public void APlayerAlreadyBeingTeleported_IsNotRecalled_AndStaysArmed()
    {
        var (kit, teleports, p, _) = RecoveryRig(4_106_024, models: true, NearSafe);
        using (kit)
        {
            Assert.True(teleports.TeleportTo(p, 0, HazardTestKit.X + 2f, HazardTestKit.Y, Ground + 1f, 0f));
            TeleportDestination? before = teleports.DestinationOf(p);

            Move(kit, p, Ground - 120f, MovementFlags.FallingFar);

            Assert.Equal(before, teleports.DestinationOf(p));
            Assert.True(p.UndermapRecallArmed);
        }
    }

    [Fact]
    public void OnWarsongGulch_FarFallingBelowTwoFiftyIsRecalled_WithoutAnyGroundData()
    {
        using var kit = new HazardTestKit(liquid: null);
        WorldMaps.Of(kit.Kit.World).Load(new MapContent(
            [
                new MapTemplate(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""),
                new MapTemplate(489, 0, MapType.Battleground, 0, 20, 0, -1, 0, 0, "Warsong Gulch", ""),
            ],
            [],
            [],
            [],
            []));
        var teleports = new TeleportService(kit.Kit.World, _ => { }, _ => { })
        {
            BattlegroundEntryAllowed = (_, _) => true, // the player is bound to the match it is in
        };
        var session = new FakeSession(4_106_030);
        Player p = TestWorld.CreatePlayer(4_106_030, HazardTestKit.X, HazardTestKit.Y, session, mapId: 489);
        p.Level = 60;
        p.MaxHealth = 3000;
        p.Health = 3000;
        p.Relocate(HazardTestKit.X, HazardTestKit.Y, 300f, 0, 1);
        kit.Kit.World.AddPlayer(p);
        kit.Kit.World.RunTick(0);
        session.Clear();
        (uint, float, float, float, float) safe = (489u, HazardTestKit.X + 10f, HazardTestKit.Y, 320f, 0f);
        p.LastSafePosition = safe;
        p.UndermapRecallArmed = true;

        Move(kit, p, 240f, MovementFlags.FallingFar);

        Assert.True(teleports.IsBeingTeleportedNear(p));
        Assert.Equal(new TeleportDestination(489, safe.Item2, safe.Item3, safe.Item4, safe.Item5), teleports.DestinationOf(p));
        Assert.Equal(safe, p.LastSafePosition);
    }

    [Fact]
    public void TheRecall_IsANearTeleportThatKeepsTheCombatAndThePet()
    {
        // The options passed to the near teleport (TELE_TO_NOT_LEAVE_COMBAT | TELE_TO_NOT_UNSUMMON_PET, Player.h:1798).
        var (kit, teleports, p, _) = RecoveryRig(4_106_025, models: true, NearSafe);
        using (kit)
        {
            TeleportOptions seen = TeleportOptions.None;
            teleports.NearTeleportStarting += (_, _, options) => seen = options;

            Move(kit, p, Ground - 120f, MovementFlags.FallingFar);

            Assert.Equal(TeleportOptions.NotLeaveCombat | TeleportOptions.NotUnsummonPet, seen);
        }
    }
}
