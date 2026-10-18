namespace ArcaneCore.Game.Spells;

/// <summary>
/// Which auras are exclusive (vmangos <c>_IsExclusiveSpellAura</c>, SpellAuras.cpp:8177-8335, the Nostalrius buff stacking rule): of the
/// exclusive auras of one type, misc value, item type and sign on a unit only the strongest is applied (<c>Aura::CheckExclusiveWith</c>,
/// :8346-8371). Two Stamina buffs of different spells, two Agility elixirs, two movement slows: the weaker one waits in its holder and comes back
/// when the stronger one ends (<c>ExclusiveAuraCanApply</c> / <c>ExclusiveAuraUnapply</c>, :8373-8434; SpellSystem.ExclusiveAuras.cs).
/// Like vmangos this gates the aura's handler only: readers that sum the auras of a type themselves still see every one.
/// </summary>
public static class ExclusiveAuras
{
    private const uint FamilyMage = 3;
    private const uint FamilyWarrior = 4;
    private const uint FamilyWarlock = 5;
    private const uint FamilyPriest = 6;
    private const uint FamilyHunter = 9;
    private const uint FamilyPaladin = 10;
    private const uint FamilyShaman = 11;

    /// <summary>SPELL_ATTR_EX2_RETAIN_ITEM_CAST (SpellDefines.h): food and drink buffs such as Well Fed.</summary>
    private const uint AttrEx2RetainItemCast = 0x80000000;

    /// <summary>Elixir of the Mongoose: "should stack with EVERYTHING", except its agility (effect 0).</summary>
    private const uint ElixirOfTheMongoose = 17538;

    /// <summary>The spells that stack with everything (SpellAuras.cpp:8189-8248): Blasted Lands buffs, alcohol, world buffs, item procs.</summary>
    private static readonly HashSet<uint> s_stackWithEverything =
    [
        10693, 10691, 10668, 10671, 10667, 10670, 10669, 10672, 10692, 10690, 24382, // Blasted Lands item buffs, Spirit of Zanza
        25804, 20875, 25722, 25037, 22789, 22790, 6114, 5020, 5021, // alcohols
        23179, 20007, 20572, 17038, 16329, 25891, 18264, 12022, 22817, // Taint of Shadow, Holy Strength, Blood Fury, Firewater, Juju Might, ...
        19506, 20905, 20906, 18262, 24932, 24907, // Trueshot Aura, Call Bloodshot, Leader of the Pack, Moonkin Aura
        22888, 15366, 22820, 17628, 22730, 18141, 18125, 18192, 18191, 25661, 24427, 17528, 23697, // world buffs and consumables
        27664, 27665, 27666, 27669, 27670, 27671, // Love is in the Air gifts of friendship
    ];

    /// <summary>
    /// Whether effect <paramref name="effectIndex"/> of <paramref name="spell"/>, an aura of <paramref name="type"/>, is exclusive. A passive
    /// spell's auras never are (vmangos <c>Aura::ComputeExclusive</c>, SpellAuras.cpp:8337-8343).
    /// </summary>
    public static bool IsExclusive(SpellInfo spell, int effectIndex, AuraType type)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (spell.IsPassive || effectIndex < 0 || effectIndex >= spell.Effects.Count)
        {
            return false;
        }

        SpellEffectInfo effect = spell.Effects[effectIndex];
        if (effect.Effect == SpellEffectName.ApplyAreaAuraRaid || s_stackWithEverything.Contains(spell.Id))
        {
            return false;
        }

        if (spell.Id == ElixirOfTheMongoose)
        {
            return effectIndex == 0;
        }

        ulong flags = spell.SpellFamilyFlags;
        bool familyException = spell.SpellFamilyName switch
        {
            FamilyWarlock => (flags & (1UL << 23)) != 0, // CF_WARLOCK_IMP_BUFFS: Blood Pact
            FamilyShaman => (flags & (1UL << 16)) != 0, // CF_SHAMAN_STRENGTH_OF_EARTH
            FamilyWarrior => (flags & (1UL << 16)) != 0, // CF_WARRIOR_BATTLE_SHOUT
            FamilyPaladin => (flags & ((1UL << 1) | (1UL << 28))) != 0, // CF_PALADIN_BLESSING_OF_MIGHT, CF_PALADIN_BLESSINGS
            FamilyHunter => (flags & (1UL << 20)) != 0, // CF_HUNTER_ASPECT_OF_THE_HAWK
            _ => false, // druid Hurricane is exclusive since 1.8.0
        };
        if (familyException || ((uint)spell.AttributesEx2 & AttrEx2RetainItemCast) != 0)
        {
            return false; // ... and food
        }

        switch (type)
        {
            case AuraType.ModHealingDone:
            case AuraType.ModDamageDone:
            case AuraType.ModAttackPowerPct:
            case AuraType.ModRangedAttackPowerPct:
            case AuraType.ModAttackPower:
            case AuraType.ModRangedAttackPower:
            case AuraType.ModPowerRegen:
            case AuraType.ModDamagePercentTaken:
            case AuraType.ModStat:
            case AuraType.WaterBreathing:
            case AuraType.ModSpellCritChance:
                return true;
            case AuraType.ModAttackerSpellCritChance:
                return spell.SpellFamilyName != FamilyMage; // Winter's Chill / Improved Scorch stack
            case AuraType.ModHealingPct:
                return effect.BasePoints >= 0; // the healing taken debuffs (Mortal Strike, Wound Poison, Aimed Shot) are not exclusive
            case AuraType.ModResistancePct:
                return spell.SpellFamilyName is not (FamilyShaman or FamilyPriest); // Ancestral Healing / Inspiration
            case AuraType.ModMeleeHaste:
            case AuraType.ModDecreaseSpeed:
                return effect.BasePoints < 0; // attack and movement speed reductions only
            default:
                return false;
        }
    }

    /// <summary>
    /// vmangos <c>Aura::CheckExclusiveWith</c> (SpellAuras.cpp:8346-8371): 0 = not the same kind, 1 = <paramref name="mine"/> is at least as
    /// important (it applies), 2 = <paramref name="other"/> is more important.
    /// </summary>
    internal static int Compare(SpellAuraHolder myHolder, SpellAura mine, SpellAuraHolder otherHolder, SpellAura other)
    {
        if (other.Type != mine.Type || other.MiscValue != mine.MiscValue
            || ItemType(otherHolder.Spell, other.EffectIndex) != ItemType(myHolder.Spell, mine.EffectIndex)
            || (other.Amount > 0) != (mine.Amount > 0))
        {
            return 0;
        }

        if (other.Amount > mine.Amount && mine.Amount >= 0)
        {
            return 2;
        }

        return other.Amount < mine.Amount && mine.Amount < 0 ? 2 : 1;
    }

    private static uint ItemType(SpellInfo spell, int index) => index >= 0 && index < spell.Effects.Count ? spell.Effects[index].ItemType : 0;
}