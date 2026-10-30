using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Spells;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>
/// The vmangos <c>spell_effect_mod</c> corrections as <see cref="SpellStoreFactory"/> applies them (vmangos SpellModMgr::LoadSpellMods,
/// src/game/Spells/SpellModMgr.cpp:195-262; -1 keeps a value, :48-70). Only an effect slot the spell data leaves empty takes a correction:
/// the world database's own spell data is the source of record, as classic-db's rows are for the spell DB scripts.
/// </summary>
public sealed class SpellEffectModTests
{
    private const uint MothersMilk = 16468;
    private const uint WebExplosion = 15474;

    /// <summary>
    /// Mother's Milk as Spell.dbc / classic-db <c>spell_template</c> has it: one effect, APPLY_AURA DUMMY at the caster's area (targets 22/15,
    /// radius index 13), effects 2 and 3 empty.
    /// </summary>
    private static SpellTemplateRow MothersMilkRow() => new()
    {
        Id = MothersMilk, SpellName = "Mother's Milk", DurationIndex = 21, RangeIndex = 7,
        Effect1 = 6, EffectDieSides1 = 1, EffectBaseDice1 = 1, EffectImplicitTargetA1 = 22, EffectImplicitTargetB1 = 15, EffectRadiusIndex1 = 13,
        EffectApplyAuraName1 = 4,
    };

    /// <summary>The vmangos row for effect index 1: a periodic trigger of Web Explosion every 11999 ms on the aura's holder.</summary>
    private static SpellEffectModRow MothersMilkMod() => new()
    {
        Id = MothersMilk, EffectIndex = 1, Effect = 6, EffectApplyAuraName = 23, EffectMechanic = 0, EffectImplicitTargetA = 6, EffectImplicitTargetB = 0,
        EffectRadiusIndex = 0, EffectItemType = 0, EffectMiscValue = 1, EffectTriggerSpell = (int)WebExplosion, EffectDieSides = 1, EffectBaseDice = 1,
        EffectBasePoints = 0, EffectAmplitude = 11999, EffectChainTarget = 0, EffectDicePerLevel = 0, EffectRealPointsPerLevel = 0,
        EffectPointsPerComboPoint = 0, EffectMultipleValue = 0,
    };

    private static SpellInfo Build(SpellTemplateRow row, params SpellEffectModRow[] mods)
    {
        var content = new SpellContent([row], [], [], [], [new SpellRadiusRow { Id = 13, Radius = 10f }], [], []) { EffectMods = mods };
        return SpellStoreFactory.Build(content).Get(row.Id)!;
    }

    [Fact]
    public void MothersMilk_TheModFillsTheEmptySecondEffect_WithAPeriodicTriggerOfWebExplosion()
    {
        SpellInfo spell = Build(MothersMilkRow(), MothersMilkMod());

        SpellEffectInfo added = spell.Effects[1];
        Assert.Equal(SpellEffectName.ApplyAura, added.Effect);
        Assert.Equal(AuraType.PeriodicTriggerSpell, added.AuraType);
        Assert.Equal(11999u, added.Amplitude);
        Assert.Equal(WebExplosion, added.TriggerSpell);
        Assert.Equal(SpellImplicitTarget.UnitEnemy, added.TargetA);
        Assert.Equal((1, 1, 0, 1), (added.DieSides, added.BaseDice, added.BasePoints, added.MiscValue));

        // The spell's own effect is untouched, its radius resolved as before.
        Assert.Equal((AuraType.Dummy, 10f), (spell.Effects[0].AuraType, spell.Effects[0].Radius));
        Assert.True(spell.Effects[2].IsEmpty);
    }

    [Fact]
    public void WithoutTheMod_MothersMilkHasOneEffect()
        => Assert.True(Build(MothersMilkRow()).Effects[1].IsEmpty);

    [Fact]
    public void AModOfAnEffectTheSpellDefines_IsNotApplied()
    {
        SpellEffectModRow overwrite = MothersMilkMod();
        overwrite.EffectIndex = 0;

        SpellInfo spell = Build(MothersMilkRow(), overwrite);

        Assert.Equal((AuraType.Dummy, SpellImplicitTarget.LocationCasterSrc, 0u), (spell.Effects[0].AuraType, spell.Effects[0].TargetA, spell.Effects[0].TriggerSpell));
    }

    [Fact]
    public void MinusOne_KeepsTheValue_AndARadiusIndexResolvesThroughSpellRadius()
    {
        // Every column but Effect, the aura and the radius index left at -1: the empty slot keeps its zeroes.
        var mod = new SpellEffectModRow { Id = MothersMilk, EffectIndex = 2, Effect = 6, EffectApplyAuraName = 4, EffectRadiusIndex = 13 };

        SpellEffectInfo added = Build(MothersMilkRow(), mod).Effects[2];

        Assert.Equal((SpellEffectName.ApplyAura, AuraType.Dummy, 10f), (added.Effect, added.AuraType, added.Radius));
        Assert.Equal((0, 0, 0u, 0u, 0), (added.DieSides, added.BasePoints, added.Amplitude, added.TriggerSpell, added.MiscValue));
    }

    [Fact]
    public void AModOfASpellTheDataLacks_IsIgnored()
    {
        var content = new SpellContent([MothersMilkRow()], [], [], [], [], [], []) { EffectMods = [new SpellEffectModRow { Id = 1, EffectIndex = 0, Effect = 6 }] };

        SpellStore store = SpellStoreFactory.Build(content);

        Assert.Null(store.Get(1));
    }
}
