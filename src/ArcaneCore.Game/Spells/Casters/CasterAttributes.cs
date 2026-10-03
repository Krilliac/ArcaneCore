namespace ArcaneCore.Game.Spells.Casters;

/// <summary>
/// Spell attribute bits the caster rules read that the shared attribute enums do not name yet
/// (kept here so the shared <c>SpellDefines.cs</c> stays untouched).
/// </summary>
public static class CasterAttributes
{
    /// <summary>SPELL_ATTR_EX2_DONT_BLOCK_MANA_REGEN (vmangos SpellDefines.h:931): paying for the spell does not start the five second rule.</summary>
    public const uint Ex2DontBlockManaRegen = 0x02000000;

    /// <summary>SPELL_ATTR_SCALES_WITH_CREATURE_LEVEL (vmangos SpellDefines.h:849, <c>Attributes</c>): the cost is divided by a spell level / caster level ratio.</summary>
    public const uint ScalesWithCreatureLevel = 0x00080000;

    /// <summary>SPELL_ATTR_EX3_IGNORE_CASTER_MODIFIERS (vmangos SpellDefines.h:978).</summary>
    public const uint Ex3IgnoreCasterModifiers = 0x20000000;

    /// <summary>SPELL_ATTR_EX4_IGNORE_DAMAGE_TAKEN_MODIFIERS (vmangos SpellDefines.h:994).</summary>
    public const uint Ex4IgnoreDamageTakenModifiers = 0x00000100;
}
