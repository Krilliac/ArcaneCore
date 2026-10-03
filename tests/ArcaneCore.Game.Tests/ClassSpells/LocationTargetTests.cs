using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

/// <summary>
/// Caster-relative location targets 41-44 and 47 and TARGET_UNIT_RAID_AND_CLASS (61), after vmangos
/// Spell.cpp:2940-2960 (RAID_AND_CLASS) and :2977-3022 (caster-relative destination); ids from
/// SpellDefines.h:96-116. The 2.0 yd totem placement is EffectSummonTotem's (SpellEffects.cpp:4952-4957), not this selector's.
/// </summary>
public sealed class LocationTargetTests
{
    private const uint FrontRight = 910041;
    private const uint BackRight = 910042;
    private const uint BackLeft = 910043;
    private const uint FrontLeft = 910044;
    private const uint Front = 910047;
    private const uint ZeroRadius = 910048;
    private const uint RaidAndClass = 910061;
    private const uint Unregistered = 910099;

    private const float Pi = MathF.PI;

    private static SpellImplicitTarget T(uint v) => (SpellImplicitTarget)v;

    private static SpellInfo LocationSpell(uint id, uint target, float radius)
        => Spell(id, Effect(SpellEffectName.SummonTotem, 5, T(target)) with { Radius = radius }) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    private static Fixture Build()
    {
        var kit = new SpellTestKit(
            LocationSpell(FrontRight, 41, 2),
            LocationSpell(BackRight, 42, 2),
            LocationSpell(BackLeft, 43, 2),
            LocationSpell(FrontLeft, 44, 2),
            LocationSpell(Front, 47, 2),
            LocationSpell(ZeroRadius, 44, 0),
            Spell(RaidAndClass, Effect(SpellEffectName.Heal, 20, T(61)) with { Radius = 30 }) with
            {
                RangeIndex = 4,
                Range = new SpellRange(0, 30),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            },
            Spell(Unregistered, Effect(SpellEffectName.SummonTotem, 5, T(62))) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 });
        var relations = new FakeRelations();
        var groups = new FakeGroups { Raid = true };
        kit.System.Relations = relations;
        kit.System.Groups = groups;
        var seen = new List<(Unit Target, float X, float Y, bool HasDest)>();
        kit.System.RegisterEffect(SpellEffectName.SummonTotem, ctx =>
            seen.Add((ctx.Target, ctx.Cast.Targets.Dest.X, ctx.Cast.Targets.Dest.Y, ctx.Cast.Targets.HasDest)));
        return new Fixture(kit, relations, groups, seen);
    }

    private sealed record Fixture(SpellTestKit Kit, FakeRelations Relations, FakeGroups Groups, List<(Unit Target, float X, float Y, bool HasDest)> Seen) : IDisposable
    {
        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void LocationFrontRight41_Summon_ReachesEffectHandler_WithCasterAsTarget_AndDestinationAtRadiusAngle1_75pi()
    {
        using Fixture f = Build();
        (Player caster, _) = f.Kit.AddPlayer(1, 100, 200);
        caster.Orientation = 0.5f;

        Assert.Equal(SpellCastResult.CastOk, f.Kit.System.CastSpell(caster, FrontRight, SpellCastTargets.ForSelf(), triggered: true));

        var hit = Assert.Single(f.Seen);
        Assert.Same(caster, hit.Target);
        Assert.True(hit.HasDest);
        float angle = 0.5f + (Pi * 1.75f);
        Assert.Equal(100 + (2 * MathF.Cos(angle)), hit.X, 3);
        Assert.Equal(200 + (2 * MathF.Sin(angle)), hit.Y, 3);
    }

    [Theory]
    [InlineData(FrontRight, 1.75f)]
    [InlineData(BackRight, 1.25f)]
    [InlineData(BackLeft, 0.75f)]
    [InlineData(FrontLeft, 0.25f)]
    [InlineData(Front, 0.0f)]
    public void AllAnglesMatchVmangos(uint spell, float piMultiple)
    {
        using Fixture f = Build();
        (Player caster, _) = f.Kit.AddPlayer(1, 0, 0);
        caster.Orientation = 0;

        f.Kit.System.CastSpell(caster, spell, SpellCastTargets.ForSelf(), triggered: true);

        var hit = Assert.Single(f.Seen);
        Assert.Equal(2 * MathF.Cos(Pi * piMultiple), hit.X, 3);
        Assert.Equal(2 * MathF.Sin(Pi * piMultiple), hit.Y, 3);
    }

    [Fact]
    public void ZeroRadiusIndex_PutsTheDestinationAtTheCaster()
    {
        using Fixture f = Build();
        (Player caster, _) = f.Kit.AddPlayer(1, 7, 9);

        f.Kit.System.CastSpell(caster, ZeroRadius, SpellCastTargets.ForSelf(), triggered: true);

        var hit = Assert.Single(f.Seen);
        Assert.Equal(7f, hit.X, 3);
        Assert.Equal(9f, hit.Y, 3);
    }

    [Fact]
    public void ClientSuppliedDestination_IsKept()
    {
        using Fixture f = Build();
        (Player caster, _) = f.Kit.AddPlayer(1, 0, 0);
        var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.DestLocation, Dest = (40, 50, 83.5f) };

        f.Kit.System.CastSpell(caster, FrontLeft, targets, triggered: true);

        var hit = Assert.Single(f.Seen);
        Assert.Equal(40f, hit.X, 3);
        Assert.Equal(50f, hit.Y, 3);
    }

    [Fact]
    public void RaidAndClass61_SelectsSameClassGroupMembersInRadius_ExcludesOtherClassAndHostileAndFar()
    {
        using Fixture f = Build();
        (Player paladin, _) = f.Kit.AddPlayer(1, 0, 0);
        (Player a, _) = f.Kit.AddPlayer(2, 3, 0);
        (Player otherClass, _) = f.Kit.AddPlayer(3, 4, 0);
        (Player far, _) = f.Kit.AddPlayer(4, 90, 0);
        (Player hostile, _) = f.Kit.AddPlayer(5, 5, 0);
        (Player outsider, _) = f.Kit.AddPlayer(6, 6, 0);
        otherClass.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Priest);
        f.Relations.Hostile.Add(hostile.Guid);
        a.Health = 10;
        otherClass.Health = 10;
        far.Health = 10;
        hostile.Health = 10;
        outsider.Health = 10;
        f.Groups.Parties.Add([paladin.Guid, a.Guid, otherClass.Guid, far.Guid, hostile.Guid]);

        Assert.Equal(SpellCastResult.CastOk, f.Kit.System.CastSpell(paladin, RaidAndClass, SpellCastTargets.ForUnit(a.Guid), triggered: true));

        Assert.Equal(30u, a.Health);
        Assert.Equal(10u, otherClass.Health);
        Assert.Equal(10u, far.Health);
        Assert.Equal(10u, hostile.Health);
        Assert.Equal(10u, outsider.Health);
    }

    [Fact]
    public void RaidAndClass61_UngroupedTarget_HitsOnlyTheExplicitTarget()
    {
        using Fixture f = Build();
        (Player paladin, _) = f.Kit.AddPlayer(1, 0, 0);
        (Player a, _) = f.Kit.AddPlayer(2, 3, 0);
        (Player bystander, _) = f.Kit.AddPlayer(3, 4, 0);
        a.Health = 10;
        bystander.Health = 10;

        f.Kit.System.CastSpell(paladin, RaidAndClass, SpellCastTargets.ForUnit(a.Guid), triggered: true);

        Assert.Equal(30u, a.Health);
        Assert.Equal(10u, bystander.Health);
    }

    [Fact]
    public void UnregisteredTarget_StillReportsUnsupported_AndNeverRunsTheEffect()
    {
        using Fixture f = Build();
        (Player caster, _) = f.Kit.AddPlayer(1, 0, 0);

        f.Kit.System.CastSpell(caster, Unregistered, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Empty(f.Seen);
    }

    [Fact]
    public void Registry_DuplicateRegistration_Throws()
    {
        using Fixture f = Build();

        Assert.Throws<InvalidOperationException>(() => f.Kit.System.RegisterTargetSelector((SpellImplicitTarget)41, (_, _, _, _) => [], locationOnly: true));
    }

    [Fact]
    public void Registry_LocationOnlyFlag_IsReportedPerTarget()
    {
        using Fixture f = Build();
        f.Kit.System.RegisterTargetSelector((SpellImplicitTarget)77, (_, cast, _, _) => [(cast.Caster, 1.0f)], locationOnly: true);

        Assert.True(f.Kit.System.IsRegisteredLocationTarget((SpellImplicitTarget)77));
        Assert.False(f.Kit.System.IsRegisteredLocationTarget((SpellImplicitTarget)61));
        Assert.True(f.Kit.System.IsRegisteredLocationTarget((SpellImplicitTarget)41));
    }
}
