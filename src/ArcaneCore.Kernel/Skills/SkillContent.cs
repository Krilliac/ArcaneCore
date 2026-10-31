using System.Collections.Frozen;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Talents;

namespace ArcaneCore.Kernel.Skills;

/// <summary>
/// The spells that grant a skill when learned (vmangos SpellMgr::LoadSpellLearnSkills, Spells/SpellMgr.cpp:1850-1887):
/// the first SPELL_EFFECT_SKILL effect of a spell decides. The skill is the effect's MiscValue and the step
/// its simple value (BasePoints + BaseDice); the starting value is 1, except for Riding where it is
/// <c>step * 75</c>; the maximum is <c>step * 75</c>. Every number is truncated to <c>uint16</c> like
/// <c>SpellLearnSkillNode</c>.
/// </summary>
public sealed class SpellLearnSkillTable
{
    private readonly FrozenDictionary<uint, SpellLearnSkillNode> _bySpell;
    private readonly FrozenDictionary<uint, ushort> _effectOneSkill;

    private SpellLearnSkillTable(Dictionary<uint, SpellLearnSkillNode> bySpell, Dictionary<uint, ushort> effectOne)
    {
        _bySpell = bySpell.ToFrozenDictionary();
        _effectOneSkill = effectOne.ToFrozenDictionary();
    }

    public static SpellLearnSkillTable Empty { get; } = Build([]);

    public int Count => _bySpell.Count;

    /// <summary>
    /// Derive the table from every SKILL effect of every spell. Effects of one spell are ordered by effect
    /// index; the lowest index wins (the reference <c>break</c>s after the first).
    /// </summary>
    public static SpellLearnSkillTable Build(IEnumerable<SpellSkillEffect> skillEffects)
    {
        ArgumentNullException.ThrowIfNull(skillEffects);
        var bySpell = new Dictionary<uint, SpellLearnSkillNode>();
        var lowest = new Dictionary<uint, int>();
        var effectOne = new Dictionary<uint, ushort>();
        foreach (SpellSkillEffect effect in skillEffects)
        {
            if (effect.EffectIndex == 1)
            {
                // SpellMgr::IsPrimaryProfessionSpell reads Effect[EFFECT_INDEX_1] only (SpellMgr.cpp:1356-1373).
                effectOne[effect.SpellId] = unchecked((ushort)effect.MiscValue);
            }

            if (lowest.TryGetValue(effect.SpellId, out int index) && index <= effect.EffectIndex)
            {
                continue;
            }

            lowest[effect.SpellId] = effect.EffectIndex;
            unchecked
            {
                ushort step = (ushort)(effect.BasePoints + effect.BaseDice);
                ushort skill = (ushort)effect.MiscValue;
                ushort value = skill == SkillIds.Riding ? (ushort)(step * 75) : (ushort)1;
                bySpell[effect.SpellId] = new SpellLearnSkillNode(skill, step, value, (ushort)(step * 75));
            }
        }

        return new SpellLearnSkillTable(bySpell, effectOne);
    }

    public bool TryGet(uint spellId, out SpellLearnSkillNode node) => _bySpell.TryGetValue(spellId, out node);

    /// <summary>The skill a spell's second effect (index 1) teaches, the only effect the profession predicates read.</summary>
    public bool TryGetEffectOneSkill(uint spellId, out ushort skillId) => _effectOneSkill.TryGetValue(spellId, out skillId);
}

