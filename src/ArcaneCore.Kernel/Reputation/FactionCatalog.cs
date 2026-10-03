namespace ArcaneCore.Kernel.Reputation;

/// <summary>
/// One build-5875 Faction.dbc row: the reputation-bearing faction a faction template points at.
/// Field order verified against cmangos/mangos-classic DBCStructure.h FactionEntry and
/// DBCfmt.h FactionEntryfmt ("niiiiiiiiiiiiiiiiiissssssssxxxxxxxxxx", 37 four-byte fields);
/// vmangos/core 4b3d241 DBCStructure.h FactionEntry is the same structure. Localised names and
/// descriptions are not retained. Arrays are copied so the record stays immutable.
/// </summary>
public sealed class FactionRecord
{
    /// <summary>Base-reputation slots per faction (four race/class mask groups).</summary>
    public const int BaseSlots = 4;

    private readonly uint[] _raceMasks;
    private readonly uint[] _classMasks;
    private readonly int[] _baseValues;
    private readonly uint[] _baseFlags;

    public FactionRecord(uint id, int reputationListId, IReadOnlyList<uint> raceMasks, IReadOnlyList<uint> classMasks,
        IReadOnlyList<int> baseValues, IReadOnlyList<uint> baseFlags, uint parentFactionId = 0, string name = "")
    {
        ArgumentNullException.ThrowIfNull(raceMasks);
        ArgumentNullException.ThrowIfNull(classMasks);
        ArgumentNullException.ThrowIfNull(baseValues);
        ArgumentNullException.ThrowIfNull(baseFlags);
        if (raceMasks.Count != BaseSlots || classMasks.Count != BaseSlots || baseValues.Count != BaseSlots || baseFlags.Count != BaseSlots)
        {
            throw new ArgumentException("a faction has exactly four base-reputation slots");
        }

        Id = id;
        ReputationListId = reputationListId;
        ParentFactionId = parentFactionId;
        Name = name ?? string.Empty;
        _raceMasks = [.. raceMasks];
        _classMasks = [.. classMasks];
        _baseValues = [.. baseValues];
        _baseFlags = [.. baseFlags];
    }

    /// <summary>Faction.dbc m_ID.</summary>
    public uint Id { get; }

    /// <summary>m_reputationIndex: the client's reputation-list slot, or negative for no reputation.</summary>
    public int ReputationListId { get; }

    /// <summary>m_parentFactionID (vmangos "team"): the faction that receives team-award spillover.</summary>
    public uint ParentFactionId { get; }

    /// <summary>The first (enUS) localised name, informational only.</summary>
    public string Name { get; }

    public IReadOnlyList<uint> RaceMasks => _raceMasks;

    public IReadOnlyList<uint> ClassMasks => _classMasks;

    public IReadOnlyList<int> BaseValues => _baseValues;

    public IReadOnlyList<uint> BaseFlags => _baseFlags;

    /// <summary>FactionEntry::CanHaveReputation.</summary>
    public bool CanHaveReputation => ReputationListId >= 0;

    /// <summary>
    /// FactionEntry::GetIndexFitTo: the first slot whose race mask and class mask are each zero
    /// or match the player's bit. -1 when none fits.
    /// </summary>
    public int BaseSlotFor(uint raceMask, uint classMask)
    {
        for (int i = 0; i < BaseSlots; i++)
        {
            if ((_raceMasks[i] == 0 || (_raceMasks[i] & raceMask) != 0)
                && (_classMasks[i] == 0 || (_classMasks[i] & classMask) != 0))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>ReputationMgr::GetBaseReputation for a race/class mask pair.</summary>
    public int BaseReputation(uint raceMask, uint classMask) => BaseSlotFor(raceMask, classMask) is var i and >= 0 ? _baseValues[i] : 0;

    /// <summary>ReputationMgr::GetDefaultStateFlags for a race/class mask pair.</summary>
    public uint DefaultFlags(uint raceMask, uint classMask) => BaseSlotFor(raceMask, classMask) is var i and >= 0 ? _baseFlags[i] : 0;
}

/// <summary>
/// Startup Faction.dbc content. Ids and reputation-list slots are unique; the client has 64
/// list slots in 1.12 (SMSG_INITIALIZE_FACTIONS), so a slot outside 0..63 is rejected.
/// </summary>
public sealed class FactionCatalog
{
    /// <summary>Reputation list slots in build 5875 (vmangos InitializeFactions: 64).</summary>
    public const int ReputationListSize = 64;

    private readonly Dictionary<uint, FactionRecord> _byId;
    private readonly Dictionary<int, FactionRecord> _byListId;

    public static FactionCatalog Empty { get; } = new([]);

    public FactionCatalog(IEnumerable<FactionRecord> factions)
    {
        ArgumentNullException.ThrowIfNull(factions);
        _byId = [];
        _byListId = [];
        foreach (FactionRecord faction in factions)
        {
            ArgumentNullException.ThrowIfNull(faction);
            if (faction.Id == 0)
            {
                throw new ArgumentException("faction id zero is not a resolvable faction", nameof(factions));
            }

            if (!_byId.TryAdd(faction.Id, faction))
            {
                throw new ArgumentException($"duplicate faction id {faction.Id}", nameof(factions));
            }

            if (faction.CanHaveReputation)
            {
                if (faction.ReputationListId >= ReputationListSize)
                {
                    throw new ArgumentException($"faction {faction.Id} reputation slot {faction.ReputationListId} exceeds the 1.12 list", nameof(factions));
                }

                if (!_byListId.TryAdd(faction.ReputationListId, faction))
                {
                    throw new ArgumentException($"duplicate reputation slot {faction.ReputationListId}", nameof(factions));
                }
            }
        }
    }

    public int Count => _byId.Count;

    public FactionRecord? Find(uint id) => _byId.GetValueOrDefault(id);

    public FactionRecord? FindByListId(int reputationListId) => _byListId.GetValueOrDefault(reputationListId);

    /// <summary>Every faction that can carry reputation, ordered by list slot.</summary>
    public IEnumerable<FactionRecord> ReputationFactions => _byListId.Values.OrderBy(f => f.ReputationListId);
}
