using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.SpellMods;

/// <summary>
/// Talents as data: a passive spell with aura 107 (ADD_FLAT_MODIFIER) or 108 (ADD_PCT_MODIFIER) changes the numbers the
/// spell system reads for the spells in its family mask, with no per-talent code (vmangos Aura::HandleAddModifier,
/// SpellAuras.cpp:1081-1111; Player::ApplySpellMod, Player.cpp:22417-22466). Every synthetic spell below is shaped like a
/// talent family, never a copy of retail data. These tests were written against the pre-engine tree, where they fail on the
/// asserted value (a talent-shaped passive changed nothing).
/// </summary>
public sealed class SpellModTalentShapeTests
{
    private const uint MageFamily = 3;
    private const uint OtherFamily = 8;

    private const uint FlatCastTime = 940001;   // Improved Fireball shape: flat -500 ms casting time on mask 0x1
    private const uint PctCrit = 940002;        // pct crit chance, mask 0x1
    private const uint OtherMask = 940003;      // flat cast time, mask 0x2 (a different spell of the family)
    private const uint OtherFamilyMod = 940004; // same mask as the bolt, wrong family
    private const uint ZeroMask = 940005;       // no class mask: affects nothing
    private const uint FlatCrit = 940006;
    private const uint NonPassiveCast = 940007; // the modified bolt-shaped spell family member

    private static SpellInfo ModSpell(uint id, uint family, ulong mask, AuraType aura, SpellModOp op, int value) =>
        Spell(id, Effect(SpellEffectName.ApplyAura, value, aura: aura, misc: (int)op) with { ItemType = (uint)mask }) with
        {
            Attributes = SpellAttributes.Passive,
            Duration = new SpellDuration(-1, 0, -1),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
            SpellFamilyName = family,
            SpellFamilyFlags = mask,
        };

    private static SpellTestKit Kit() => new(
        ModSpell(FlatCastTime, MageFamily, 1, AuraType.AddFlatModifier, SpellModOp.CastingTime, -500),
        ModSpell(PctCrit, MageFamily, 1, AuraType.AddPctModifier, SpellModOp.CriticalChance, 50),
        ModSpell(OtherMask, MageFamily, 2, AuraType.AddFlatModifier, SpellModOp.CastingTime, -500),
        ModSpell(OtherFamilyMod, OtherFamily, 1, AuraType.AddFlatModifier, SpellModOp.CastingTime, -500),
        ModSpell(ZeroMask, MageFamily, 0, AuraType.AddFlatModifier, SpellModOp.CastingTime, -500),
        ModSpell(FlatCrit, MageFamily, 1, AuraType.AddFlatModifier, SpellModOp.CriticalChance, 3),
        Spell(NonPassiveCast, Effect(SpellEffectName.SchoolDamage, 15, SpellImplicitTarget.UnitEnemy)) with
        {
            School = SpellSchool.Fire,
            CastTime = new SpellCastTime(2000, 0, 0),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            PowerType = (int)PowerType.Rage,
            ManaCost = 10,
            SpellFamilyName = MageFamily,
            SpellFamilyFlags = 1,
        });

    private static SpellInfo Bolt(SpellTestKit kit) => kit.Store.Get(NonPassiveCast)!;

    private static (Player Caster, Player Target) Duel(SpellTestKit kit)
    {
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 3, 0);
        kit.Spellbook.Teach(caster, NonPassiveCast);
        SpellSystem.SetPower(caster, PowerType.Rage, 100);
        kit.World.RunTick(0);
        return (caster, target);
    }

    private static int CastTimeOfBolt(SpellTestKit kit, Player caster, Player target)
    {
        kit.System.HandleCastRequest(caster, NonPassiveCast, SpellCastTargets.ForUnit(target.Guid));
        int castTime = kit.System.GetState(caster.Guid)!.CurrentCast!.CastTime;
        kit.System.Interrupt(kit.System.GetState(caster.Guid)!.CurrentCast!);
        kit.Advance(1500);
        return castTime;
    }

    [Fact]
    public void FlatCastTimePassive_ShortensTheCast_AndForgettingItRestoresIt()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target) = Duel(kit);
        Assert.Equal(2000, CastTimeOfBolt(kit, caster, target));

        Assert.True(kit.System.LearnSpell(caster, FlatCastTime));
        Assert.Equal(1500, CastTimeOfBolt(kit, caster, target));

        Assert.True(kit.System.RemoveSpell(caster, FlatCastTime));
        Assert.Equal(2000, CastTimeOfBolt(kit, caster, target));
    }

    [Fact]
    public void AModOnAnotherMaskOrFamilyOrWithNoMask_AffectsNothing()
    {
        using SpellTestKit kit = Kit();
        (Player caster, Player target) = Duel(kit);

        kit.System.LearnSpell(caster, OtherMask);
        kit.System.LearnSpell(caster, OtherFamilyMod);
        kit.System.LearnSpell(caster, ZeroMask);

        Assert.Equal(2000, CastTimeOfBolt(kit, caster, target));
    }

    [Fact]
    public void FlatAndPctCritPassives_ComposeThroughTheCritSeam()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = Duel(kit);
        SpellInfo bolt = Bolt(kit);
        Assert.Equal(5f, kit.System.SpellModifiers.Apply(caster, bolt, SpellModOp.CriticalChance, 5f));

        kit.System.LearnSpell(caster, FlatCrit);
        kit.System.LearnSpell(caster, PctCrit);

        // vmangos ApplySpellMod: base 5, flat +3, pct +50: 5 + ((5 + 3) * 50 / 100 + 3) = 12.
        Assert.Equal(12f, kit.System.SpellModifiers.Apply(caster, bolt, SpellModOp.CriticalChance, 5f));
    }
}
