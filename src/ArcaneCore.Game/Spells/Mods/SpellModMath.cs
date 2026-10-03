namespace ArcaneCore.Game.Spells.Mods;

/// <summary>
/// The spell-modifier formula, after vmangos <c>Player::ApplySpellMod</c> (Player.cpp:22417-22466). It only reads the mods it is
/// given, except that with a consuming <see cref="SpellModScope"/> it spends charges (DropModCharge).
/// </summary>
public static class SpellModMath
{
    /// <summary>vmangos SPELL_ATTR_EX3_IGNORE_CASTER_MODIFIERS (SpellDefines.h:978): such a spell is never modified.</summary>
    private const uint Ex3IgnoreCasterModifiers = 0x20000000;

    /// <summary>
    /// Apply the mods of one operation to <paramref name="value"/>: flat mods add up, percent mods add up, and
    /// <c>result = value + ((value + flat) * pct / 100 + flat)</c> (flat first, then the percent of the flat-adjusted value),
    /// in single precision. The caller truncates toward zero for an integer base (vmangos <c>T(float(base) + diff)</c>).
    /// <list type="bullet">
    /// <item>A percent mod is skipped when the base is 0 (Player.cpp:22431-22434, "most important for spell mods with charges").</item>
    /// <item>CASTING_TIME with a base of 10 s or more skips a percent mod of -100 or less (the instant-cast setting, :22436-22438).</item>
    /// <item>A CASTING_TIME percent mod of exactly -100 forces -100% and discards the flat total, then stops reading further mods
    /// (Barkskin plus Nature's Swiftness, :22455-22460).</item>
    /// <item>A spell with ATTRIBUTES_EX3 IGNORE_CASTER_MODIFIERS is returned unchanged (:22420).</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <paramref name="scope"/> is the consuming cast, or null to only read: with a scope, a mod that has no charge left (-1) applies
    /// only if the scope already holds it (vmangos IsAffectedBySpellmod, Player.cpp:17617-17633), and every mod that contributed
    /// spends one charge (DropModCharge, :17765-17783) unless it is a flat CASTING_TIME mod and an instant-cast percent mod covers
    /// the spell and <paramref name="instantKeepsFlatCastTimeCharge"/> is set (Patch 1.11, :22444-22453).
    /// </remarks>
    /// <param name="mods">The player's mods of <paramref name="op"/>, in the order they were added.</param>
    public static float Evaluate(IReadOnlyList<SpellMod> mods, SpellInfo spell, SpellModOp op, float value,
        SpellModScope? scope = null, bool instantKeepsFlatCastTimeCharge = true)
    {
        ArgumentNullException.ThrowIfNull(mods);
        ArgumentNullException.ThrowIfNull(spell);
        if ((spell.AttributesEx3 & Ex3IgnoreCasterModifiers) != 0)
        {
            return value;
        }

        float totalPct = 0;
        float totalFlat = 0;
        foreach (SpellMod mod in mods)
        {
            if (scope is not null && mod.Charges == -1 && !scope.HasModifierApplied(mod))
            {
                continue;
            }

            if (!mod.IsAffectedOnSpell(spell))
            {
                continue;
            }

            if (mod.Type == SpellModType.Flat)
            {
                totalFlat += mod.Value;
            }
            else
            {
                if (value == 0)
                {
                    continue;
                }

                if (op == SpellModOp.CastingTime && value >= 10 * 1000 && mod.Value <= -100)
                {
                    continue;
                }

                totalPct += mod.Value;
            }

            if (scope is not null
                && !(op == SpellModOp.CastingTime && mod.Type == SpellModType.Flat && instantKeepsFlatCastTimeCharge && HasInstantCastingMod(mods, spell)))
            {
                DropCharge(mod, scope);
            }

            if (op == SpellModOp.CastingTime && mod.Type == SpellModType.Pct && mod.Value == -100)
            {
                totalPct = -100;
                totalFlat = 0;
                break;
            }
        }

        float diff = (value + totalFlat) * totalPct / 100.0f + totalFlat;
        return value + diff;
    }

    /// <summary>vmangos Player::HasInstantCastingSpellMod (Player.cpp:17607-17615): a percent CASTING_TIME mod of -100 or less that covers the spell.</summary>
    internal static bool HasInstantCastingMod(IReadOnlyList<SpellMod> castTimeMods, SpellInfo spell)
    {
        foreach (SpellMod mod in castTimeMods)
        {
            if (mod.Type == SpellModType.Pct && mod.Value <= -100 && mod.IsAffectedOnSpell(spell))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>vmangos Player::DropModCharge (Player.cpp:17765-17783): spend one charge and remember the mod on the cast.</summary>
    private static void DropCharge(SpellMod mod, SpellModScope scope)
    {
        if (scope.HasModifierApplied(mod) || mod.Charges <= 0)
        {
            return;
        }

        mod.Charges--;
        if (mod.Charges == 0)
        {
            mod.Charges = -1;
        }

        scope.Add(mod);
    }
}
