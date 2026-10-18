using System.Runtime.CompilerServices;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The visible aura limits (vmangos Unit::AddSpellAuraHolder debuff limit check, Unit.cpp:3284-3305, GetVisibleAurasCount and
/// RemoveAuraDueToVisibleSlotLimit, :3309-3346): a unit shows at most 32 buffs and 16 debuffs (DebuffLimit 0 = 16 from patch 1.7). A holder that
/// would go over evicts the least important holder of its polarity, which can be itself; importance is a category score
/// (SpellAuraHolder::CalculateForBuffLimit / CalculateForDebuffLimit, SpellAuras.cpp:7570-8051) and, within a category, the later application.
/// </summary>
public static class VisibleSlotLimit
{
    /// <summary>vmangos MAX_POSITIVE_AURAS.</summary>
    public const int BuffLimit = 32;

    /// <summary>vmangos CONFIG_UINT32_DEBUFF_LIMIT for patch 1.7 and later (World.cpp:999-1008).</summary>
    public const int DebuffLimit = 16;

    private const uint FamilyWarrior = 4;
    private const uint FamilyDruid = 7;
    private const uint FamilyRogue = 8;

    /// <summary>The category of each first rank listed by CalculateForDebuffLimit (SpellAuras.cpp:7729-7987).</summary>
    private static readonly Dictionary<uint, int> s_firstRankScores = Build(
        (0, [1604, 2094, 3409, 11201, 3600, 4066, 4067, 4069, 4507, 5246, 5530, 6358, 6788, 9672, 339, 2908, 8056, 8122, 605, 6770, 18202, 18075,
            17511, 17504, 17484, 17483, 17407, 17196, 16406, 14914, 21162, 13752, 12328, 13526, 13318, 11366, 133, 12721, 24388, 21159, 21151, 12421,
            12562, 118, 13237, 13327, 13747, 13808, 14251, 3355, 14308, 14309, 15235, 15487, 8034, 16454, 17153, 5484, 18093, 18118, 18223, 710,
            18798, 19229, 19410, 19755, 19784, 19872, 19975, 19974, 19973, 19972, 19971, 19970, 20066, 20586, 24375, 24394]),
        (1, [1833, 5917, 7922, 9658, 10578, 2096, 453, 1714, 11103, 11113, 2974, 3043, 16413, 16415, 17330, 17506, 17639, 18289, 18425, 18469,
            18498, 19821, 20184, 20511, 22639, 23023, 28272]),
        (2, [12579, 67, 26017, 26018, 23605, 702, 11374, 8921, 18265, 980, 13797, 1978, 21992, 2818, 348, 23577, 17315, 16928, 17877, 23415, 9035,
            2944, 24640, 12766, 2943, 19249, 19251, 19252, 19253, 19254, 8050, 172, 16528, 15258, 13003, 589, 12654]),
        (3, [355, 603, 676, 1161, 1543, 2855, 4068, 5116, 5209, 5782, 770, 16857, 120, 10, 2120, 122, 853, 5760, 8692, 11398, 1120, 5740, 689,
            5138, 704, 1490, 12323, 11071, 12543, 12798, 12809, 13218, 13810, 3034, 1510, 13812, 1130, 15286, 15268, 15407, 16922, 17364, 16914,
            17794, 6789, 17862, 18656, 2637, 19185, 10797, 19503, 19675, 19769, 20006, 21183, 20188, 20300, 20301, 20302, 20303, 20185, 20344,
            20345, 20346, 20186, 20354, 20355, 20549, 20253, 20614, 20615, 20116, 26573, 21152, 22959, 23454, 23694, 24423, 5570, 116, 25999]),
        (4, [6136, 6795, 17331, 27648, 24323, 24322, 28732, 25181, 25177, 25178, 25180, 25183, 7321, 16597, 20005]));

    /// <summary>Per spell store: the listed scores keyed by (name, family) of a listed "Rank 1" spell, standing in for the spell chains.</summary>
    private static readonly ConditionalWeakTable<SpellStore, Dictionary<(string, uint), int>> s_rankIndex = new();

    private static Dictionary<uint, int> Build(params (int Score, uint[] Ids)[] groups)
    {
        var map = new Dictionary<uint, int>();
        foreach ((int score, uint[] ids) in groups)
        {
            foreach (uint id in ids)
            {
                map.TryAdd(id, score);
            }
        }

        return map;
    }

    /// <summary>The limit for a holder of this polarity.</summary>
    public static int LimitFor(bool positive) => positive ? BuffLimit : DebuffLimit;

    /// <summary>vmangos SpellAuraHolder::IsMoreImportantVisualAuraThan (SpellAuras.cpp:396-403).</summary>
    internal static bool IsMoreImportant(SpellAuraHolder holder, int score, long appliedOrder, SpellAuraHolder other, int otherScore, long otherOrder)
        => score == otherScore ? appliedOrder > otherOrder : score > otherScore;

    /// <summary>The holder's category: <see cref="BuffScore"/> or <see cref="DebuffScore"/>.</summary>
    internal static int Score(SpellAuraHolder holder, SpellStore store)
        => holder.IsPositive ? BuffScore(holder) : DebuffScore(holder.Spell, holder.IsTriggered, store);

    /// <summary>vmangos SpellAuraHolder::CalculateForBuffLimit (SpellAuras.cpp:7570-7582).</summary>
    internal static int BuffScore(SpellAuraHolder holder)
    {
        if (holder.IsPermanent)
        {
            return 3;
        }

        if (holder.CasterGuid != holder.Target.Guid)
        {
            return 2;
        }

        return holder.CastItemGuid.IsEmpty ? 0 : 1;
    }

    /// <summary>vmangos SpellAuraHolder::CalculateForDebuffLimit (SpellAuras.cpp:7595-8051).</summary>
    public static int DebuffScore(SpellInfo spell, bool triggered, SpellStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(spell);
        if (FamilyScore(spell) is { } family)
        {
            return family;
        }

        if (FirstRankScore(spell, store) is { } listed)
        {
            return listed;
        }

        int score = 0;
        foreach (SpellEffectInfo effect in spell.Effects)
        {
            int current = 0;
            switch (effect.AuraType)
            {
                case AuraType.None:
                    continue;
                case AuraType.ModTaunt:
                case AuraType.ModThreat:
                case AuraType.ModCharm:
                case AuraType.ModFear:
                case AuraType.ModConfuse:
                case AuraType.ModPossess:
                case AuraType.ModStun:
                    current = 4;
                    break;
                case AuraType.ModResistance:
                case AuraType.ModLanguage:
                case AuraType.ModStalked:
                case AuraType.ModDisarm:
                case AuraType.ModDamagePercentTaken:
                case AuraType.PreventsFleeing:
                case AuraType.ModAttackerSpellCritChance:
                case AuraType.ModMeleeHaste:
                case AuraType.ModAttackPower:
                case AuraType.ModDamageDone:
                case AuraType.ModDamageTaken:
                case AuraType.ModHealingPct:
                    current = 3;
                    break;
                case AuraType.PeriodicDamage:
                case AuraType.PeriodicDamagePercent:
                case AuraType.PeriodicLeech:
                    current = 1;
                    break;
                case AuraType.RangedAttackPowerAttackerBonus: // Expose Weakness
                    return 3;
                case AuraType.Dummy when spell.SpellVisual == 3582 && spell.SpellIconId == 150: // Vampiric Embrace
                    return 3;
            }

            score = Math.Max(score, current);
        }

        if (triggered)
        {
            return score > 2 ? 2 : 0;
        }

        return spell.IsChanneled && score < 1 ? 1 : score;
    }

    private static int? FamilyScore(SpellInfo spell)
    {
        ulong flags = spell.SpellFamilyFlags;
        bool Bit(int bit) => (flags & (1UL << bit)) != 0;
        return spell.SpellFamilyName switch
        {
            FamilyRogue when Bit(3) => 1, // Gouge
            FamilyRogue when Bit(8) || Bit(20) => 2, // Garrote, Rupture
            FamilyRogue when Bit(21) || Bit(19) || Bit(25) => 3, // Kidney Shot, Expose Armor, Hemorrhage
            FamilyWarrior when Bit(5) => 2, // Rend
            FamilyWarrior when Bit(1) || Bit(14) || Bit(7) || Bit(27) || Bit(25) => 3, // Hamstring, Sunder Armor, Thunder Clap, Mocking Blow, Mortal Strike
            FamilyWarrior when Bit(17) => 4, // Demoralizing Shout
            FamilyDruid when Bit(12) || Bit(23) => 2, // Rake (and Claw), Rip (and Ferocious Bite)
            FamilyDruid when Bit(13) => 3, // Bash
            FamilyDruid when Bit(3) => 4, // Demoralizing Roar
            _ => null,
        };
    }

    /// <summary>
    /// The listed category of the spell's first rank (sSpellMgr.GetFirstSpellInChain). The repo has no spell chains, so a rank of a listed
    /// spell is found by the name and family of a listed "Rank 1" spell in the store.
    /// </summary>
    private static int? FirstRankScore(SpellInfo spell, SpellStore? store)
    {
        if (s_firstRankScores.TryGetValue(spell.Id, out int direct))
        {
            return direct;
        }

        if (store is null || !spell.Rank.StartsWith("Rank", StringComparison.Ordinal))
        {
            return null;
        }

        Dictionary<(string, uint), int> index = s_rankIndex.GetValue(store, static s =>
        {
            var byName = new Dictionary<(string, uint), int>();
            foreach ((uint id, int score) in s_firstRankScores)
            {
                if (s.Get(id) is { Rank: "Rank 1" } first)
                {
                    byName.TryAdd((first.Name, first.SpellFamilyName), score);
                }
            }

            return byName;
        });
        return index.TryGetValue((spell.Name, spell.SpellFamilyName), out int ranked) ? ranked : null;
    }
}