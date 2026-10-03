using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.GridTerrain;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Ranged;

/// <summary>
/// The movement leeway of Spell::CheckRange (vmangos Object.cpp:1890-1912 GetLeewayBonusRange,
/// ObjectDefines.h:56-57: +2.66 yd when a player is involved and both units move laterally faster
/// than 4.97 yd/s) and the minimum-range dead zone (Spell.cpp:6911-6945).
/// </summary>
public sealed class RangeLeewayTests
{
    private static readonly SpellInfo Shot = new()
    {
        Id = 1,
        RangeIndex = 4,
        Range = new SpellRange(0, 35),
    };

    private static readonly SpellInfo DeadZoneShot = Shot with { Range = new SpellRange(5, 35) };

    private static void Move(Unit unit, MovementFlags flags)
    {
        unit.ApplyMovement(new MovementInfo { Flags = flags, X = unit.X, Y = unit.Y, Z = unit.Z, Orientation = unit.Orientation }, 0);
    }

    private static (Player Caster, Player Target) Pair(float distance)
    {
        Player caster = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
        Player target = TestWorld.CreatePlayer(2, distance, 0, new FakeSession(2));
        return (caster, target);
    }

    [Fact]
    public void Bonus_NeedsAPlayerAndBothSidesRunning()
    {
        (Player a, Player b) = Pair(10);
        Assert.Equal(0f, RangeLeeway.Bonus(a, b));

        Move(a, MovementFlags.Forward);
        Assert.Equal(0f, RangeLeeway.Bonus(a, b));            // only one side moves

        Move(b, MovementFlags.StrafeLeft);
        Assert.Equal(2.66f, RangeLeeway.Bonus(a, b));          // run speed 7.0 > 4.97 on both sides
        Assert.Equal(2.66f, RangeLeeway.Bonus(b, a));
        Assert.Equal(2.66f, RangeLeeway.BonusRange);
        Assert.Equal(4.97f, RangeLeeway.MinMoveSpeed);
    }

    [Theory]
    [InlineData(MovementFlags.Forward | MovementFlags.WalkMode)]          // walk 2.5
    [InlineData(MovementFlags.Backward)]                                  // run back 4.5 < 4.97
    [InlineData(MovementFlags.Forward | MovementFlags.Swimming)]          // swim 4.72 < 4.97
    [InlineData(MovementFlags.Jumping)]                                   // no lateral flag at all
    [InlineData(MovementFlags.None)]
    public void Bonus_IsZeroWhenASideIsTooSlow(MovementFlags slow)
    {
        (Player a, Player b) = Pair(10);
        Move(a, MovementFlags.Forward);
        Move(b, slow);

        Assert.Equal(0f, RangeLeeway.Bonus(a, b));
    }

    [Fact]
    public void XzSpeed_FollowsTheFlagsAndTheUnitsSpeeds()
    {
        (Player a, _) = Pair(10);
        a.RunSpeed = 8.5f;
        Move(a, MovementFlags.Forward);
        Assert.Equal(8.5f, RangeLeeway.XzFlagBasedSpeed(a));
        Move(a, MovementFlags.Forward | MovementFlags.WalkMode);
        Assert.Equal(a.WalkSpeed, RangeLeeway.XzFlagBasedSpeed(a));
        Move(a, MovementFlags.Backward);
        Assert.Equal(a.RunBackSpeed, RangeLeeway.XzFlagBasedSpeed(a));
        Move(a, MovementFlags.Forward | MovementFlags.Swimming);
        Assert.Equal(a.SwimSpeed, RangeLeeway.XzFlagBasedSpeed(a));
        Move(a, MovementFlags.Backward | MovementFlags.Swimming);
        Assert.Equal(a.SwimBackSpeed, RangeLeeway.XzFlagBasedSpeed(a));
        Move(a, MovementFlags.None);
        Assert.Equal(0f, RangeLeeway.XzFlagBasedSpeed(a));
    }

    [Fact]
    public void ACreatureCastingAtAMovingPlayer_GetsTheBonusToo_ButTwoCreaturesDoNot()
    {
        (Player player, _) = Pair(10);
        var creature = new TestUnit(1, 0, 0, 83.5f);
        var other = new TestUnit(2, 5, 0);
        Move(player, MovementFlags.Forward);
        Move(creature, MovementFlags.Forward);
        Move(other, MovementFlags.Forward);

        Assert.Equal(2.66f, RangeLeeway.Bonus(creature, player));
        Assert.Equal(0f, RangeLeeway.Bonus(creature, other));
        Assert.Equal(0f, RangeLeeway.Bonus(creature, null));
    }

    // --- CheckRange ------------------------------------------------------------------------