/// <summary>
/// Rank links the client DBC lacks for the trainer-sold higher ranks of single-rank talent abilities (Pyroblast, Holy Shield, ...).
/// Talent.dbc lists one spell for these talents and SkillLineAbility.dbc has forward_spellid 0 on these ranks, so the DBC chain stops
/// at rank 1 (Nature's Grasp is DBC-linked only from rank 1 to 2). The reference servers keep the link in spell_chain prev_spell (mangos-classic sql/archive/0.6/2875_spell_chain.sql,
/// e.g. <c>('12505','11366','11366','2')</c>; vmangos SpellMgr::LoadSpellChains merges those rows over the DBC chains). With the link a
/// trainer refuses the rank until the player knows the talent spell (Player::GetTrainerSpellState) and a respec walks up to the
/// purchased ranks. A link the DBC provides always wins; Mortal Strike, Bloodthirst, Shield Slam and Hemorrhage are chained by the DBC already.
/// </summary>
public static class TalentAbilityRankLinks
{
    /// <summary>(rank spell, previous rank spell) pairs, lowest rank first within each ability.</summary>
    public static IReadOnlyList<(uint Rank, uint Previous)> Links { get; } = Build();

    private static (uint Rank, uint Previous)[] Build() =>
    [
        // Pyroblast
        (12505, 11366), (12522, 12505), (12523, 12522), (12524, 12523), (12525, 12524), (12526, 12525), (18809, 12526),
        // Blast Wave
        (13018, 11113), (13019, 13018), (13020, 13019), (13021, 13020),
        // Ice Barrier
        (13031, 11426), (13032, 13031), (13033, 13032),
        // Aimed Shot
        (20900, 19434), (20901, 20900), (20902, 20901), (20903, 20902), (20904, 20903),
        // Shadowburn
        (18867, 17877), (18868, 18867), (18869, 18868), (18870, 18869), (18871, 18870),
        // Conflagrate
        (18930, 17962), (18931, 18930), (18932, 18931),
        // Siphon Life
        (18879, 18265), (18880, 18879), (18881, 18880),
        // Holy Shock
        (20929, 20473), (20930, 20929),
        // Seal of Command
        (20915, 20375), (20918, 20915), (20919, 20918), (20920, 20919),
        // Insect Swarm
        (24974, 5570), (24975, 24974), (24976, 24975), (24977, 24976),
        // Faerie Fire (Feral)
        (17390, 16857), (17391, 17390), (17392, 17391),
        // Wyvern Sting
        (24132, 19386), (24133, 24132),
        // Counterattack
        (20909, 19306), (20910, 20909),
        // Mind Flay
        (17311, 15407), (17312, 17311), (17313, 17312), (17314, 17313), (18807, 17314),
        // Holy Nova
        (15430, 15237), (15431, 15430), (27799, 15431), (27800, 27799), (27801, 27800),
        // Divine Spirit
        (14818, 14752), (14819, 14818), (27841, 14819),
        // Prayer of Spirit (mangos-classic spell_chain: ('27681','14752','14752','2'))
        (27681, 14752),
        // Lightwell
        (27870, 724), (27871, 27870),
        // Dark Pact
        (18937, 18220), (18938, 18937),
        // Trueshot Aura
        (20905, 19506), (20906, 20905),
        // Blessing of Sanctuary
        (20912, 20911), (20913, 20912), (20914, 20913),
        // Holy Shield
        (20927, 20925), (20928, 20927),
        // Consecration
        (20116, 26573), (20922, 20116), (20923, 20922), (20924, 20923),
        // Nature's Grasp (the DBC only links rank 1 -> 2)
        (16811, 16810), (16812, 16811), (16813, 16812), (17329, 16813),
        // Mana Tide Totem. Neither reference chains these ranks; retail trains rank 2 and 3 only after rank 1, so this follows retail beyond the references.
        (17354, 16190), (17359, 17354),
    ];
}

/// <summary>
/// The spell_chain req_spell column for the trainer-sold spells that carry one (mangos-classic sql/base/mangos.sql spell_chain rows
/// <c>(25898,0,25898,1,20217)</c> and <c>(25899,0,25899,1,20914)</c>): Greater Blessing of Kings needs Blessing of Kings and Greater
/// Blessing of Sanctuary needs Blessing of Sanctuary rank 4. Trainers refuse the spell until the player knows the required spell
/// (Player::GetTrainerSpellState) and send it as the first chain node of the trainer list. No other 1.12 row has a req_spell.
/// </summary>
public static class SpellChainRequirements
{
    private static readonly FrozenDictionary<uint, uint> Required = new Dictionary<uint, uint>
    {
        [25898] = 20217,
        [25899] = 20914,
    }.ToFrozenDictionary();

