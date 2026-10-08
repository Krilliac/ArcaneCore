namespace ArcaneCore.Game.Spells.Paladin;

/// <summary>vmangos SpellSpecific values the paladin rules use (SpellEntry.h:35-62); <see cref="None"/> for every other spell.</summary>
public enum PaladinSpellSpecific
{
    None,

    /// <summary>SPELL_SEAL: one per target, from any caster (IsSingleFromSpellSpecificPerTarget).</summary>
    Seal,

    /// <summary>SPELL_BLESSING: one per target per caster; one rank of a chain per target.</summary>
    Blessing,

    /// <summary>SPELL_AURA (the paladin auras): one per target per caster; one rank of a chain per target.</summary>
    Aura,

    /// <summary>SPELL_JUDGEMENT: one per target per caster; one rank of a chain per target.</summary>
    Judgement,
}

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
    public static PaladinSpellSpecific Specific(SpellInfo spell)
    {
        if (spell.SpellFamilyName != Family)
        {
            return PaladinSpellSpecific.None;
        }

        if (IsSeal(spell))
        {
            return PaladinSpellSpecific.Seal;
        }

        if ((spell.SpellFamilyFlags & BlessingMask) != 0)
        {
            return PaladinSpellSpecific.Blessing;
        }

        if ((spell.SpellFamilyFlags & JudgementMask) != 0 && spell.BaseLevel != 0)
        {
            return PaladinSpellSpecific.Judgement;
        }

        if (spell.SpellIconId == OldJudgementOfCommandIcon && spell.SpellVisual == OldJudgementOfCommandVisual)
        {
            return PaladinSpellSpecific.Judgement;
        }

        // "only paladin auras have this"
        return spell.HasEffect(SpellEffectName.ApplyAreaAuraParty) ? PaladinSpellSpecific.Aura : PaladinSpellSpecific.None;
    }

    /// <summary>
    /// vmangos Spells::CompareAuraRanks (SpellEntry.cpp:177-195): the base points of the first effect both spells share; negative when
    /// <paramref name="first"/> is the weaker (a negative effect compares the other way).
    /// </summary>
    public static int CompareAuraRanks(SpellInfo first, SpellInfo second)
    {
        if (first.Id == second.Id)
        {
            return 0;
        }

        for (int i = 0; i < SpellConstants.MaxEffects && i < first.Effects.Count && i < second.Effects.Count; i++)
        {
            SpellEffectInfo a = first.Effects[i];
            SpellEffectInfo b = second.Effects[i];
            if (a.Effect != SpellEffectName.None && a.Effect == b.Effect)
            {
                int diff = a.BasePoints - b.BasePoints;
                return first.SimpleValue(i) < 0 && second.SimpleValue(i) < 0 ? -diff : diff;
            }
        }

        return 0;
    }

    /// <summary>
    /// Whether two spells are ranks of one chain (vmangos SpellMgr::IsRankSpellDueToSpell, from SkillLineAbility.dbc). The spell system has no
    /// rank table, so the same family and the same name stand for the chain (every paladin blessing, aura, seal and judgement rank shares its
    /// chain's name; the greater blessings have their own).
    /// </summary>
    public static bool IsSameChain(SpellInfo first, SpellInfo second)
        => first.SpellFamilyName == second.SpellFamilyName && string.Equals(first.Name, second.Name, StringComparison.Ordinal);
}
