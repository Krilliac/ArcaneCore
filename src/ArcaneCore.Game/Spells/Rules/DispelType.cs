namespace ArcaneCore.Game.Spells.Rules;

/// <summary>Spell.dbc dispel type (vmangos SpellDefines.h:709-721 enum DispelType).</summary>
public enum DispelType : uint
{
    None = 0,
    Magic = 1,
    Curse = 2,
    Disease = 3,
    Poison = 4,
    Stealth = 5,
    Invisibility = 6,
    All = 7,
    SpecialNpcOnly = 8,
    Enrage = 9,
    ZgTicket = 10,
}

/// <summary>Dispel type bit-mask helpers.</summary>
public static class DispelTypes
{
    /// <summary>vmangos DISPEL_ALL_MASK: magic | curse | disease | poison.</summary>
    public const uint AllMask = (1u << (int)DispelType.Magic) | (1u << (int)DispelType.Curse) | (1u << (int)DispelType.Disease) | (1u << (int)DispelType.Poison);

    /// <summary>
    /// The mask of dispel types a dispel effect with the given misc value removes (vmangos
    /// SpellEntry::GetDispellMask, SpellEntry.h:460-467): ALL (7) expands to <see cref="AllMask"/>,
    /// any other type to its own bit. Types above 31 map to 0.
    /// </summary>
    public static uint GetDispelMask(uint dispelType) =>
        dispelType == (uint)DispelType.All ? AllMask : dispelType < 32 ? 1u << (int)dispelType : 0u;

    /// <inheritdoc cref="GetDispelMask(uint)"/>
    public static uint GetDispelMask(DispelType dispelType) => GetDispelMask((uint)dispelType);
}
