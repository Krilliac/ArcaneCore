using ArcaneCore.Kernel.Honor;

namespace ArcaneCore.Game.Honor;

/// <summary>
/// One player's honor, owned by the world thread (vmangos HonorMgr members: m_honorCP, m_rankPoints, m_standing,
/// m_lastWeekHK/CP, m_storedHK/DK, m_rank, m_highestRank, m_totalHK/DK). Mutated only through
/// <see cref="HonorService"/>.
/// </summary>
public sealed class HonorState
{
    private readonly List<HonorCpRecord> _rows;

    internal HonorState(CharacterHonorData data)
    {
        _rows = [.. data.Cp];
        RankPoints = data.State.RankPoints;
        HighestRank = HonorRanks.Describe(data.State.HighestRank, true);
        Standing = data.State.Standing;
        LastWeekHk = data.State.LastWeekHk;
        LastWeekCp = data.State.LastWeekCp;
        StoredHk = data.State.StoredHk;
        StoredDk = data.State.StoredDk;
        PvpFlags = data.State.PvpFlags;
        CityProtector = data.State.CityProtector;
        Rank = HonorRanks.None;
    }

    /// <summary>The damage this player took recently, by attacking player (the PvP kill credit input).</summary>
    public PvpDamageLedger Ledger { get; } = new();

    /// <summary>Every contribution row, oldest first.</summary>
    public IReadOnlyList<HonorCpRecord> Rows => _rows;

    public float RankPoints { get; internal set; }

    public HonorRankInfo Rank { get; internal set; }

    public HonorRankInfo HighestRank { get; internal set; }

    /// <summary>The honor week begin day this state was built under; a weekly result for a week at or before it is already in the loaded row.</summary>
    public uint LoadedWeek { get; internal set; }

    public uint Standing { get; internal set; }

    public uint LastWeekHk { get; internal set; }

    public float LastWeekCp { get; internal set; }

    public int StoredHk { get; internal set; }

    public int StoredDk { get; internal set; }

    /// <summary>Lifetime honorable kills as of the last update: stored plus this week's.</summary>
    public int TotalHk { get; internal set; }

    /// <summary>Lifetime dishonorable kills as of the last update: stored plus every row.</summary>
    public int TotalDk { get; internal set; }

    /// <summary>The persisted PvP flag bits (documented in the PvP flag slice); kept so saves do not drop them.</summary>
    public byte PvpFlags { get; internal set; }

    public bool CityProtector { get; internal set; }

    internal void AddRow(HonorCpRecord row) => _rows.Add(row);

    internal void ClearRows() => _rows.Clear();

    internal void RemoveRowsBefore(uint day) => _rows.RemoveAll(r => r.Date < day);

    internal CharacterHonorState Snapshot()
        => new(RankPoints, HighestRank.Rank, Standing, LastWeekHk, LastWeekCp, StoredHk, StoredDk, PvpFlags, CityProtector);
}
