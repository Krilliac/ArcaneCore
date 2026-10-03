namespace ArcaneCore.Game.Spells.Rules;

/// <summary>
/// Read-only views of <see cref="SpellInfo"/> the combat rules share: mechanic masks, school mask,
/// area-of-effect test and the raw attribute bits of <see cref="SpellRuleFlags"/>.
/// </summary>
public static class SpellInfoRuleExtensions
{
    public static uint SchoolMask(this SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return SpellSchoolMasks.Of(spell.School);
    }

    /// <summary>
    /// The spell's own mechanic plus every effect's mechanic (vmangos GetAllSpellMechanicMask,
    /// SpellEntry.h:720-729). An effect mechanic on an effect slot with no effect still counts, as in vmangos.
    /// </summary>
    public static uint AllMechanicMask(this SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        uint mask = SpellMechanics.Mask(spell.Mechanic);
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            mask |= SpellMechanics.Mask(effect.Mechanic);
        }

        return mask;
    }

    /// <summary>The mechanic of effect <paramref name="index"/>, falling back to the spell mechanic (vmangos SpellEntry.h:1210 GetEffectMechanic).</summary>
    public static uint EffectMechanic(this SpellInfo spell, int index)
    {
        ArgumentNullException.ThrowIfNull(spell);
        uint effect = spell.Effects[index].Mechanic;
        return effect != 0 ? effect : spell.Mechanic;
    }

    /// <summary>The spell mechanic plus the mechanic of effect <paramref name="index"/> (vmangos GetSpellMechanicMask for one effect, SpellEntry.h:1192-1208).</summary>
    public static uint EffectMechanicMask(this SpellInfo spell, int index)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return SpellMechanics.Mask(spell.Mechanic) | SpellMechanics.Mask(spell.Effects[index].Mechanic);
    }

    public static bool HasMechanic(this SpellInfo spell, SpellMechanic mechanic) =>
        (spell.AllMechanicMask() & SpellMechanics.Mask(mechanic)) != 0;

    /// <summary>
    /// vmangos IsAreaOfEffectSpell (SpellMgr.cpp:3262-3271 over SpellEntry.h:382-406 IsAreaEffectTarget):
    /// any target slot is an area target. Cone-54 and caster-range-36 are not area targets in vmangos.
    /// </summary>
    public static bool IsAreaEffect(this SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (IsAreaTarget(effect.TargetA) || IsAreaTarget(effect.TargetB))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAreaTarget(SpellImplicitTarget target) => target is
        SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc or SpellImplicitTarget.EnumUnitsEnemyAoeAtDestLoc
        or SpellImplicitTarget.EnumUnitsPartyWithinCasterRange or SpellImplicitTarget.EnumUnitsEnemyInCone24
        or SpellImplicitTarget.EnumUnitsFriendAoeAtSrcLoc or SpellImplicitTarget.EnumUnitsFriendAoeAtDestLoc
        or SpellImplicitTarget.EnumUnitsPartyAoeAtSrcLoc or SpellImplicitTarget.EnumUnitsPartyAoeAtDestLoc
        or SpellImplicitTarget.UnitFriendAndParty or SpellImplicitTarget.EnumUnitsRaidWithinCasterRange;

    public static bool IgnoresImmunities(this SpellInfo spell) => ((uint)spell.Attributes & SpellRuleFlags.NoImmunities) != 0;

    public static bool IsAlwaysHit(this SpellInfo spell) => (spell.AttributesEx3 & SpellRuleFlags.Ex3AlwaysHit) != 0;

    public static bool IgnoresResistances(this SpellInfo spell) => (spell.AttributesEx4 & SpellRuleFlags.Ex4IgnoreResistances) != 0;

    public static bool IsChanneled(this SpellInfo spell) => spell.HasAttribute(SpellAttributesEx.IsChanneled);

    /// <summary>True when any effect is a periodic damage or heal aura (the DoT/HoT shape).</summary>
    public static bool HasPeriodicDamageOrHeal(this SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.Effect == SpellEffectName.ApplyAura
                && effect.AuraType is AuraType.PeriodicDamage or AuraType.PeriodicHeal or AuraType.PeriodicLeech or AuraType.PeriodicDamagePercent)
            {
                return true;
            }
        }

        return false;
    }
}
