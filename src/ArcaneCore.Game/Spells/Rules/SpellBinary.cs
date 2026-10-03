namespace ArcaneCore.Game.Spells.Rules;

/// <summary>
/// Binary spells: all-or-nothing, so their resistance lowers the hit chance instead of being a
/// partial resist (vmangos Nostalrius rule; the classification is SpellMgr.cpp:3342-3393 IsBinary).
/// </summary>
public static class SpellBinary
{
    /// <summary>vmangos SPELL_MIND_FLAY of C'Thun's eye tentacles (SpellMgr.cpp:3386-3387).</summary>
    private const uint CthunMindFlay = 26143;

    /// <summary>vmangos SPELL_GROUND_RUPTURE_NATURE of C'Thun's giant tentacles (SpellMgr.cpp:3388-3389).</summary>
    private const uint CthunGroundRupture = 26478;

    /// <summary>
    /// Damage class MAGIC, non-physical school, and an interrupt or knock back effect or one of the
    /// slow / fear / stun / pacify / root / silence / disarm / resistance / damage-taken auras; plus
    /// the two hardcoded C'Thun spells. Heals-over-time, polymorph and the other auras are not binary in vmangos.
    /// </summary>
    public static bool IsBinary(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.DamageClass != SpellDamageClass.Magic || spell.School == SpellSchool.Normal)
        {
            return false;
        }

        if (spell.Id is CthunMindFlay or CthunGroundRupture)
        {
            return true;
        }

        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.Effect is SpellEffectName.InterruptCast or SpellEffectName.KnockBack)
            {
                return true;
            }

            if (effect.Effect == SpellEffectName.ApplyAura && effect.AuraType is
                AuraType.ModDecreaseSpeed or AuraType.ModFear or AuraType.ModStun or AuraType.ModPacify or AuraType.ModRoot
                or AuraType.ModSilence or AuraType.ModDisarm or AuraType.ModResistance or AuraType.ModDamageTaken)
            {
                return true;
            }
        }

        return false;
    }
}