    // Two players: combat distance = distance - 3 (both combat reaches 1.5); a player gets 1.25 yd at cast start.
    [Fact]
    public void CastStart_AllowsTheMaximumPlus1_25_AndBothRunningWidensItBy2_66()
    {
        (Player caster, Player target) = Pair(39.0f);
        Assert.Equal(SpellCastResult.CastOk, SpellSystem.CheckRange(caster, Shot, target, strict: true));

        target.Relocate(40.0f, 0, 83.5f, 0, 0);
        Assert.Equal(SpellCastResult.OutOfRange, SpellSystem.CheckRange(caster, Shot, target, strict: true)); // 37 > 36.25

        Move(caster, MovementFlags.Forward);
        Assert.Equal(SpellCastResult.OutOfRange, SpellSystem.CheckRange(caster, Shot, target, strict: true)); // one runner is not enough

        Move(target, MovementFlags.Forward);
        Assert.Equal(SpellCastResult.CastOk, SpellSystem.CheckRange(caster, Shot, target, strict: true));    // 37 <= 36.25 + 2.66
        target.Relocate(42.0f, 0, 83.5f, 0, 0);
        Move(target, MovementFlags.Forward);
        Assert.Equal(SpellCastResult.OutOfRange, SpellSystem.CheckRange(caster, Shot, target, strict: true)); // 39 > 38.91
    }

    [Fact]
    public void TheLeewayAlsoWidensTheLandingCheck_AndCanBeSwitchedOff()
    {
        (Player caster, Player target) = Pair(46.0f);
        Move(caster, MovementFlags.Forward);
        Move(target, MovementFlags.Forward);

        // landing: 35 + 6.25 + 2.66 = 43.91 >= 43 (distance 46 - 3)
        Assert.Equal(SpellCastResult.CastOk, SpellSystem.CheckRange(caster, Shot, target, strict: false));
        Assert.Equal(SpellCastResult.OutOfRange, SpellSystem.CheckRange(caster, Shot, target, strict: false, movementLeeway: false)); // 41.25 < 43
    }

    [Fact]
    public void ACreatureCaster_UsesNoFixedAllowanceAtCastStart_OnlyTheLeeway()
    {
        Player player = TestWorld.CreatePlayer(2, 40.5f, 0, new FakeSession(2));
        var creature = new TestUnit(1, 0, 0, 83.5f);

        Assert.Equal(SpellCastResult.OutOfRange, SpellSystem.CheckRange(creature, Shot, player, strict: true)); // 37.5 > 35
        Move(creature, MovementFlags.Forward);
        Move(player, MovementFlags.Forward);
        Assert.Equal(SpellCastResult.CastOk, SpellSystem.CheckRange(creature, Shot, player, strict: true));     // 37.5 <= 35 + 2.66
    }

    [Theory]
    [InlineData(RangeLeewayMode.Retail, SpellCastResult.CastOk)]
    [InlineData(RangeLeewayMode.None, SpellCastResult.OutOfRange)]
    public void TheCastPipelineHonoursTheLeewayOption(RangeLeewayMode mode, SpellCastResult expected)
    {
        SpellInfo bolt = SpellTestKit.Spell(910100, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.UnitEnemy)) with
        {
            RangeIndex = 4,
            Range = new SpellRange(0, 35),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        using var kit = new SpellTestKit(bolt);
        kit.System.RangedOptions.Range.Leeway = mode;
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 41.0f); // combat distance 38: inside 35 + 1.25 + 2.66, outside 35 + 1.25
        Move(caster, MovementFlags.Forward);
        Move(target, MovementFlags.Forward);

        Assert.Equal(expected, kit.System.CastSpell(caster, 910100, SpellCastTargets.ForUnit(target.Guid), triggered: true));
    }

    // --- dead zone (characterization: already correct before this lane; kept as guards) -------

    [Fact]
    public void TheMinimumRangeIsMeasuredAfterBothCombatReaches()
    {
        (Player caster, Player target) = Pair(7.9f);
        Assert.Equal(SpellCastResult.TooClose, SpellSystem.CheckRange(caster, DeadZoneShot, target, strict: true));   // 4.9 < 5

        target.Relocate(8.1f, 0, 83.5f, 0, 0);
        Assert.Equal(SpellCastResult.CastOk, SpellSystem.CheckRange(caster, DeadZoneShot, target, strict: true));     // 5.1 >= 5
    }

    [Fact]
    public void MovingDoesNotShrinkTheDeadZone()
    {
        (Player caster, Player target) = Pair(7.9f);
        Move(caster, MovementFlags.Forward);
        Move(target, MovementFlags.Forward);

        Assert.Equal(SpellCastResult.TooClose, SpellSystem.CheckRange(caster, DeadZoneShot, target, strict: true));
    }
}
