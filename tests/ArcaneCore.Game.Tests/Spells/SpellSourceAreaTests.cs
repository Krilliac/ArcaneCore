using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// The "AoE at source location" selectors centre on the spell's source (vmangos PUSH_SRC_CENTER, Spell.cpp:7968-7971 and
/// 8092-8095: <c>m_targets.m_srcX/Y/Z</c>), not on the caster: the source the client sent (SpellCastTargetsInfo.cpp:162-164),
/// or the casting object, which TARGET_LOCATION_CASTER_SRC writes there (Spell.cpp:2549-2555; for a trap, the trap object,
/// SpellCaster.cpp:2271-2273). The "within caster range" selector stays on the caster (PUSH_SELF_CENTER, Spell.cpp:2557-2558).
/// </summary>
public sealed class SpellSourceAreaTests
{
    private const uint EnemyAtSource = 900901;
    private const uint FriendAtSource = 900902;
    private const uint CasterSrcEnemy = 900903;
    private const uint WithinCasterRange = 900904;

    private static SpellTestKit Kit(out FakeRelations relations)
    {
        static SpellInfo Area(uint id, SpellEffectInfo effect) => Spell(id, effect) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 };
        var kit = new SpellTestKit(
            Area(EnemyAtSource, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc) with { Radius = 5 }),
            Area(FriendAtSource, Effect(SpellEffectName.Heal, 10, SpellImplicitTarget.EnumUnitsFriendAoeAtSrcLoc) with { Radius = 5 }),
            Area(CasterSrcEnemy, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.LocationCasterSrc,
                targetB: SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc) with { Radius = 5 }),
            Area(WithinCasterRange, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.EnumUnitsEnemyWithinCasterRange) with { Radius = 5 }));
        relations = new FakeRelations();
        kit.System.Relations = relations;
        return kit;
    }

    private static Player Enemy(SpellTestKit kit, FakeRelations relations, uint guid, float x)
    {
        (Player player, _) = kit.AddPlayer(guid, x, 0);
        relations.Hostile.Add(player.Guid);
        return player;
    }

    private static SpellCastTargets Source(float x, float y, float z)
        => new() { Mask = SpellCastTargetFlags.SourceLocation, Source = (x, y, z) };

    [Fact]
    public void EnemyAoeAtSource_CentresOnTheClientSource()
    {
        using SpellTestKit kit = Kit(out FakeRelations relations);
        (Player caster, _) = kit.AddPlayer(1);
        Player atSource = Enemy(kit, relations, 2, 20);
        Player nearCaster = Enemy(kit, relations, 3, 2);
        kit.World.RunTick(0);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, EnemyAtSource, Source(21, 0, caster.Z), triggered: true));

        Assert.Equal(50u, atSource.Health);
        Assert.Equal(60u, nearCaster.Health);
    }

    [Fact]
    public void FriendAoeAtSource_CentresOnTheClientSource()
    {
        using SpellTestKit kit = Kit(out _);
        (Player caster, _) = kit.AddPlayer(1);
        (Player atSource, _) = kit.AddPlayer(2, 20, 0);
        kit.World.RunTick(0);
        caster.Health = 30;
        atSource.Health = 30;

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, FriendAtSource, Source(21, 0, caster.Z), triggered: true));

        Assert.Equal(40u, atSource.Health);
        Assert.Equal(30u, caster.Health);
    }

    [Fact]
    public void EnemyAoeAtSource_WithoutASource_StaysOnTheCaster()
    {
        using SpellTestKit kit = Kit(out FakeRelations relations);
        (Player caster, _) = kit.AddPlayer(1);
        Player nearCaster = Enemy(kit, relations, 2, 2);
        kit.World.RunTick(0);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, EnemyAtSource, SpellCastTargets.ForSelf(), triggered: true));

        Assert.Equal(50u, nearCaster.Health);
    }

    [Fact]
    public void CasterSourceTarget_OverridesTheClientSource_WithTheCaster()
    {
        using SpellTestKit kit = Kit(out FakeRelations relations);
        (Player caster, _) = kit.AddPlayer(1);
        Player atSource = Enemy(kit, relations, 2, 20);
        Player nearCaster = Enemy(kit, relations, 3, 2);
        kit.World.RunTick(0);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, CasterSrcEnemy, Source(21, 0, caster.Z), triggered: true));

        Assert.Equal(60u, atSource.Health);
        Assert.Equal(50u, nearCaster.Health);
    }

    [Fact]
    public void WithinCasterRange_StaysOnTheCaster_WhateverTheSource()
    {
        using SpellTestKit kit = Kit(out FakeRelations relations);
        (Player caster, _) = kit.AddPlayer(1);
        Player atSource = Enemy(kit, relations, 2, 20);
        Player nearCaster = Enemy(kit, relations, 3, 2);
        kit.World.RunTick(0);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, WithinCasterRange, Source(21, 0, caster.Z), triggered: true));

        Assert.Equal(60u, atSource.Health);
        Assert.Equal(50u, nearCaster.Health);
    }

    [Fact]
    public void ObjectCast_CentresTheCasterSourceArea_OnTheObject()
    {
        using SpellTestKit kit = Kit(out FakeRelations relations);
        (Player owner, _) = kit.AddPlayer(1);
        Player atTrap = Enemy(kit, relations, 2, 20);
        Player nearOwner = Enemy(kit, relations, 3, 2);
        kit.World.RunTick(0);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastFromObject(owner, CasterSrcEnemy, atTrap, source: (21, 0, owner.Z)));

        Assert.Equal(50u, atTrap.Health);
        Assert.Equal(60u, nearOwner.Health);
    }
}