    /// <summary>The spell the player must know before <paramref name="spellId"/> is trainable, 0 when none.</summary>
    public static uint RequiredFor(uint spellId) => Required.GetValueOrDefault(spellId);
}

/// <summary>
/// Ranks of ranked spells derived from SkillLineAbility.dbc forward links (vmangos SpellMgr::LoadSpellChains,
/// Spells/SpellMgr.cpp:1543-1650 and LoadSpellChains_AbilityHelper :1440-1503), with the two reference patches:
/// Herb Gathering Apprentice (2366) forwards to 2368 although the 1.12 client has no link for it
/// (:1559-1562) and Seal of Righteousness (20154) never starts a chain (:1564-1565).
/// </summary>
public sealed class SpellRankChains
{
    /// <summary>The reference recurses at most 30 deep and asserts beyond (SpellMgr.cpp:1440).</summary>
    private const int MaxChainDepth = 30;

    /// <summary>Herb Gathering, Apprentice (SpellMgr.cpp:1559-1562).</summary>
    public const uint HerbGatheringApprentice = 2366;

    /// <summary>Herb Gathering, Journeyman: the forced forward link of <see cref="HerbGatheringApprentice"/>.</summary>
    public const uint HerbGatheringJourneyman = 2368;

    /// <summary>Seal of Righteousness (20154) "makes double in spellbook" and is skipped (SpellMgr.cpp:1564-1565).</summary>
    public const uint SealOfRighteousness = 20154;

    private readonly FrozenDictionary<uint, uint> _previous;
    private readonly FrozenDictionary<uint, uint> _first;
    private readonly FrozenDictionary<uint, byte> _rank;
    private readonly FrozenDictionary<uint, uint> _next;

    public SpellRankChains(IEnumerable<SkillLineAbilityRecord> abilities, IEnumerable<TalentRecord>? talents)
        : this(abilities, null, talents)
    {
    }

    public SpellRankChains(IEnumerable<SkillLineAbilityRecord> abilities, Func<uint, bool>? spellExists = null, IEnumerable<TalentRecord>? talents = null)
    {
        ArgumentNullException.ThrowIfNull(abilities);
        spellExists ??= static _ => true;
        SkillLineAbilityRecord[] all = abilities.ToArray();
        HashSet<uint> withAbilities = all.Select(a => a.SpellId).ToHashSet();

        // The reference iterates mSkillLineAbilityMapBySpellId (a multimap ordered by spell id).
        // vmangos SpellMgr::LoadSpellChains (Spells/SpellMgr.cpp:1510-1541): link consecutive ranks of multi-rank talents first.
        var previous = new SortedDictionary<uint, uint>();
        bool hasTalents = false;
        if (talents is not null)
        {
            foreach (TalentRecord talent in talents.OrderBy(t => t.Id))
            {
                hasTalents = true;
                if (talent?.RankSpells is not { Count: > 1 } ranks)
                {
                    continue;
                }

                for (int r = 0; r < ranks.Count - 1; r++)
                {
                    uint prevSpell = ranks[r];
                    uint nextSpell = ranks[r + 1];
                    if (prevSpell == 0 || nextSpell == 0 || prevSpell == nextSpell
                        || !spellExists(prevSpell) || !spellExists(nextSpell))
                    {
                        continue;
                    }

                    previous.TryAdd(nextSpell, prevSpell);
                }
            }
        }

        HasTalents = hasTalents;
        foreach (SkillLineAbilityRecord ability in all.OrderBy(a => a.SpellId))
        {
            uint spell = ability.SpellId;
            if (!spellExists(spell))
            {
                continue;
            }

            uint forward = ability.ForwardSpellId;
            if (spell == HerbGatheringApprentice)
            {
                forward = HerbGatheringJourneyman;
            }

            if (spell == SealOfRighteousness || forward == 0 || !spellExists(forward) || !withAbilities.Contains(forward))
            {
                continue;
            }

            // Conflicting data (two spells forwarding to one) asserts in the reference; the lowest spell id is kept.
            previous.TryAdd(forward, spell);
        }

        // vmangos merges spell_chain rows over the DBC chains; the link is kept only when both ranks are real abilities and the DBC gave none.
        foreach ((uint rankSpell, uint before) in TalentAbilityRankLinks.Links)
        {
            if (withAbilities.Contains(rankSpell) && withAbilities.Contains(before) && spellExists(rankSpell) && spellExists(before))
            {
                previous.TryAdd(rankSpell, before);
            }
        }

        var prev = new Dictionary<uint, uint>();
        var first = new Dictionary<uint, uint>();
        var rank = new Dictionary<uint, byte>();
        var nodes = new HashSet<uint>(previous.Keys);
        nodes.UnionWith(previous.Values);
        foreach (uint spell in nodes)
        {
            uint cursor = spell;
            int depth = 1;
            while (previous.TryGetValue(cursor, out uint before) && depth <= MaxChainDepth + 1)
            {
                cursor = before;
                depth++;
            }

            if (depth > MaxChainDepth + 1)
            {
                continue; // a cycle or an over-long chain: the reference asserts here
            }

            first[spell] = cursor;
            rank[spell] = (byte)depth;
            if (previous.TryGetValue(spell, out uint direct))
            {
                prev[spell] = direct;
            }
        }

        var next = new Dictionary<uint, uint>();
        foreach ((uint spell, uint before) in prev)
        {
            if (rank.ContainsKey(spell))
            {
                next.TryAdd(before, spell);
            }
        }

        _previous = prev.ToFrozenDictionary();
        _first = first.ToFrozenDictionary();
        _rank = rank.ToFrozenDictionary();
        _next = next.ToFrozenDictionary();
    }

