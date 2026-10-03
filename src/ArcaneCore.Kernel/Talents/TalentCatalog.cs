namespace ArcaneCore.Kernel.Talents;

/// <summary>
/// One Talent.dbc row (build 5875). Field indexes: vmangos src/game/Database/DBCfmt.h:81
/// ("niiiiiiiixxxxixxixxxi") and DBCStructure.h:660-675 (TalentEntry): id 0, tab 1, row 2, column 3,
/// RankID[5] 4-8, DependsOn 13, DependsOnRank 16, DependsOnSpell 20. <see cref="DependsOnRank"/> is
/// the lowest zero-based rank index of the prerequisite that satisfies it (vmangos Player::LearnTalent
/// loops <c>i = DependsOnRank .. 4</c>, Player.cpp:20733-20740).
/// </summary>
public sealed record TalentRecord(
    uint Id,
    uint TabId,
    uint Row,
    uint Column,
    IReadOnlyList<uint> RankSpells,
    uint DependsOn,
    uint DependsOnRank,
    uint DependsOnSpell)
{
    /// <summary>Talents have at most five ranks (vmangos MAX_TALENT_RANK, DBCStructure.h:658).</summary>
    public const int MaxRanks = 5;

    /// <summary>Number of non-zero rank spells (ranks are contiguous from index 0; the catalog enforces it).</summary>
    public int RankCount => RankSpells.Count(s => s != 0);
}

/// <summary>
/// One TalentTab.dbc row. vmangos DBCfmt.h:82 ("nxxxxxxxxxxxiix") and DBCStructure.h:677-686:
/// id 0, ClassMask 12, order 13.
/// </summary>
public sealed record TalentTabRecord(uint Id, uint ClassMask, uint Order);

/// <summary>Which talent and which zero-based rank a spell is (vmangos sTalentSpellPosMap, DBCStores.cpp:331-336).</summary>
public readonly record struct TalentRankPosition(uint TalentId, int RankIndex);

/// <summary>
/// The immutable talent content: Talent.dbc plus TalentTab.dbc. Validated at construction
/// (fail closed, where vmangos silently ignores bad rows): unique ids, five-slot contiguous rank
/// arrays, no spell that is a rank of two talents, existing tabs, rows of at most 10, and prerequisites
/// that resolve, are acyclic and name a rank the prerequisite has.
/// </summary>
public sealed class TalentCatalog
{
    /// <summary>Largest accepted talent row (vanilla uses rows 0-6; the bound only rejects garbage).</summary>
    public const uint MaxRow = 10;

    private readonly Dictionary<uint, TalentRecord> _talents = [];
    private readonly Dictionary<uint, TalentTabRecord> _tabs = [];
    private readonly Dictionary<uint, TalentRankPosition> _byRankSpell = [];
    private readonly Dictionary<uint, List<TalentRecord>> _byTab = [];
    private readonly List<TalentTabRecord> _tabOrder;

    public TalentCatalog(IEnumerable<TalentTabRecord> tabs, IEnumerable<TalentRecord> talents)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(talents);
        foreach (TalentTabRecord tab in tabs)
        {
            if (!_tabs.TryAdd(tab.Id, tab))
            {
                throw new InvalidDataException($"TalentTab {tab.Id} appears twice");
            }
        }

        _tabOrder = [.. _tabs.Values.OrderBy(t => t.Order).ThenBy(t => t.Id)];

        List<TalentRecord> ordered = [.. talents.OrderBy(t => t.Id)];
        foreach (TalentRecord talent in ordered)
        {
            Validate(talent);
            if (!_talents.TryAdd(talent.Id, talent))
            {
                throw new InvalidDataException($"Talent {talent.Id} appears twice");
            }

            for (int rank = 0; rank < talent.RankSpells.Count; rank++)
            {
                uint spell = talent.RankSpells[rank];
                if (spell != 0 && !_byRankSpell.TryAdd(spell, new TalentRankPosition(talent.Id, rank)))
                {
                    throw new InvalidDataException($"Spell {spell} is a rank of more than one talent (talent {talent.Id})");
                }
            }

            if (!_byTab.TryGetValue(talent.TabId, out List<TalentRecord>? list))
            {
                _byTab[talent.TabId] = list = [];
            }

            list.Add(talent);
        }

        foreach (TalentRecord talent in ordered)
        {
            ValidatePrerequisites(talent);
        }
    }

    public int TalentCount => _talents.Count;

    public int TabCount => _tabs.Count;

    /// <summary>Every talent in id order.</summary>
    public IEnumerable<TalentRecord> Talents => _talents.Values.OrderBy(t => t.Id);

    public TalentRecord? ById(uint talentId) => _talents.GetValueOrDefault(talentId);

    public TalentTabRecord? Tab(uint tabId) => _tabs.GetValueOrDefault(tabId);

    /// <summary>The talent and rank a spell id is, when it is a talent rank spell.</summary>
    public bool TryGetRankPosition(uint spellId, out TalentRankPosition position)
        => _byRankSpell.TryGetValue(spellId, out position);

    /// <summary>The talents of one tab in id order (empty for an unknown tab).</summary>
    public IReadOnlyList<TalentRecord> TalentsOfTab(uint tabId)
        => _byTab.TryGetValue(tabId, out List<TalentRecord>? list) ? list : [];

    /// <summary>The tabs whose ClassMask shares a bit with <paramref name="classMask"/> (vmangos <c>GetClassMask() &amp; ClassMask</c>), in tab order.</summary>
    public IReadOnlyList<TalentTabRecord> TabsForClassMask(uint classMask)
        => [.. _tabOrder.Where(t => (t.ClassMask & classMask) != 0)];

    private void Validate(TalentRecord talent)
    {
        if (talent.RankSpells is null || talent.RankSpells.Count != TalentRecord.MaxRanks)
        {
            throw new InvalidDataException($"Talent {talent.Id} must carry exactly {TalentRecord.MaxRanks} rank slots");
        }

        if (!_tabs.ContainsKey(talent.TabId))
        {
            throw new InvalidDataException($"Talent {talent.Id} names unknown tab {talent.TabId}");
        }

        if (talent.Row > MaxRow)
        {
            throw new InvalidDataException($"Talent {talent.Id} row {talent.Row} exceeds {MaxRow}");
        }

        bool seenEmpty = false;
        foreach (uint spell in talent.RankSpells)
        {
            if (spell == 0)
            {
                seenEmpty = true;
            }
            else if (seenEmpty)
            {
                throw new InvalidDataException($"Talent {talent.Id} has a rank after an empty rank slot");
            }
        }
    }

    private void ValidatePrerequisites(TalentRecord talent)
    {
        if (talent.DependsOn == 0)
        {
            return;
        }

        var seen = new HashSet<uint> { talent.Id };
        TalentRecord? cursor = talent;
        while (cursor is { DependsOn: not 0 })
        {
            if (!_talents.TryGetValue(cursor.DependsOn, out TalentRecord? next))
            {
                throw new InvalidDataException($"Talent {cursor.Id} depends on unknown talent {cursor.DependsOn}");
            }

            if (!seen.Add(next.Id))
            {
                throw new InvalidDataException($"Talent {talent.Id} has a cyclic prerequisite chain through {next.Id}");
            }

            cursor = next;
        }

        TalentRecord prerequisite = _talents[talent.DependsOn];
        if (talent.DependsOnRank >= prerequisite.RankCount)
        {
            throw new InvalidDataException(
                $"Talent {talent.Id} needs rank index {talent.DependsOnRank} of talent {prerequisite.Id}, which has {prerequisite.RankCount} ranks");
        }
    }
}
