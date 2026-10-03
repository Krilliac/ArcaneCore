namespace ArcaneCore.Kernel.Reputation;

/// <summary>
/// One stored faction state (vmangos character_reputation: guid, faction, standing, flags).
/// <see cref="Standing"/> is relative to the race/class base reputation from Faction.dbc, as in
/// vmangos FactionState::Standing; the base is recomputed from the DBC at every load.
/// </summary>
public sealed record CharacterReputationRow(int CharacterId, uint Faction, int Standing, uint Flags);

/// <summary>A character's stored reputation: changed faction rows and the watched list slot (-1 none).</summary>
public sealed record CharacterReputationData(IReadOnlyList<CharacterReputationRow> Factions, int WatchedFaction)
{
    public static CharacterReputationData Empty { get; } = new([], -1);
}

/// <summary>Persistence of character reputation (characters schema v7, docs/integration/reputation.md).</summary>
public interface ICharacterReputationStore
{
    Task<CharacterReputationData> LoadAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Insert or replace the given faction rows; a missing character is ignored (deleted while queued).</summary>
    Task SaveFactionsAsync(int characterId, IReadOnlyList<CharacterReputationRow> upserts, CancellationToken cancellationToken = default);

    /// <summary>Persist the watched reputation-list slot (-1 for none); a missing character is ignored.</summary>
    Task SaveWatchedFactionAsync(int characterId, int watchedFaction, CancellationToken cancellationToken = default);

    /// <summary>Remove every reputation row of a character (a reused id at creation).</summary>
    Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove the reputation rows of a character id that has no <c>characters</c> row: the queued
    /// removal after a deletion, which must not wipe a character recreated with the same id before
    /// it executes (docs/integration/character-delete.md).
    /// </summary>
    Task DeleteDeletedCharacterAsync(int characterId, CancellationToken cancellationToken = default);
}

/// <summary>
/// One creature_onkill_reputation row (vmangos ReputationOnKillEntry; cmangos/vmangos world table
/// columns creature_id, RewOnKillRepFaction1/2, MaxStanding1/2, IsTeamAward1/2, RewOnKillRepValue1/2, TeamDependent).
/// </summary>
public sealed record ReputationOnKillEntry(
    uint CreatureEntry,
    uint Faction1,
    uint Faction2,
    byte MaxStanding1,
    bool IsTeamAward1,
    int Value1,
    byte MaxStanding2,
    bool IsTeamAward2,
    int Value2,
    bool TeamDependent);

/// <summary>
/// Source of kill-reputation content. No world schema version is reserved for it in this
/// round, so the daemon resolves it from DI; absent means no creature grants reputation.
/// </summary>
public interface IReputationOnKillSource
{
    Task<IReadOnlyList<ReputationOnKillEntry>> LoadAsync(CancellationToken cancellationToken = default);
}
