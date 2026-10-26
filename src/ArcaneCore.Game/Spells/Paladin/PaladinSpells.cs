namespace ArcaneCore.Game.Spells.Paladin;

/// <summary>The paladin spell family facts of vmangos (SpellClassMask.h:274-311, SpellEntry::IsSealSpell, Spells::GetSpellSpecific).</summary>
public static class PaladinSpells
{
    /// <summary>SPELLFAMILY_PALADIN.</summary>
    public const uint Family = 10;

    /// <summary>CF_PALADIN_SEAL_OF_THE_CRUSADER, CF_PALADIN_SEAL_OF_COMMAND, CF_PALADIN_SEALS (bits 9, 25, 27).</summary>
    public const ulong SealMask = (1UL << 9) | (1UL << 25) | (1UL << 27);

    /// <summary>The SPELL_BLESSING family mask (SpellEntry.cpp:105).</summary>
    public const ulong BlessingMask = 0x0000000010000100;

    /// <summary>The SPELL_JUDGEMENT family mask (SpellEntry.cpp:108): JoR, JoW/JoL, JoJ, JotC; a base level is required.</summary>
    public const ulong JudgementMask = 0x0000000020180400;

    /// <summary>Judgement (20271), CF_PALADIN_JUDGEMENT bit 23.</summary>
    public const uint Judgement = 20271;

    /// <summary>The old Judgement of Command (SpellEntry.cpp:111-113).</summary>
    public const uint OldJudgementOfCommandIcon = 561;

    /// <summary>The old Judgement of Command's visual.</summary>
    public const uint OldJudgementOfCommandVisual = 5652;

    /// <summary>vmangos SpellEntry::IsSealSpell: "Collection of all the seal family flags. No other paladin spell has any of those."</summary>
    public static bool IsSeal(SpellInfo spell) => spell.SpellFamilyName == Family && (spell.SpellFamilyFlags & SealMask) != 0;

    /// <summary>vmangos Spells::GetSpellSpecific, the paladin branch (SpellEntry.cpp:100-122).</summary>
    public static SpellSpecific Specific(SpellInfo spell)
    {
        if (spell.SpellFamilyName != Family)
        {
            return SpellSpecific.Normal;
        }

        if (IsSeal(spell))
        {
            return SpellSpecific.Seal;
        }

        if ((spell.SpellFamilyFlags & BlessingMask) != 0)
        {
            return SpellSpecific.Blessing;
        }

        if ((spell.SpellFamilyFlags & JudgementMask) != 0 && spell.BaseLevel != 0)
        {
            return SpellSpecific.Judgement;
        }

        if (spell.SpellIconId == OldJudgementOfCommandIcon && spell.SpellVisual == OldJudgementOfCommandVisual)
        {
            return SpellSpecific.Judgement;
        }

        // "only paladin auras have this"
        return spell.HasEffect(SpellEffectName.ApplyAreaAuraParty) ? SpellSpecific.Aura : SpellSpecific.Normal;
    }
}
