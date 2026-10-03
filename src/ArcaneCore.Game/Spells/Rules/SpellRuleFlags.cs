namespace ArcaneCore.Game.Spells.Rules;

/// <summary>
/// Spell attribute bits the rules read that the typed <see cref="SpellAttributes"/> enums do not carry,
/// as raw masks over the Spell.dbc Attributes / AttributesEx..Ex4 columns (vmangos SpellDefines.h
/// :858-994, cited per constant). Typed accessors live in <see cref="SpellInfoRuleExtensions"/>.
/// </summary>
public static class SpellRuleFlags
{
    /// <summary>Attributes: SPELL_ATTR_NO_IMMUNITIES (SpellDefines.h:859).</summary>
    public const uint NoImmunities = 0x20000000;

    /// <summary>Attributes: SPELL_ATTR_HEARTBEAT_RESIST (SpellDefines.h:860).</summary>
    public const uint HeartbeatResist = 0x40000000;

    /// <summary>AttributesEx: SPELL_ATTR_EX_NO_REFLECTION (SpellDefines.h:877).</summary>
    public const uint ExNoReflection = 0x00000080;

    /// <summary>AttributesEx: SPELL_ATTR_EX_IMMUNITY_PURGES_EFFECT (SpellDefines.h:885).</summary>
    public const uint ExImmunityPurgesEffect = 0x00008000;

    /// <summary>AttributesEx: SPELL_ATTR_EX_IMMUNITY_TO_HOSTILE_AND_FRIENDLY_EFFECTS (SpellDefines.h:886).</summary>
    public const uint ExImmunityToHostileAndFriendly = 0x00010000;

    /// <summary>AttributesEx2: SPELL_ATTR_EX2_NO_SCHOOL_IMMUNITIES (SpellDefines.h:932).</summary>
    public const uint Ex2NoSchoolImmunities = 0x04000000;

    /// <summary>AttributesEx3: SPELL_ATTR_EX3_COMPLETELY_BLOCKED (SpellDefines.h:946).</summary>
    public const uint Ex3CompletelyBlocked = 0x00000008;

    /// <summary>AttributesEx3: SPELL_ATTR_EX3_ALWAYS_HIT (SpellDefines.h:965).</summary>
    public const uint Ex3AlwaysHit = 0x00040000;

    /// <summary>AttributesEx3: SPELL_ATTR_EX3_IGNORE_CASTER_MODIFIERS (SpellDefines.h:978).</summary>
    public const uint Ex3IgnoreCasterModifiers = 0x20000000;

    /// <summary>AttributesEx4: SPELL_ATTR_EX4_IGNORE_RESISTANCES (SpellDefines.h:986; absent from 1.12 DBCs, set by DB spell mods).</summary>
    public const uint Ex4IgnoreResistances = 0x00000001;

    /// <summary>AttributesEx4: SPELL_ATTR_EX4_IGNORE_DAMAGE_TAKEN_MODIFIERS (SpellDefines.h:994).</summary>
    public const uint Ex4IgnoreDamageTakenModifiers = 0x00000100;

    /// <summary>AttributesEx: SPELL_ATTR_EX_IGNORE_CASTER_AND_TARGET_RESTRICTIONS (SpellDefines.h:893; moved to Ex3 after 1.10).</summary>
    public const uint ExIgnoreCasterAndTargetRestrictions = 0x00800000;

    /// <summary>AttributesEx3: SPELL_ATTR_EX3_IGNORE_CASTER_AND_TARGET_RESTRICTIONS (SpellDefines.h:976).</summary>
    public const uint Ex3IgnoreCasterAndTargetRestrictions = 0x10000000;
}
