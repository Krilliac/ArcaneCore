namespace ArcaneCore.Game.Spells;

/// <summary>
/// vmangos <c>SpellSpecific</c> (SpellDefines.h:1313-1340): the kind of a spell that the aura stacking rules of
/// <c>Unit::RemoveNoStackAurasDueToAuraHolder</c> (Unit.cpp:3355-3573) key on. <see cref="Normal"/> is every other spell. The elixir values
/// are kept for the predicates (SpellEntry.h:142-180) but <see cref="SpellSpecifics.Of"/> never returns them: vmangos reads them from the
/// <c>spell_elixir</c> table (SpellMgr.h:412-424), which this server does not load.
/// </summary>
public enum SpellSpecific
{
    Normal = 0,
    Seal = 1,
    Blessing = 2,
    Aura = 3,
    Sting = 4,
    Curse = 5,
    Aspect = 6,
    Tracker = 7,
    WarlockArmor = 8,
    MageArmor = 9,
    ElementalShield = 10,
    MagePolymorph = 11,
    PositiveShout = 12,
    Judgement = 13,
    BattleElixir = 14,
    GuardianElixir = 15,
    FlaskElixir = 16,
    WellFed = 19,
    Food = 20,
    Drink = 21,
    FoodAndDrink = 22,
    NegativeHaste = 23,
    Snare = 24,
}

/// <summary>
/// The spell specific classification of vmangos (<c>Spells::GetSpellSpecific</c>, SpellEntry.cpp:14-175) and the three predicates that turn two
/// classifications into a stacking rule (SpellEntry.h:107-180), plus the rank helpers the rule uses (<c>Spells::CompareAuraRanks</c>,
/// <c>CompareSpellSpecificAuras</c>, SpellEntry.cpp:177-220). Pure functions over an immutable <see cref="SpellInfo"/>: thread safe.
/// </summary>
public static class SpellSpecifics
{
    private const uint FamilyGeneric = 0;
    private const uint FamilyMage = 3;
    private const uint FamilyWarrior = 4;
    private const uint FamilyWarlock = 5;
    private const uint FamilyPriest = 6;
    private const uint FamilyHunter = 9;
    private const uint FamilyPaladin = 10;
    private const uint FamilyShaman = 11;

    /// <summary>SPELLFAMILY_POTION (SharedDefines.h): elixirs, answered by the spell_elixir table, which is not loaded.</summary>
    private const uint FamilyPotion = 13;

    /// <summary>Family flags 18 (Molten), 25 (Frost / Ice) and 28 (Mage Armor) (SpellEntry.cpp:69-71).</summary>
    private const ulong MageArmorMask = 0x12000000;

    /// <summary>The warrior Battle / Commanding shout family flags (SpellEntry.cpp:80).</summary>
    private const ulong PositiveShoutMask = 0x0000008000010000;

    /// <summary>CF_SHAMAN_LIGHTNING_SHIELD, bit 10 (SpellClassMask.h:323).</summary>
    private const ulong LightningShieldMask = 1UL << 10;

    /// <summary>The proc shield of the tier 2 eight piece bonus (SpellEntry.h:906).</summary>
    private const uint ElementalShieldBonusSpell = 23552;

    /// <summary>DISPEL_CURSE and DISPEL_POISON (SpellDefines.h:721-723).</summary>
    private const uint DispelCurse = 2;

    private const uint DispelPoison = 4;

    /// <summary>The active icon every hunter aspect carries, and Auto Shot, which shares it (SpellEntry.cpp:107-109).</summary>
    private const uint AspectActiveIcon = 122;

    private const uint AutoShot = 75;

    /// <summary>Warlock armor and skin visual and icon (SpellEntry.cpp:148-150).</summary>
    private const uint WarlockArmorVisual = 130;

    private const uint WarlockArmorIcon = 89;

    /// <summary>The Blessed Sunfruit family icons (SpellEntry.cpp:97).</summary>
    private const uint SunfruitIconA = 52;

    private const uint SunfruitIconB = 79;

    /// <summary>The Aspect of the Beast spell (SpellEntry.cpp:25).</summary>
    private const uint AspectOfTheBeast = 13161;

    /// <summary>SPELL_PREVENTION_TYPE_SILENCE (SpellDefines.h).</summary>
    private const uint PreventionSilence = 1;

