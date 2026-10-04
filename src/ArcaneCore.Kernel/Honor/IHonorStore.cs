using System.Globalization;

namespace ArcaneCore.Kernel.Honor;

/// <summary>vmangos HonorType (HonorMgr.h:100-107): why a contribution point row exists.</summary>
public enum HonorKind : byte
{
    Honorable = 1,
    Dishonorable = 2,
    Bonus = 3,
    Quest = 4,
    Other = 5,
}

/// <summary>
/// One contribution point row (vmangos character_honor_cp: victim_type, victim_id, cp, date, type).
/// <see cref="Date"/> is the game day; <see cref="VictimType"/> is 0 (no source), 3 (unit) or 4 (player).
/// </summary>
public sealed record HonorCpRecord(byte VictimType, uint VictimId, float Cp, uint Date, byte Type);

/// <summary>
/// The per-character honor state that vmangos keeps in columns of <c>characters</c>
/// (honor_rank_points, honor_highest_rank, honor_standing, honor_last_week_hk, honor_last_week_cp,
/// honor_stored_hk, honor_stored_dk) plus the PvP flag bits and the City Protector flag (extra_flags 0x0400).
/// </summary>
public sealed record CharacterHonorState(
    float RankPoints,
    byte HighestRank,
    uint Standing,
    uint LastWeekHk,
    float LastWeekCp,
    int StoredHk,
    int StoredDk,
    byte PvpFlags,
    bool CityProtector)
{
    /// <summary>The state of a character with no stored honor.</summary>
    public static CharacterHonorState Empty { get; } = new(0f, 0, 0, 0, 0f, 0, 0, 0, false);
}

/// <summary>A character's stored honor: the state and every contribution point row, oldest first.</summary>
public sealed record CharacterHonorData(CharacterHonorState State, IReadOnlyList<HonorCpRecord> Cp)
{
    public static CharacterHonorData Empty { get; } = new(CharacterHonorState.Empty, []);
}

/// <summary>The weekly maintenance bookkeeping (vmangos saved_variables honor_*_maintenance_day, honor_maintenance_marker).</summary>
public sealed record HonorMaintenanceState(uint LastDay, uint NextDay, bool Marker);

/// <summary>
/// One character's inputs to the weekly calculation (vmangos HonorMaintenancer::LoadWeeklyScores,
/// HonorMgr.cpp:62-102): honorable and dishonorable kills and the contribution points of the week, plus the
/// character's level, account and race.
/// </summary>
public sealed record HonorWeeklyScore(
    int CharacterId,
    byte Level,
    int AccountId,
    byte Race,
    float OldRankPoints,
    byte HighestRank,
    uint Hk,
    uint Dk,
    float Cp);

/// <summary>One character's result of the weekly calculation (vmangos HonorMaintenancer::FlushRankPoints).</summary>
public sealed record HonorRankUpdate(
    int CharacterId,
    float RankPoints,
    uint Standing,
    byte HighestRank,
    uint WeekHk,
    uint WeekDk,
    float WeekCp);

/// <summary>
/// Everything one maintenance period changes, applied in a single transaction: every standing is cleared, the
/// <see cref="Updates"/> are written (stored HK/DK grow by the week's counts, last week's values are set),
/// contribution rows dated before <see cref="DeleteCpBefore"/> are dropped, and the maintenance days move.
/// <see cref="CityProtectors"/> null leaves the City Protector flag alone; otherwise exactly those characters
/// keep it.
/// </summary>
public sealed record HonorMaintenanceBatch(
    IReadOnlyList<HonorRankUpdate> Updates,
    uint DeleteCpBefore,
    HonorMaintenanceState NewState,
    IReadOnlyCollection<int>? CityProtectors = null);

/// <summary>
/// vmangos persists contribution points and rank points with printf <c>%.1f</c> (HonorMgr.cpp:719-720,
/// 760-763). The same one-decimal rounding is applied in the store so every provider reads back identical
/// floats. A tie on the exact binary value (x.25, x.75) rounds to even, as glibc printf does (.NET exact formatting).
/// </summary>
public static class HonorRounding
{
    public static float OneDecimal(float value)
    {
        if (!float.IsFinite(value))
        {
            return 0f;
        }

        return float.Parse(value.ToString("F1", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }
}

/// <summary>Persistence of honor (characters schema, docs/areas/honor.md).</summary>
public interface IHonorStore
{
    /// <summary>The stored honor of a character; <see cref="CharacterHonorData.Empty"/> when it has none.</summary>
    Task<CharacterHonorData> LoadAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Insert or replace the state (rank and contribution points rounded to one decimal); a missing character is ignored.</summary>
    Task SaveStateAsync(int characterId, CharacterHonorState state, CancellationToken cancellationToken = default);

    /// <summary>Append contribution rows (cp rounded to one decimal); a missing character is ignored.</summary>
    Task AppendCpAsync(int characterId, IReadOnlyList<HonorCpRecord> rows, CancellationToken cancellationToken = default);

    /// <summary>HonorMgr::Reset: delete the contribution rows and the state of a character.</summary>
    Task ResetAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Remove every honor row of a character (a reused id at creation).</summary>
    Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove the honor rows of an id that has no <c>characters</c> row: the queued removal after a deletion,
    /// which must not wipe a character recreated with the same id before it executes.
    /// </summary>
    Task DeleteDeletedCharacterAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>The maintenance bookkeeping, or null before the first run.</summary>
    Task<HonorMaintenanceState?> GetMaintenanceAsync(CancellationToken cancellationToken = default);

    /// <summary>Store the maintenance bookkeeping.</summary>
    Task SaveMaintenanceAsync(HonorMaintenanceState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// The weekly inputs of every character with contribution rows dated between the two days (inclusive) or with
    /// rank points above zero. Dishonorable kills are counted but add nothing to the contribution points.
    /// </summary>
    Task<IReadOnlyList<HonorWeeklyScore>> ListWeeklyScoresAsync(uint weekBeginDay, uint weekEndDay, CancellationToken cancellationToken = default);

    /// <summary>Apply one maintenance period atomically; nothing changes if any step fails.</summary>
    Task ApplyMaintenanceAsync(HonorMaintenanceBatch batch, CancellationToken cancellationToken = default);
}
