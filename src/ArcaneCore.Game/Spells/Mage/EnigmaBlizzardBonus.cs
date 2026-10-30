using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Mage;

/// <summary>
/// Enigma Blizzard Bonus 26127, the Enigma Vestments 3-piece bonus (ItemSet.dbc row 503): "Your Blizzard spell has a 30% chance to be
/// uninterruptible." This behaviour is inferred, not verified against a retail capture: no reference core (vmangos, cmangos, mangoszero) reads the
/// script id and no public source says what the bonus blocks or when the 30% is rolled.
/// <para>
/// Evidence: Spell.dbc 5875 gives 26127 one ApplyAura effect, aura OverrideClassScripts (112), misc 4235, base value 100, mage family with the class
/// mask (EffectItemType) 0x80, which is Blizzard's family bit (vmangos SpellClassMask.h CF_MAGE_BLIZZARD = 7, "CM0 00000080"; every Blizzard rank
/// carries family flags 0x80080). The tooltip is the sentence above. Ranks are therefore resolved by the aura's class mask against the spell's
/// family flags, the way the other OVERRIDE_CLASS_SCRIPTS consumers do (<c>SpellBonusModule</c>), not by a list of rank ids.
/// </para>
/// <para>
/// Decisions: the 30% is rolled once, when the channel starts, with the spell system's random (one draw, only for a channel of a spell the aura's mask
/// selects while the caster holds the aura, so a seeded sequence is untouched otherwise). A successful roll sets <see cref="SpellCast.Uninterruptible"/>
/// for the whole channel. That flag stops what the code base calls an interrupt of a cast in progress: the INTERRUPT_CAST effect (Kick, Counterspell,
/// Earth Shock: it neither ends the channel nor locks the school) and damage pushback, including the damage-cancel channel flags. It does not stop
/// the other ways a channel ends: the caster moving or turning, dying, being stunned, silenced, feared or polymorphed, a cancel request, or losing the
/// target. Those are not interrupts of the spell in the sense of the tooltip, and the code base routes them through separate paths.
/// </para>
/// Thread affinity: runs on the map thread that runs the caster's spell system. No state is kept.
/// </summary>
internal static class EnigmaBlizzardBonus
{
    /// <summary>The OVERRIDE_CLASS_SCRIPTS misc value of 26127 (Spell.dbc EffectMiscValue).</summary>
    public const int ScriptMiscValue = 4235;

    /// <summary>The chance in percent, from the tooltip ("30% chance"); the DBC base value 100 is not the chance.</summary>
    public const int ChancePercent = 30;

    /// <summary>
    /// Whether the channel of <paramref name="cast"/> that is starting is uninterruptible. Draws one number from the spell system's random when, and only
    /// when, the caster holds the bonus aura and the spell is selected by its class mask.
    /// </summary>
    public static bool RollAtChannelStart(SpellSystem system, SpellCast cast)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(cast);
        SpellInfo spell = cast.Spell;
        if (!spell.IsChanneled || spell.SpellFamilyFlags == 0 || !HoldsBonusFor(system, cast.Caster, spell))
        {
            return false;
        }

        return system.Random.Next(100) < ChancePercent;
    }

    private static bool HoldsBonusFor(SpellSystem system, Unit caster, SpellInfo spell)
    {
        foreach (SpellAuraHolder holder in system.GetAuras(caster))
        {
            if (holder.IsRemoved || holder.Spell.SpellFamilyName != spell.SpellFamilyName)
            {
                continue;
            }

            foreach (SpellAura? aura in holder.Auras)
            {
                if (aura is { Type: AuraType.OverrideClassScripts, MiscValue: ScriptMiscValue }
                    && (system.AffectMask(holder.Spell, aura.EffectIndex) & spell.SpellFamilyFlags) != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