    /// <summary>vmangos <c>Spells::GetSpellSpecific</c> (SpellEntry.cpp:14-175), the same branch order.</summary>
    public static SpellSpecific Of(SpellInfo spell)
    {
        ArgumentNullException.ThrowIfNull(spell);
        switch (spell.SpellFamilyName)
        {
            case FamilyGeneric:
                if (spell.Id == AspectOfTheBeast)
                {
                    return SpellSpecific.Aspect;
                }

                if ((spell.AuraInterruptFlags & SpellAuraInterruptFlags.StandingCancels) != 0)
                {
                    // Food / drinks (mostly): the regeneration auras of the three effects decide.
                    bool food = false;
                    bool drink = false;
                    foreach (SpellEffectInfo effect in spell.Effects)
                    {
                        switch (effect.AuraType)
                        {
                            case AuraType.ModRegen:
                            case AuraType.ObsModHealth:
                                food = true;
                                break;
                            case AuraType.ModPowerRegen:
                            case AuraType.ObsModMana:
                                drink = true;
                                break;
                        }
                    }

                    if (food && drink)
                    {
                        return SpellSpecific.FoodAndDrink;
                    }

                    if (food)
                    {
                        return SpellSpecific.Food;
                    }

                    if (drink)
                    {
                        return SpellSpecific.Drink;
                    }
                }
                else if (spell.HasAttribute(SpellAttributesEx2.RetainItemCast))
                {
                    return SpellSpecific.WellFed; // exclusive with the food and drink effects, or Well Fed would remove them
                }

                break;

            case FamilyMage:
                if ((spell.SpellFamilyFlags & MageArmorMask) != 0)
                {
                    return SpellSpecific.MageArmor;
                }

                if (spell.Effects[0].AuraType == AuraType.ModConfuse && spell.PreventionType == PreventionSilence)
                {
                    return SpellSpecific.MagePolymorph;
                }

                break;

            case FamilyWarrior:
                if ((spell.SpellFamilyFlags & PositiveShoutMask) != 0)
                {
                    return SpellSpecific.PositiveShout;
                }

                break;

            case FamilyWarlock:
                if (spell.Dispel == DispelCurse)
                {
                    return SpellSpecific.Curse; // only warlock curses have this
                }

                break;

            case FamilyPriest:
                // "Well Fed" from Blessed Sunfruit, Blessed Sunfruit Juice and Alterac Spring Water.
                if (spell.HasAttribute(SpellAttributes.AllowWhileSitting) && (spell.InterruptFlags & SpellInterruptFlags.Combat) != 0
                    && (spell.SpellIconId == SunfruitIconA || spell.SpellIconId == SunfruitIconB))
                {
                    return SpellSpecific.WellFed;
                }

                break;

            case FamilyHunter:
                if (spell.Dispel == DispelPoison)
                {
                    return SpellSpecific.Sting; // only hunter stings have this
                }

                // Only hunter aspects have this (one has the generic family), Auto Shot excluded.
                if (spell.ActiveIconId == AspectActiveIcon && spell.Id != AutoShot)
                {
                    return SpellSpecific.Aspect;
                }

                break;

            case FamilyPaladin:
                if (Paladin.PaladinSpells.Specific(spell) is var paladin && paladin != SpellSpecific.Normal)
                {
                    return paladin;
                }

                break;

            case FamilyShaman:
                if ((spell.SpellFamilyFlags & LightningShieldMask) != 0 || spell.Id == ElementalShieldBonusSpell)
                {
                    return SpellSpecific.ElementalShield;
                }

                break;

            case FamilyPotion:
                return SpellSpecific.Normal; // GetSpellElixirSpecific: the spell_elixir table is not loaded

            default:
                break;
        }

        // Only warlock armor and skin have this (in addition to the family cases).
        if (spell.SpellVisual == WarlockArmorVisual && spell.SpellIconId == WarlockArmorIcon)
        {
            return SpellSpecific.WarlockArmor;
        }

        if (spell.IsTracker)
        {
            return SpellSpecific.Tracker; // SpellEntry.cpp:152-157
        }

        // Attack speed reduction.
        if (HasSingleAura(spell, AuraType.ModMeleeHaste))
        {
            foreach (SpellEffectInfo effect in spell.Effects)
            {
                if (effect.AuraType == AuraType.ModMeleeHaste && effect.BasePoints < 0)
                {
                    return SpellSpecific.NegativeHaste;
                }
            }
        }

        // Movement speed reduction; dazes are not affected.
        if (HasSingleAura(spell, AuraType.ModDecreaseSpeed) && !spell.HasAttribute(SpellAttributesEx.PreventsAnim))
        {
            return SpellSpecific.Snare;
        }

        return SpellSpecific.Normal;
    }

