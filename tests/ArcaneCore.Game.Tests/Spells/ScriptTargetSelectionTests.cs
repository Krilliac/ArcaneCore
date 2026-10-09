using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Pets;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// spell_script_target driving implicit targets 38 (TARGET_UNIT_SCRIPT_NEAR_CASTER, vmangos Spell::CheckScriptTargeting) and 7
/// (TARGET_ENUM_UNITS_SCRIPT_AOE_AT_SRC_LOC, vmangos Spell::SetTargetMap). Synthetic spells; the ClassicDB users are 8283 (38) and 10252/10258 (7).
/// </summary>
public sealed class ScriptTargetSelectionTests
{
    private const uint NearestSpell = 990100;
    private const uint AreaSpell = 990101;
    private const uint UnlistedAreaSpell = 990102;

    private static SpellInfo Marker(uint id, SpellImplicitTarget targetA, SpellImplicitTarget targetB = SpellImplicitTarget.None, float radius = 0)
        => Spell(id, Effect(SpellEffectName.ApplyAura, 1, targetA, AuraType.Dummy, targetB: targetB) with { Radius = radius }) with
        {
            Range = new SpellRange(0, 30),
            Duration = new SpellDuration(10_000, 0, 10_000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static PetTestKit Kit(params SpellStore.ScriptTarget[] rows)
    {
        var kit = new PetTestKit(
        [
            Marker(NearestSpell, SpellImplicitTarget.UnitScriptNearCaster),
            Marker(AreaSpell, SpellImplicitTarget.LocationCasterSrc, SpellImplicitTarget.EnumUnitsScriptAoeAtSrcLoc, radius: 20),
            Marker(UnlistedAreaSpell, SpellImplicitTarget.LocationCasterSrc, SpellImplicitTarget.EnumUnitsScriptAoeAtSrcLoc, radius: 20),
        ]);
        kit.Spells.System.Store = new SpellStore(kit.Spells.Store.All, [], [], rows);
        return kit;
    }

    private static Creature Spawn(PetTestKit kit, uint entry, float x, Player near)
        => kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(entry)!, x, 0, near.Z, 0);

    [Fact]
    public void ScriptNearCaster_TakesOnlyTheNearestListedUnit_AndPrefersAListedExplicitTarget()
    {
        using PetTestKit kit = Kit(new SpellStore.ScriptTarget(NearestSpell, 1, PetTestKit.WildEntry, 0));
        (Player player, _) = kit.AddPlayer(1);
        Creature unlisted = Spawn(kit, PetTestKit.GuardianEntry, 2, player);
        Creature near = Spawn(kit, PetTestKit.WildEntry, 5, player);
        Creature far = Spawn(kit, PetTestKit.WildEntry, 10, player);

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(player, NearestSpell));
        Assert.True(kit.Spells.System.HasAura(near, NearestSpell));
        Assert.False(kit.Spells.System.HasAura(far, NearestSpell));
        Assert.False(kit.Spells.System.HasAura(unlisted, NearestSpell));

        kit.Spells.System.RemoveAuras(near, NearestSpell);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(player, NearestSpell, SpellCastTargets.ForUnit(far.Guid)));
        Assert.True(kit.Spells.System.HasAura(far, NearestSpell));
        Assert.False(kit.Spells.System.HasAura(near, NearestSpell));
    }

    [Fact]
    public void ScriptAoeAtSource_KeepsListedEntriesInTheRadius_AndHonoursTheInverseEffectMask()
    {
        using PetTestKit kit = Kit(
            new SpellStore.ScriptTarget(AreaSpell, 1, PetTestKit.WildEntry, 0),
            new SpellStore.ScriptTarget(AreaSpell, 1, PetTestKit.GuardianEntry, 1)); // effect 0 may not hit the guardian entry
        (Player player, _) = kit.AddPlayer(1);
        Creature excluded = Spawn(kit, PetTestKit.GuardianEntry, 3, player);
        Creature a = Spawn(kit, PetTestKit.WildEntry, 5, player);
        Creature b = Spawn(kit, PetTestKit.WildEntry, 15, player);
        Creature outside = Spawn(kit, PetTestKit.WildEntry, 30, player);

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(player, AreaSpell));
        Assert.True(kit.Spells.System.HasAura(a, AreaSpell));
        Assert.True(kit.Spells.System.HasAura(b, AreaSpell));
        Assert.False(kit.Spells.System.HasAura(excluded, AreaSpell));
        Assert.False(kit.Spells.System.HasAura(outside, AreaSpell));
        Assert.False(kit.Spells.System.HasAura(player, AreaSpell));
    }

    [Fact]
    public void ScriptAoeAtSource_WithoutRows_TakesEveryLivingUnitButTheCaster()
    {
        using PetTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        Creature guardian = Spawn(kit, PetTestKit.GuardianEntry, 3, player);
        Creature wild = Spawn(kit, PetTestKit.WildEntry, 5, player);

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(player, UnlistedAreaSpell));
        Assert.True(kit.Spells.System.HasAura(guardian, UnlistedAreaSpell));
        Assert.True(kit.Spells.System.HasAura(wild, UnlistedAreaSpell));
        Assert.False(kit.Spells.System.HasAura(player, UnlistedAreaSpell));
    }
}