    public static SpellRankChains Empty { get; } = new([]);

    public bool HasTalents { get; }

    public int Count => _rank.Count;

    /// <summary>The rank (1 = first) of a ranked spell, 0 when it is in no chain (vmangos GetSpellRank, SpellMgr.h:534-540).</summary>
    public byte Rank(uint spellId) => _rank.GetValueOrDefault(spellId);

    /// <summary>The previous rank, 0 for a first rank or an unranked spell.</summary>
    public uint Previous(uint spellId) => _previous.GetValueOrDefault(spellId);

    /// <summary>The next rank, 0 for the highest rank or an unranked spell.</summary>
    public uint Next(uint spellId) => _next.GetValueOrDefault(spellId);

    /// <summary>The first rank of the chain a spell belongs to, 0 when unranked.</summary>
    public uint First(uint spellId) => _first.GetValueOrDefault(spellId);

    /// <summary>vmangos SpellMgr::IsHighRankOfSpell (SpellMgr.h:542-558): <paramref name="spell1"/> is a strictly higher rank of <paramref name="spell2"/> in one chain.</summary>
    public bool IsHighRankOf(uint spell1, uint spell2)
    {
        byte rank1 = Rank(spell1);
        byte rank2 = Rank(spell2);
        if (rank1 == 0 || rank2 == 0 || rank1 <= rank2)
        {
            return false;
        }

        for (uint cursor = spell1; _previous.TryGetValue(cursor, out uint before); cursor = before)
        {
            if (before == spell2)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Immutable skill content: SkillLine, SkillRaceClassInfo, SkillTiers and SkillLineAbility rows with the
/// lookups the reference builds from them (vmangos Database/DBCStores.cpp, Spells/SpellMgr.h:244-277,
/// ObjectMgr.cpp:10450-10464). Loaded once from the developer-supplied DBC files; never mutated.
/// </summary>
public sealed class SkillCatalog
{
    private readonly FrozenDictionary<uint, SkillLineRecord> _lines;
    private readonly FrozenDictionary<uint, SkillRaceClassInfoRecord[]> _raceClassBySkill;
    private readonly FrozenDictionary<uint, SkillTierRecord> _tiers;
    private readonly FrozenDictionary<uint, SkillLineAbilityRecord[]> _abilitiesBySkill;
    private readonly FrozenDictionary<uint, SkillLineAbilityRecord[]> _abilitiesBySpell;

    /// <param name="lines">SkillLine.dbc rows (a duplicate id is rejected).</param>
    /// <param name="raceClassInfos">SkillRaceClassInfo.dbc rows in file order; lookups report the first fitting row.</param>
    /// <param name="tiers">SkillTiers.dbc rows (a duplicate id is rejected).</param>
    /// <param name="abilities">SkillLineAbility.dbc rows in file order.</param>
    /// <param name="learnSkills">Spell-derived skill grants (empty when the spell store is not loaded).</param>
    /// <param name="spellExists">Whether a spell id exists in Spell.dbc (rank chains skip links to missing spells); all exist when null.</param>
    public SkillCatalog(
        IEnumerable<SkillLineRecord> lines,
        IEnumerable<SkillRaceClassInfoRecord> raceClassInfos,
        IEnumerable<SkillTierRecord> tiers,
        IEnumerable<SkillLineAbilityRecord> abilities,
        SpellLearnSkillTable? learnSkills = null,
        Func<uint, bool>? spellExists = null,
        IEnumerable<TalentRecord>? talents = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(raceClassInfos);
        ArgumentNullException.ThrowIfNull(tiers);
        ArgumentNullException.ThrowIfNull(abilities);

        _lines = ToUniqueDictionary(lines, l => l.Id, "SkillLine");
        _tiers = ToUniqueDictionary(tiers, t => t.Id, "SkillTiers");
        _raceClassBySkill = raceClassInfos.GroupBy(r => r.SkillId).ToFrozenDictionary(g => g.Key, g => g.ToArray());
        SkillLineAbilityRecord[] abilityRows = abilities.ToArray();
        Abilities = abilityRows;
        _abilitiesBySkill = abilityRows.GroupBy(a => a.SkillId).ToFrozenDictionary(g => g.Key, g => g.ToArray());
        _abilitiesBySpell = abilityRows.GroupBy(a => a.SpellId).ToFrozenDictionary(g => g.Key, g => g.ToArray());
        LearnSkills = learnSkills ?? SpellLearnSkillTable.Empty;
        Ranks = new SpellRankChains(abilityRows, spellExists, talents);
    }

    public static SkillCatalog Empty { get; } = new([], [], [], []);

    /// <summary>The skills spells grant when learned.</summary>
    public SpellLearnSkillTable LearnSkills { get; }

    /// <summary>Spell ranks derived from the ability forward links.</summary>
    public SpellRankChains Ranks { get; }

    public IReadOnlyList<SkillLineAbilityRecord> Abilities { get; }

    public int LineCount => _lines.Count;

    public IEnumerable<SkillLineRecord> Lines => _lines.Values;

    public SkillLineRecord? Line(uint skillId) => _lines.GetValueOrDefault(skillId);

    public SkillTierRecord? Tier(uint tierId) => _tiers.GetValueOrDefault(tierId);

    /// <summary>The abilities (spell rows) of a skill line, in file order.</summary>
    public IReadOnlyList<SkillLineAbilityRecord> AbilitiesOfSkill(uint skillId) => _abilitiesBySkill.GetValueOrDefault(skillId) ?? [];

    /// <summary>The abilities a spell appears in, in file order.</summary>
    public IReadOnlyList<SkillLineAbilityRecord> AbilitiesOfSpell(uint spellId) => _abilitiesBySpell.GetValueOrDefault(spellId) ?? [];

    /// <summary>Every SkillRaceClassInfo row of a skill, in file order.</summary>
    public IReadOnlyList<SkillRaceClassInfoRecord> RaceClassInfos(uint skillId) => _raceClassBySkill.GetValueOrDefault(skillId) ?? [];

    /// <summary>
    /// vmangos <c>GetSkillRaceClassInfo</c> (DBCStores.cpp:589-602): the first row, in file order, whose race
    /// mask contains <c>1 &lt;&lt; (race - 1)</c> and class mask contains <c>1 &lt;&lt; (class - 1)</c>; a mask
    /// of zero accepts everything. Null when no row fits.
    /// </summary>
    public SkillRaceClassInfoRecord? RaceClassInfo(uint skillId, byte race, byte playerClass)
    {
        if (race is < 1 or > 32 || playerClass is < 1 or > 32)
        {
            return null;
        }

        uint raceBit = 1u << (race - 1);
        uint classBit = 1u << (playerClass - 1);
        foreach (SkillRaceClassInfoRecord row in RaceClassInfos(skillId))
        {
            if ((row.RaceMask != 0 && (row.RaceMask & raceBit) == 0) || (row.ClassMask != 0 && (row.ClassMask & classBit) == 0))
            {
                continue;
            }

            return row;
        }

        return null;
    }

    /// <summary>
    /// vmangos <c>GetSkillRangeType</c> (ObjectMgr.cpp:10450-10464): a row whose tier exists is Rank; otherwise
    /// the category decides (armor Mono, languages Language, anything else Level). Null for an unknown skill line.
    /// </summary>
    public SkillRangeType? RangeType(uint skillId, SkillRaceClassInfoRecord raceClass)
    {
        ArgumentNullException.ThrowIfNull(raceClass);
        SkillLineRecord? line = Line(skillId);
        if (line is null)
        {
            return null;
        }

        if (_tiers.ContainsKey(raceClass.TierId))
        {
            return SkillRangeType.Rank;
        }

        return line.Category switch
        {
            SkillCategories.Armor => SkillRangeType.Mono,
            SkillCategories.Languages => SkillRangeType.Language,
            _ => SkillRangeType.Level,
        };
    }

    /// <summary>vmangos <c>IsPrimaryProfessionSkill</c> (SpellMgr.h:256-265): a known skill line of category 11.</summary>
    public bool IsPrimaryProfessionSkill(uint skillId) => Line(skillId)?.Category == SkillCategories.Profession;

    /// <summary>vmangos <c>IsProfessionSkill</c> (SpellMgr.h:267-270): a primary profession, Fishing, Cooking or First Aid.</summary>
    public bool IsProfessionSkill(uint skillId)
        => IsPrimaryProfessionSkill(skillId) || skillId is SkillIds.Fishing or SkillIds.Cooking or SkillIds.FirstAid;

    /// <summary>vmangos <c>IsProfessionOrRidingSkill</c> (SpellMgr.h:272-275).</summary>
    public bool IsProfessionOrRidingSkill(uint skillId) => IsProfessionSkill(skillId) || skillId == SkillIds.Riding;

    /// <summary>vmangos <c>SpellMgr::IsPrimaryProfessionSpell</c> (SpellMgr.cpp:1356-1373): effect 1 is a SKILL effect for a primary profession.</summary>
    public bool IsPrimaryProfessionSpell(uint spellId)
        => LearnSkills.TryGetEffectOneSkill(spellId, out ushort skill) && IsPrimaryProfessionSkill(skill);

    /// <summary>vmangos <c>IsPrimaryProfessionFirstRankSpell</c> (SpellMgr.cpp:1370-1373): a primary profession spell of rank 1.</summary>
    public bool IsPrimaryProfessionFirstRankSpell(uint spellId) => IsPrimaryProfessionSpell(spellId) && Ranks.Rank(spellId) == 1;

    private static FrozenDictionary<uint, T> ToUniqueDictionary<T>(IEnumerable<T> rows, Func<T, uint> key, string table)
    {
        var map = new Dictionary<uint, T>();
        foreach (T row in rows)
        {
            if (!map.TryAdd(key(row), row))
            {
                throw new ArgumentException($"duplicate {table} id {key(row)}");
            }
        }

        return map.ToFrozenDictionary();
    }
}