    /// <summary>vmangos <c>SpellEntry::HasSingleAura</c> (SpellEntry.h:859-868): the aura on some effect, and no other aura effect.</summary>
    private static bool HasSingleAura(SpellInfo spell, AuraType aura)
    {
        bool has = false;
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            if (effect.AuraType == aura)
            {
                has = true;
            }
            else if (effect.Effect == SpellEffectName.ApplyAura)
            {
                return false;
            }
        }

        return has;
    }

    /// <summary>
    /// <c>Spells::IsSingleFromSpellSpecificPerTargetPerCaster</c> (SpellEntry.h:109-124): a target holds one of the kind from one caster.
    /// </summary>
    public static bool IsSinglePerTargetPerCaster(SpellSpecific first, SpellSpecific second)
        => first is SpellSpecific.Blessing or SpellSpecific.Aura or SpellSpecific.Sting or SpellSpecific.Curse or SpellSpecific.Aspect
            or SpellSpecific.PositiveShout or SpellSpecific.Judgement
        && first == second;

    /// <summary>
    /// <c>Spells::IsSingleFromSpellSpecificSpellRanksPerTarget</c> (SpellEntry.h:127-140): a target holds one rank of a spell of the kind, whoever
    /// the casters are.
    /// </summary>
    public static bool IsSingleRanksPerTarget(SpellSpecific first, SpellSpecific second)
        => first is SpellSpecific.Blessing or SpellSpecific.Aura or SpellSpecific.Curse or SpellSpecific.Aspect or SpellSpecific.Judgement
        && first == second;

    /// <summary><c>Spells::IsSingleFromSpellSpecificPerTarget</c> (SpellEntry.h:143-180): a target holds one of the kind from any caster.</summary>
    public static bool IsSinglePerTarget(SpellSpecific first, SpellSpecific second) => first switch
    {
        SpellSpecific.Seal or SpellSpecific.Tracker or SpellSpecific.WarlockArmor or SpellSpecific.MageArmor or SpellSpecific.ElementalShield
            or SpellSpecific.MagePolymorph or SpellSpecific.WellFed or SpellSpecific.NegativeHaste or SpellSpecific.Snare => first == second,
        SpellSpecific.BattleElixir => second is SpellSpecific.BattleElixir or SpellSpecific.FlaskElixir,
        SpellSpecific.GuardianElixir => second is SpellSpecific.GuardianElixir or SpellSpecific.FlaskElixir,
        SpellSpecific.FlaskElixir => second is SpellSpecific.BattleElixir or SpellSpecific.GuardianElixir or SpellSpecific.FlaskElixir,
        SpellSpecific.Food => second is SpellSpecific.Food or SpellSpecific.FoodAndDrink,
        SpellSpecific.Drink => second is SpellSpecific.Drink or SpellSpecific.FoodAndDrink,
        SpellSpecific.FoodAndDrink => second is SpellSpecific.Food or SpellSpecific.Drink or SpellSpecific.FoodAndDrink,
        _ => false,
    };

    /// <summary>
    /// vmangos <c>Spells::CompareAuraRanks</c> (SpellEntry.cpp:177-195): the base points of the first effect both spells share; negative when
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
    /// vmangos <c>Spells::CompareSpellSpecificAuras</c> (SpellEntry.cpp:197-220): whether <paramref name="first"/>, a snare or an attack speed
    /// reduction, is at least as strong as <paramref name="second"/> (a weaker one never replaces it).
    /// </summary>
    public static bool IsAtLeastAsStrong(SpellInfo first, SpellInfo second)
    {
        foreach (SpellEffectInfo a in first.Effects)
        {
            if (a.Effect != SpellEffectName.ApplyAura)
            {
                continue;
            }

            foreach (SpellEffectInfo b in second.Effects)
            {
                if (a.AuraType != b.AuraType)
                {
                    continue;
                }

                if (a.BasePoints < b.BasePoints && b.BasePoints < 0)
                {
                    return true;
                }

                if (a.BasePoints > b.BasePoints && b.BasePoints >= 0)
                {
                    return true;
                }

                if (a.BasePoints == b.BasePoints && first.Duration.Base >= second.Duration.Base)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether two spells are ranks of one chain (vmangos SpellMgr::IsRankSpellDueToSpell, from SkillLineAbility.dbc and a generic check on the
    /// family, flags, icon and visual). The spell system has no rank table, so the same family and the same name stand for the chain (every
    /// paladin blessing, aura, seal and judgement, hunter aspect and sting, mage armor, warlock curse and armor rank shares its chain's name; the
    /// greater blessings have their own).
    /// </summary>
    public static bool IsSameChain(SpellInfo first, SpellInfo second)
        => first.SpellFamilyName == second.SpellFamilyName && string.Equals(first.Name, second.Name, StringComparison.Ordinal);
}
