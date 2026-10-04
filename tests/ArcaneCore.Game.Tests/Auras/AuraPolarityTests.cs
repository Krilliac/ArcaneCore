using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Auras;

/// <summary>
/// Aura polarity is decided per effect (vmangos SpellEntry::IsPositiveEffect, SpellEntry.cpp:808-1007) and a holder is
/// positive only when all of its AURA effects are (SpellAuras.cpp:7475-7482). Spell shapes are copied from Spell.dbc
/// (1.12.1 client patch-2.MPQ): Stealth rank 1 = effect 0 ModShapeshift -1, effect 1 ModStealth +4, effect 2 ModDecreaseSpeed -51,
/// all self targeted, Attributes 0x1A150010.
/// </summary>
public sealed class AuraPolarityTests
{
    private const uint Stealth = 940001;
    private const uint SliceAndDice = 940002;
    private const uint Hellfire = 940003;

    private static SpellInfo StealthShape(uint id = Stealth) => Spell(
        id,
        Effect(SpellEffectName.ApplyAura, -1, SpellImplicitTarget.UnitCaster, AuraType.ModShapeshift),
        Effect(SpellEffectName.ApplyAura, 4, SpellImplicitTarget.UnitCaster, AuraType.ModStealth),
        Effect(SpellEffectName.ApplyAura, -51, SpellImplicitTarget.UnitCaster, AuraType.ModDecreaseSpeed)) with
    {
        Attributes = (SpellAttributes)0x1A150010,
        Duration = new SpellDuration(-1, 0, -1),
        SpellVisual = 1,
        SpellFamilyName = 8,
    };

    [Fact]
    public void Stealth_FromDbcShape_IsAPositiveSpell_UsesABuffSlot_AndIsCancelable()
    {
        using var kit = new SpellTestKit(StealthShape());
        (Player rogue, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(rogue, Stealth);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(rogue, Stealth, SpellCastTargets.ForSelf()));

        SpellAuraHolder holder = kit.System.GetAuras(rogue).Single();
        Assert.True(holder.IsPositive);
        Assert.True(holder.Slot < 32, $"slot {holder.Slot}");
        Assert.Equal(1u, rogue.GetUInt32(UpdateFields.UnitFieldAuraflags) & 0x1); // CANCELABLE bit of slot 0
    }

    [Fact]
    public void Stealth_CanBeCancelled_ByCmsgCancelAura()
    {
        using var kit = new SpellTestKit(StealthShape());
        (Player rogue, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(rogue, Stealth);
        kit.System.HandleCastRequest(rogue, Stealth, SpellCastTargets.ForSelf());
        Assert.Single(kit.System.GetAuras(rogue));

        kit.System.CancelAura(rogue, Stealth);

        Assert.Empty(kit.System.GetAuras(rogue));
    }

    [Fact]
    public void SpeedEffectOnlyMakesTheDebuffWhenTheSpellIsMarkedDebuff_OrIsGenericSelfCast()
    {
        // Non-generic family, self target, not a debuff: the speed effect alone is positive (SpellEntry.cpp:937-947).
        SpellInfo benign = Spell(1, Effect(SpellEffectName.ApplyAura, -30, SpellImplicitTarget.UnitCaster, AuraType.ModDecreaseSpeed)) with { SpellFamilyName = 7 };
        SpellInfo generic = benign with { SpellFamilyName = 0 };
        SpellInfo debuff = benign with { Attributes = SpellAttributes.AuraIsDebuff };

        Assert.True(benign.IsPositiveSpell());
        Assert.False(generic.IsPositiveSpell());
        Assert.False(debuff.IsPositiveSpell());
    }

    [Fact]
    public void SliceAndDice_HolderIsPositive_EvenThoughSpellHasAnEnemyTargetedEffect()
    {
        // Effect 0 is the haste aura on the caster; effect 1 is a non-aura effect aimed at an enemy, so the SPELL is
        // negative while the HOLDER (aura effects only) is positive and takes a buff slot.
        SpellInfo spell = Spell(
            SliceAndDice,
            Effect(SpellEffectName.ApplyAura, 30, SpellImplicitTarget.UnitCaster, AuraType.ModMeleeHaste),
            Effect(SpellEffectName.Dummy, 0, SpellImplicitTarget.UnitEnemy)) with
        {
            Duration = new SpellDuration(9000, 0, 9000),
            SpellVisual = 1,
        };
        Assert.False(spell.IsPositiveSpell());

        using var kit = new SpellTestKit(spell);
        (Player rogue, _) = kit.AddPlayer(1);
        kit.Spellbook.Teach(rogue, SliceAndDice);
        kit.System.HandleCastRequest(rogue, SliceAndDice, SpellCastTargets.ForSelf());

        SpellAuraHolder holder = kit.System.GetAuras(rogue).Single();
        Assert.True(holder.IsPositive);
        Assert.True(holder.Slot < 32);
        Assert.True(holder.Auras[0]!.IsPositive);
    }

    [Fact]
    public void HolderPolarity_IsTheAndOfItsAuraEffects()
    {
        SpellInfo mixed = Spell(
            1,
            Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitCaster, AuraType.ModStat, misc: 0),
            Effect(SpellEffectName.ApplyAura, -5, SpellImplicitTarget.UnitCaster, AuraType.ModStat, misc: 1));
        Assert.True(mixed.IsPositiveEffect(0));
        Assert.False(mixed.IsPositiveEffect(1));
        Assert.False(mixed.IsPositiveSpell());
    }

