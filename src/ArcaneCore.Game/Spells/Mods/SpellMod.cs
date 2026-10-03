namespace ArcaneCore.Game.Spells.Mods;

/// <summary>Flat or percent (vmangos SpellModType, SpellDefines.h: the values are the aura types 107 and 108).</summary>
public enum SpellModType
{
    /// <summary>SPELL_AURA_ADD_FLAT_MODIFIER (107).</summary>
    Flat = 107,

    /// <summary>SPELL_AURA_ADD_PCT_MODIFIER (108).</summary>
    Pct = 108,
}

/// <summary>
/// One live spell modifier a player holds (vmangos SpellModifier, SpellModifier.h:30-38): an operation, a flat or percent
/// value, the spell family and 64-bit class mask it applies to, and where it came from. It affects a spell when the spell
/// has the same family name as the mod's source spell and shares at least one bit of the mask (SpellModifier.cpp:34-41);
/// a zero mask affects nothing.
/// </summary>
public sealed class SpellMod
{
    internal SpellMod(SpellModOp op, SpellModType type, int value, ulong mask, uint familyName, uint spellId, int effectIndex)
    {
        Op = op;
        Type = type;
        Value = value;
        Mask = mask;
        FamilyName = familyName;
        SpellId = spellId;
        EffectIndex = effectIndex;
    }

    public SpellModOp Op { get; }

    public SpellModType Type { get; }

    /// <summary>The modifier amount (vmangos Modifier::m_amount): flat units, or percent points.</summary>
    public int Value { get; }

    /// <summary>The class mask (vmangos GetSpellAffectMask: the aura effect's EffectItemType, 64-bit).</summary>
    public ulong Mask { get; }

    /// <summary>SpellFamilyName of the spell that carries the aura (not of the spells it modifies).</summary>
    public uint FamilyName { get; }

    /// <summary>The spell that carries the aura.</summary>
    public uint SpellId { get; }

    public int EffectIndex { get; }

    /// <summary>vmangos SpellModifier::IsAffectedOnSpell (SpellModifier.cpp:34-41) with SpellEntry::IsFitToFamilyMask (SpellEntry.h:698-701).</summary>
    public bool IsAffectedOnSpell(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        return spell.SpellFamilyName == FamilyName && (spell.SpellFamilyFlags & Mask) != 0;
    }
}
