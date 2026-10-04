using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// SPELL_EFFECT_CHARGE, LEAP (Blink) and TELEPORT_UNITS_FACE_CASTER (mangoszero Spell::EffectCharge, EffectLeapForward,
/// EffectTeleUnitsFaceCaster, SpellEffectObjectCombat.cpp:944, :1245; SpellEffectSkillEnchantPet.cpp:413) and their cast checks (SpellChecks.cpp:1124, :1380).
/// </summary>
public sealed class ChargeEffectTests
{
    private const uint Charge = 940100;
    private const uint ChargeBonus = 940101;      // what a warrior's Charge triggers: rage for the caster, a stun for the target
    private const uint ChargeStunAura = 940102;
    private const uint FriendlyCharge = 940103;   // a positive charge: moves but does not attack
    private const uint Blink = 940110;
    private const uint FaceTeleport = 940120;
    private const uint FaceTeleportToDest = 940121;
    private const float Z = 83.5f;                // the height TestWorld gives its players

    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Z, actual.Z, 3);
    }

    private static float Radius(Unit unit) => unit.GetFloat(UpdateFields.UnitFieldBoundingradius);

    /// <summary>Where a charge stops from its target: the contact gap plus both bounding radii (players carry the radius of their model).</summary>
    private static float Gap(Unit caster, Unit target) => ForcedMovement.ChargeContactGap + Radius(caster) + Radius(target);

    private static SpellInfo Ranged(SpellInfo spell) => spell with { RangeIndex = 4, Range = new SpellRange(0, 30) };

    private static SpellTestKit Kit() => new(
        Ranged(Spell(Charge,
            Effect(SpellEffectName.Charge, 0, SpellImplicitTarget.UnitEnemy),
            Effect(SpellEffectName.TriggerSpell, 0, SpellImplicitTarget.UnitEnemy, trigger: ChargeBonus))),
        Spell(ChargeBonus,
            Effect(SpellEffectName.Energize, 90, SpellImplicitTarget.UnitCaster, misc: (int)PowerType.Rage),
            Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModStun)) with
        {
            Duration = new SpellDuration(1000, 0, 1000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
        },
        Ranged(Spell(FriendlyCharge, Effect(SpellEffectName.Charge, 0, SpellImplicitTarget.UnitFriend))),
        Spell(Blink, Effect(SpellEffectName.Leap, 0, SpellImplicitTarget.UnitCaster) with { Radius = 20f }),
        Ranged(Spell(FaceTeleport, Effect(SpellEffectName.TeleportUnitsFaceCaster, 0, SpellImplicitTarget.UnitEnemy) with { Radius = 5f })),
        Ranged(Spell(FaceTeleportToDest, Effect(SpellEffectName.TeleportUnitsFaceCaster, 0, SpellImplicitTarget.UnitEnemy) with { Radius = 5f })));

    private sealed record Move(ulong Guid, Vector3 Start, uint SplineId, byte Type, uint Flags, uint DurationMs, uint PointCount, Vector3 Destination);

    private static Move ReadMove(byte[] payload)
    {
        var r = new PacketReader(payload);
        ulong guid = r.ReadPackedGuid();
        var start = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        uint id = r.ReadUInt32();
        byte type = r.ReadByte();
        uint flags = r.ReadUInt32();
        uint duration = r.ReadUInt32();
        uint count = r.ReadUInt32();
        var destination = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        return new Move(guid, start, id, type, flags, duration, count, destination);
    }

    private static byte[][] Of(FakeSession session, WorldOpcode opcode) => [.. session.Sent.Where(p => p.Opcode == opcode).Select(p => p.Payload)];

    /// <summary>Static models and ground for the blink: a wall at <see cref="WallX"/> and a height function for the floor.</summary>
    private sealed class Obstacles : ILineOfSight
    {
        public float? WallX { get; set; }

        public Func<float, float?>? Floor { get; set; }

        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true)
            => WallX is not { } wall || (from.X - wall) * (to.X - wall) > 0;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            if (WallX is not { } wall || (from.X - wall) * (to.X - wall) > 0)
            {
                return false;
            }

            float t = (wall - from.X) / (to.X - from.X);
            hit = from + ((to - from) * t);
            return true;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => Floor?.Invoke(x);

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }

    private sealed class FixedPath(params Vector3[] corners) : IPathfinder
    {
        public PathType Type { get; init; } = PathType.Normal;

        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => new(Type, [start, .. corners, end]);
    }

    private sealed class MapObjectResolver : ISpellUnitResolver
    {
        public Unit? Find(Unit reference, ObjectGuid guid) => reference.Guid == guid ? reference : reference.Map?.FindObject(guid) as Unit;
    }

    // --- charge ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ModuleIsDiscovered_AndTheEffectsHaveHandlers()
    {
        using SpellTestKit kit = Kit();

        Assert.Contains(typeof(ChargeEffects), kit.System.Modules);
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.Charge));
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.Leap));
        Assert.True(kit.System.HasEffectHandler(SpellEffectName.TeleportUnitsFaceCaster));
    }

    [Fact]
    public void Charge_MovesTheCasterAdjacentToTheTarget_AndTheTriggeredSpellGrantsRageAndTheStun()
    {
        using SpellTestKit kit = Kit();
        (Player caster, FakeSession casterSession) = kit.AddPlayer(1, 0, 0);
        (Player target, _) = kit.AddPlayer(2, 20, 0);
        (_, FakeSession watcherSession) = kit.AddPlayer(3, 5, 5);
        casterSession.Clear();
        watcherSession.Clear();

        kit.System.CastSpell(caster, Charge, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        // ContactPointNear: on the line from the target toward the caster, 3.666666 yards plus both bounding radii from the target.
        Assert.Equal(20f - Gap(caster, target), caster.X, 3);
        Assert.Equal(0f, caster.Y, 3);
        Assert.Equal(Z, caster.Z, 3);
        Assert.Equal(0f, caster.Orientation, 3);          // faces along the leg
        Assert.Equal(20f, target.X);                      // the target stays where it is

        // The spline: one leg at 24 yards per second, walk mode (no run flag), no facing, to the contact point.
        Move own = ReadMove(Assert.Single(Of(casterSession, WorldOpcode.SmsgMonsterMove)));
        Move seen = ReadMove(Assert.Single(Of(watcherSession, WorldOpcode.SmsgMonsterMove)));
        Assert.Equal(own, seen with { });
        Assert.Equal(caster.Guid.Value, own.Guid);
        Assert.Equal(new Vector3(0, 0, Z), own.Start);
        Assert.Equal(0, own.Type);
        Assert.Equal(0u, own.Flags);
        Assert.Equal(1u, own.PointCount);
        AssertNear(new Vector3(20f - Gap(caster, target), 0, Z), own.Destination);
        Assert.Equal((uint)Math.Ceiling((20f - Gap(caster, target)) / ForcedMovement.ChargeSpeed * 1000), own.DurationMs);
        Assert.Equal(24f, ForcedMovement.ChargeSpeed);

        // The rest of the spell is the ordinary trigger spell: rage for the caster, the stun on the target.
        Assert.Equal(90u, SpellSystem.GetPower(caster, PowerType.Rage));
        Assert.True(kit.System.HasAura(target, ChargeBonus));
        Assert.Contains(kit.System.GetAuras(target), h => h.HasAura(AuraType.ModStun));

        // A negative spell starts the caster's melee attack on the target.
        Assert.Same(target, caster.Combat.Victim);
        Assert.True(caster.Combat.IsMeleeAttacking);
    }

    [Fact]
    public void Charge_StopsAtTheBoundingRadii_OfBothUnits()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player target, _) = kit.AddPlayer(2, 20, 0);
        caster.SetFloat(UpdateFields.UnitFieldBoundingradius, 0.4f);
        target.SetFloat(UpdateFields.UnitFieldBoundingradius, 1.1f);

        kit.System.CastSpell(caster, Charge, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(20f - (ForcedMovement.ChargeContactGap + 0.4f + 1.1f), caster.X, 3);
    }

    [Fact]
    public void Charge_FromTheOtherSide_ArrivesOnTheCastersSideOfTheTarget()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 0, 30);
        (Player target, _) = kit.AddPlayer(2, 0, 10);

        kit.System.CastSpell(caster, Charge, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(0f, caster.X, 3);
        Assert.Equal(10f + Gap(caster, target), caster.Y, 3);
        Assert.Equal(1.5f * MathF.PI, caster.Orientation, 3); // heading -y
    }

    [Fact]
    public void Charge_ThatCannotSeeTheContactPointFromTheTarget_UsesTheNextFreeAngle()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player target, _) = kit.AddPlayer(2, 20, 0);
        // A wall at x = 18: the points on the caster's side of the target are cut off from it; the first one the target sees is beside it.
        WorldCollision.Of(kit.World).Install(new Obstacles { WallX = 18 });

        kit.System.CastSpell(caster, Charge, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(20f, caster.X, 3);
        Assert.Equal(-Gap(caster, target), caster.Y, 3);
    }

    [Fact]
    public void Charge_FollowsTheNavigationPath_WhenItReachesTheContactPoint()
    {
        using SpellTestKit kit = Kit();
        (Player caster, FakeSession casterSession) = kit.AddPlayer(1, 0, 0);
        (Player target, _) = kit.AddPlayer(2, 20, 0);
        WorldCollision.Of(kit.World).Install(pathfinder: new FixedPath(new Vector3(5, 10, Z), new Vector3(10, 10, Z)));
        casterSession.Clear();

        kit.System.CastSpell(caster, Charge, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Move move = ReadMove(Assert.Single(Of(casterSession, WorldOpcode.SmsgMonsterMove)));
        Assert.Equal(3u, move.PointCount); // two corners and the destination
        AssertNear(new Vector3(20f - Gap(caster, target), 0, Z), move.Destination);
        float length = Vector3.Distance(new(0, 0, Z), new(5, 10, Z)) + Vector3.Distance(new(5, 10, Z), new(10, 10, Z))
            + Vector3.Distance(new(10, 10, Z), move.Destination);
        Assert.Equal((uint)Math.Ceiling(length / ForcedMovement.ChargeSpeed * 1000), move.DurationMs);
        Assert.Equal(move.Destination.X, caster.X, 3);
    }

    [Fact]
    public void Charge_GoesStraight_WhenThePathDoesNotReachTheContactPoint()
    {
        using SpellTestKit kit = Kit();
        (Player caster, FakeSession casterSession) = kit.AddPlayer(1, 0, 0);
        (Player target, _) = kit.AddPlayer(2, 20, 0);
        WorldCollision.Of(kit.World).Install(pathfinder: new FixedPath(new Vector3(5, 10, Z)) { Type = PathType.Incomplete });
        casterSession.Clear();

        kit.System.CastSpell(caster, Charge, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Move move = ReadMove(Assert.Single(Of(casterSession, WorldOpcode.SmsgMonsterMove)));
        Assert.Equal(1u, move.PointCount); // forced destination: the straight line
        Assert.Equal(20f - Gap(caster, target), caster.X, 3);
    }

    [Fact]
    public void Charge_WithNoPathAtAll_GoesStraight()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player target, _) = kit.AddPlayer(2, 20, 0);
        WorldCollision.Of(kit.World).Install(pathfinder: new FixedPath { Type = PathType.NoPath });

        kit.System.CastSpell(caster, Charge, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(20f - Gap(caster, target), caster.X, 3);
    }

    [Fact]
    public void ACharge_ThatIsPositive_MovesTheCaster_ButStartsNoAttack()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player friend, _) = kit.AddPlayer(2, 20, 0);
        Assert.True(kit.Store.Get(FriendlyCharge)!.IsPositiveSpell(kit.Store.Get));

        kit.System.CastSpell(caster, FriendlyCharge, SpellCastTargets.ForUnit(friend.Guid), triggered: true);

        Assert.Equal(20f - Gap(caster, friend), caster.X, 3);
        Assert.Null(caster.Combat.Victim);
    }

    [Fact]
    public void ACharge_AtOneself_MovesNobody()
    {
        using SpellTestKit kit = Kit();
        (Player caster, FakeSession session) = kit.AddPlayer(1, 7, 3);

        kit.System.CastSpell(caster, Charge, SpellCastTargets.ForUnit(caster.Guid), triggered: true);

        Assert.Equal(7f, caster.X);
        Assert.Equal(3f, caster.Y);
        Assert.Empty(Of(session, WorldOpcode.SmsgMonsterMove));
    }

    [Fact]
    public void ARootedCaster_CannotCharge_ButCanBlink()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player target, _) = kit.AddPlayer(2, 20, 0);
        kit.Spellbook.Teach(caster, Charge);
        kit.Spellbook.Teach(caster, Blink);
        caster.SetRooted(true);

        Assert.Equal(SpellCastResult.Rooted, kit.System.HandleCastRequest(caster, Charge, SpellCastTargets.ForUnit(target.Guid)));
        Assert.Equal(0f, caster.X);
        // "Blink has leap first and then removing of auras with root effect": the rooted leap passes the check.
        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Blink, SpellCastTargets.ForSelf()));
    }

    [Fact]
    public void AFreeCaster_PassesTheChargeCheck()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player target, _) = kit.AddPlayer(2, 20, 0);
        kit.Spellbook.Teach(caster, Charge);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Charge, SpellCastTargets.ForUnit(target.Guid)));
        Assert.Equal(20f - Gap(caster, target), caster.X, 3);
    }

    [Fact]
    public void ACreatureTarget_StopsMoving_WhenItIsCharged()
    {
        using SpellTestKit kit = Kit();
        Map map = kit.World.GetMap(0);
        var creatures = new CreatureMapSystem(map, Content([Template()], [Spawn(1, WolfEntry, 20, 0, Z)]), random: new Random(1));
        map.AddUpdater(creatures);
        kit.System.Units = new MapObjectResolver();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        kit.World.RunTick(50);
        Creature wolf = Assert.Single(creatures.Creatures);
        creatures.MovePath(wolf, [new Vector3(30, 0, Z)], run: false, SplineFacing.None);
        Assert.True(wolf.IsMoving);

        kit.System.CastSpell(caster, Charge, SpellCastTargets.ForUnit(wolf.Guid), triggered: true);

        Assert.False(wolf.IsMoving);
        Assert.Equal(wolf.X - Gap(caster, wolf), caster.X, 2);
        Assert.Same(wolf, caster.Combat.Victim);
    }

    [Fact]
    public void ACreatureCaster_ChargesATarget_AndItsOwnSplineEnds()
    {
        using SpellTestKit kit = Kit();
        Map map = kit.World.GetMap(0);
        var creatures = new CreatureMapSystem(map, Content([Template()], [Spawn(1, WolfEntry, 40, 0, Z)]), random: new Random(1));
        map.AddUpdater(creatures);
        kit.System.Units = new MapObjectResolver();
        (Player target, FakeSession session) = kit.AddPlayer(1, 20, 0);
        kit.World.RunTick(50);
        Creature wolf = Assert.Single(creatures.Creatures);
        creatures.MovePath(wolf, [new Vector3(60, 0, Z)], run: true, SplineFacing.None);
        session.Clear();

        kit.System.CastSpell(wolf, Charge, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.False(wolf.IsMoving);
        Assert.Equal(20f + Gap(wolf, target), wolf.X, 2);
        Assert.Equal(2, Of(session, WorldOpcode.SmsgMonsterMove).Length); // the stop of its own spline, then the charge spline
        Move charge = ReadMove(Of(session, WorldOpcode.SmsgMonsterMove)[1]);
        Assert.Equal(wolf.Guid.Value, charge.Guid);
        Assert.Equal(1u, charge.PointCount);
    }

    // --- blink ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Blink_MovesTheCasterForwardTheEffectRadius_ByANearTeleport()
    {
        using SpellTestKit kit = Kit();
        (Player caster, FakeSession session) = kit.AddPlayer(1, 0, 0);
        caster.Orientation = 0;

        kit.System.CastSpell(caster, Blink, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(20f, caster.X, 3);
        Assert.Equal(0f, caster.Y, 3);
        Assert.Equal(Z, caster.Z, 3);
        Assert.Single(Of(session, WorldOpcode.MsgMoveTeleportAck)); // the client is ordered to the new place
    }

    [Fact]
    public void Blink_FollowsTheFacing()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 100, 100);
        caster.Orientation = MathF.PI / 2;

        kit.System.CastSpell(caster, Blink, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(100f, caster.X, 3);
        Assert.Equal(120f, caster.Y, 3);
    }

    [Fact]
    public void Blink_StopsAtTheLastStepBeforeACollisionHit_WhenTheCollisionServiceIsAvailable()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        caster.Orientation = 0;
        WorldCollision.Of(kit.World).Install(new Obstacles { WallX = 9 });

        kit.System.CastSpell(caster, Blink, SpellCastTargets.ForSelf(), triggered: true);

        // Steps of 2 yards: 2, 4, 6, 8 are clear, the segment 8 -> 10 meets the wall at 9, so the blink ends at 8.
        Assert.Equal(8f, caster.X, 3);
        Assert.Equal(Z, caster.Z, 3);
    }

    [Fact]
    public void Blink_WithoutCollisionData_GoesTheWholeWay()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        WorldCollision.Of(kit.World).Install(OpenLineOfSight.Instance);

        kit.System.CastSpell(caster, Blink, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(20f, caster.X, 3);
    }

    [Fact]
    public void Blink_ThatStartsAgainstAWall_StaysWhereItIs()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        WorldCollision.Of(kit.World).Install(new Obstacles { WallX = 1 });

        kit.System.CastSpell(caster, Blink, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(0f, caster.X, 3);
    }

    [Fact]
    public void Blink_FollowsTheGround_UpAGentleSlope()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        // 0.5 yards of rise per yard: 26 degrees, under the 50 the reference allows.
        WorldCollision.Of(kit.World).Install(new Obstacles { Floor = x => Z + (0.5f * x) });

        kit.System.CastSpell(caster, Blink, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(20f, caster.X, 3);
        Assert.Equal(Z + 10f, caster.Z, 3);
    }

    [Fact]
    public void Blink_StopsBelowASlopeSteeperThanFiftyDegrees()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        // Flat to x = 6, then a 2.8 yard step per 2 yards: atan(1.4) = 54.5 degrees.
        WorldCollision.Of(kit.World).Install(new Obstacles { Floor = x => x <= 6 ? Z : Z + 2.8f });

        kit.System.CastSpell(caster, Blink, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(6f, caster.X, 3);
        Assert.Equal(Z, caster.Z, 3);
    }

    [Fact]
    public void Blink_StopsAtTheEdgeOfKnownGround()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        // Ground is known to x = 10 and nowhere beyond (a hole or the map's edge), no water: "maybe flying?" and the reference stays.
        WorldCollision.Of(kit.World).Install(new Obstacles { Floor = x => x <= 10 ? Z : null });

        kit.System.CastSpell(caster, Blink, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(10f, caster.X, 3);
    }

    [Fact]
    public void Blink_WhileFalling_GoesStraightAheadAndTwoYardsDown()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        caster.AddMovementFlags(MovementFlags.Jumping);
        // No floor within reach of the caster: the falling case.
        WorldCollision.Of(kit.World).Install(new Obstacles { Floor = _ => null });

        kit.System.CastSpell(caster, Blink, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(20f, caster.X, 3);
        Assert.Equal(Z - 2f, caster.Z, 3);
    }

    [Fact]
    public void Blink_WhileFalling_IsPulledBackFromAWallInTheWay()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        caster.AddMovementFlags(MovementFlags.Jumping);
        WorldCollision.Of(kit.World).Install(new Obstacles { Floor = _ => null, WallX = 12 });

        kit.System.CastSpell(caster, Blink, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(12f, caster.X, 3); // the hit point (the segment starts half a yard up)
    }

    [Fact]
    public void Blink_OnATaxiOrATransport_IsRefused()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        kit.Spellbook.Teach(caster, Blink);

        caster.UnitFlags |= UnitFlags.TaxiFlight;
        Assert.Equal(SpellCastResult.NotOnTaxi, kit.System.HandleCastRequest(caster, Blink, SpellCastTargets.ForSelf()));
        caster.UnitFlags &= ~UnitFlags.TaxiFlight;

        caster.AddMovementFlags(MovementFlags.OnTransport);
        Assert.Equal(SpellCastResult.NotOnTransport, kit.System.HandleCastRequest(caster, Blink, SpellCastTargets.ForSelf()));
        caster.RemoveMovementFlags(MovementFlags.OnTransport);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(caster, Blink, SpellCastTargets.ForSelf()));
    }

    // --- teleport units face caster ------------------------------------------------------------------------------------------

    [Fact]
    public void FaceCasterTeleport_PutsTheTargetInFrontOfTheCaster_FacingIt()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player target, _) = kit.AddPlayer(2, 30, 0);
        caster.Orientation = 0;

        kit.System.CastSpell(caster, FaceTeleport, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(5f + Radius(caster) + Radius(target), target.X, 3);  // the effect radius in front of the caster, plus both bounding radii (ClosePointNear)
        Assert.Equal(0f, target.Y, 3);
        Assert.Equal(MathF.PI, target.Orientation, 3);
        Assert.Equal(0f, caster.X);
    }

    [Fact]
    public void FaceCasterTeleport_UsesTheSpellsDestinationWhenItHasOne()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player target, _) = kit.AddPlayer(2, 30, 0);
        caster.Orientation = MathF.PI * 1.5f;
        var targets = new SpellCastTargets
        {
            Mask = SpellCastTargetFlags.Unit | SpellCastTargetFlags.DestLocation,
            Unit = target.Guid,
            Dest = (-12f, 7f, Z),
        };

        kit.System.CastSpell(caster, FaceTeleportToDest, targets, triggered: true);

        Assert.Equal(-12f, target.X, 3);
        Assert.Equal(7f, target.Y, 3);
        Assert.Equal((MathF.PI * 2.5f) - (2 * MathF.PI), target.Orientation, 3); // the caster's facing plus pi, wrapped
    }

    [Fact]
    public void FaceCasterTeleport_SkipsATargetOnATaxi()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player target, _) = kit.AddPlayer(2, 30, 0);
        target.UnitFlags |= UnitFlags.TaxiFlight;

        kit.System.CastSpell(caster, FaceTeleport, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        Assert.Equal(30f, target.X);
    }

    [Fact]
    public void FaceCasterTeleport_RefusesARootedCasterOrOneOnATaxi()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1, 0, 0);
        (Player target, _) = kit.AddPlayer(2, 10, 0);
        kit.Spellbook.Teach(caster, FaceTeleport);

        caster.SetRooted(true);
        Assert.Equal(SpellCastResult.Rooted, kit.System.HandleCastRequest(caster, FaceTeleport, SpellCastTargets.ForUnit(target.Guid)));
        caster.SetRooted(false);

        caster.UnitFlags |= UnitFlags.TaxiFlight;
        Assert.Equal(SpellCastResult.NotOnTaxi, kit.System.HandleCastRequest(caster, FaceTeleport, SpellCastTargets.ForUnit(target.Guid)));
    }
}