    [Fact]
    public void Hellfire_IsPositive_DespiteDamagingTheCaster()
    {
        SpellInfo hellfire = Spell(Hellfire, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitCaster, AuraType.PeriodicDamage)) with
        {
            SpellFamilyName = 5,
            SpellFamilyFlags = 1UL << 6,
            SpellIconId = 937,
            SpellVisual = 5423,
        };

        Assert.True(hellfire.IsPositiveSpell());
        Assert.False((hellfire with { SpellVisual = 1 }).IsPositiveSpell());
    }

    public static TheoryData<string, SpellInfo, bool> Vmangos() => new()
    {
        { "AURA_IS_DEBUFF attribute", Spell(2, Effect(SpellEffectName.ApplyAura, 1, aura: AuraType.ModStat)) with { Attributes = SpellAttributes.AuraIsDebuff }, false },
        { "Heal effect always positive", Spell(3, Effect(SpellEffectName.Heal, 10, SpellImplicitTarget.UnitEnemy)), true },
        { "School damage at enemy", Spell(4, Effect(SpellEffectName.SchoolDamage, 10, SpellImplicitTarget.UnitEnemy)), false },
        { "Mod stat negative amount", Spell(5, Effect(SpellEffectName.ApplyAura, -10, aura: AuraType.ModStat)), false },
        { "Mod damage taken negative amount is positive", Spell(6, Effect(SpellEffectName.ApplyAura, -10, aura: AuraType.ModDamageTaken)), true },
        { "Mod damage taken positive amount is by target (self)", Spell(7, Effect(SpellEffectName.ApplyAura, 10, aura: AuraType.ModDamageTaken)), true },
        { "Mod damage taken positive at enemy", Spell(8, Effect(SpellEffectName.ApplyAura, 10, SpellImplicitTarget.UnitEnemy, AuraType.ModDamageTaken)), false },
        { "Mod damage percent done positive at enemy", Spell(9, Effect(SpellEffectName.ApplyAura, 10, SpellImplicitTarget.UnitEnemy, AuraType.ModDamagePercentDone)), true },
        { "Increase health at enemy", Spell(10, Effect(SpellEffectName.ApplyAura, 10, SpellImplicitTarget.UnitEnemy, AuraType.ModIncreaseHealth)), true },
        { "Single stun aura", Spell(11, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModStun)), false },
        { "Stun with a second effect defers to targets", Spell(12, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModStun), Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitCaster, AuraType.ModResistance)), true },
        { "Root", Spell(13, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModRoot)), false },
        { "Silence", Spell(14, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModSilence)), false },
        { "Bat Costume silence", Spell(24732, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModSilence)), true },
        { "Wisp Costume pacify silence", Spell(24740, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModPacifySilence)), true },
        { "Periodic leech", Spell(15, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitCaster, AuraType.PeriodicLeech)), false },
        { "Periodic damage at caster", Spell(16, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitCaster, AuraType.PeriodicDamage)), false },
        { "Periodic damage at party", Spell(17, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitParty, AuraType.PeriodicDamage)), true },
        { "Mechanic immunity bandage", Spell(18, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.MechanicImmunity, misc: 16)), false },
        { "Mechanic immunity shield", Spell(19, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.MechanicImmunity, misc: 19)), false },
        { "Mechanic immunity mount", Spell(20, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.MechanicImmunity, misc: 21)), false },
        { "Mechanic immunity invulnerability", Spell(21, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.MechanicImmunity, misc: 25)), false },
        { "Mechanic immunity fear", Spell(22, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.MechanicImmunity, misc: 5)), true },
        { "Spell mod cost increase", Spell(23, Effect(SpellEffectName.ApplyAura, 20, SpellImplicitTarget.UnitCaster, AuraType.AddPctModifier, misc: 14)), false },
        { "Spell mod cost reduction", Spell(24, Effect(SpellEffectName.ApplyAura, -20, SpellImplicitTarget.UnitCaster, AuraType.AddPctModifier, misc: 14)), true },
        { "Ghost", Spell(25, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Ghost)), false },
        { "Mutate Bug scale", Spell(802, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModScale)), true },
        { "Scale at enemy", Spell(26, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModScale)), false },
        { "Caster source with friend area target B", Spell(27, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.LocationCasterSrc, AuraType.ModResistance, targetB: SpellImplicitTarget.EnumUnitsFriendAoeAtSrcLoc)), true },
        { "Caster source with enemy area target B", Spell(28, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.LocationCasterSrc, AuraType.ModResistance, targetB: SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc)), false },
        { "Custom negative flag", Spell(29, Effect(SpellEffectName.ApplyAura, 5, aura: AuraType.ModStat)) with { CustomFlags = SpellInfo.CustomNegative }, false },
        { "Custom positive flag", Spell(30, Effect(SpellEffectName.ApplyAura, -5, aura: AuraType.ModStat)) with { CustomFlags = SpellInfo.CustomPositive }, true },
        { "Instakill at caster (suicide)", Spell(31, Effect(SpellEffectName.Instakill, 0, SpellImplicitTarget.UnitCaster)), true },
        { "Instakill at enemy", Spell(32, Effect(SpellEffectName.Instakill, 0, SpellImplicitTarget.UnitEnemy)), false },
        { "Dummy aura Net-o-Matic", Spell(13139, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)), false },
    };

    [Theory]
    [MemberData(nameof(Vmangos))]
    public void IsPositiveSpell_MatchesTheVmangosRules(string why, SpellInfo spell, bool expected)
    {
        Assert.True(expected == spell.IsPositiveSpell(), why);
    }

    [Fact]
    public void PeriodicTriggerAtASelfTargetedSpell_IsNegative_WhenTheTriggeredSpellHasANegativeEffectAtAPositiveTarget()
    {
        SpellInfo triggered = Spell(950, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.ModRoot));
        SpellInfo parent = Spell(951, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.PeriodicTriggerSpell, 1000, trigger: 950));

        Assert.True(parent.IsPositiveSpell());
        Assert.False(parent.IsPositiveSpell(id => id == 950 ? triggered : null));
    }

    [Fact]
    public void CancelAura_IgnoresNegativeNoCancelAndPassiveSpells_AndForeignAreaAuras()
    {
        SpellInfo debuff = Spell(960, Effect(SpellEffectName.ApplyAura, -5, SpellImplicitTarget.UnitCaster, AuraType.ModStat)) with
        {
            Duration = new SpellDuration(30000, 0, 30000),
            SpellVisual = 1,
        };
        SpellInfo noCancel = Spell(961, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitCaster, AuraType.ModStat)) with
        {
            Attributes = SpellAttributes.NoAuraCancel,
            Duration = new SpellDuration(30000, 0, 30000),
            SpellVisual = 1,
        };
        SpellInfo hiddenIcon = Spell(962, Effect(SpellEffectName.ApplyAura, 5, SpellImplicitTarget.UnitCaster, AuraType.ModStat)) with
        {
            AttributesEx = SpellAttributesEx.NoAuraIcon,
            Duration = new SpellDuration(30000, 0, 30000),
            SpellVisual = 1,
        };
        SpellInfo withIcon = hiddenIcon with { Id = 963, ActiveIconId = 5 };
        using var kit = new SpellTestKit(debuff, noCancel, hiddenIcon, withIcon);
        (Player player, _) = kit.AddPlayer(1);
        foreach (uint id in new uint[] { 960, 961, 962, 963 })
        {
            kit.System.CastSpell(player, id, SpellCastTargets.ForSelf(), triggered: true);
            kit.System.CancelAura(player, id);
        }

        Assert.Equal([960u, 961u, 962u], kit.System.GetAuras(player).Select(h => h.Spell.Id).Order().ToArray());
    }
}
