namespace ArcaneCore.Kernel.Quests;

/// <summary>
/// A character's progress on one quest — a row of the characters database's
/// <c>character_queststatus</c> (vmangos character_queststatus: guid, quest, status, rewarded,
/// explored, timer, mob_count1-4, item_count1-4, reward_choice). <see cref="Timer"/> is the Unix time (seconds)
/// at which a timed quest fails, 0 when untimed (vmangos stores the absolute end time too).
/// </summary>
public sealed record CharacterQuestStatus(
    int CharacterId,
    uint Quest,
    byte Status,
    bool Rewarded,
    bool Explored,
    long Timer,
    uint MobCount1,
    uint MobCount2,
    uint MobCount3,
    uint MobCount4,
    uint ItemCount1,
    uint ItemCount2,
    uint ItemCount3,
    uint ItemCount4,
    uint RewardChoice);

/// <summary>What the quest and taxi services keep per character (loaded at login).</summary>
public sealed record CharacterQuestData(IReadOnlyList<CharacterQuestStatus> Quests, IReadOnlyList<uint> TaxiMask)
{
    public static CharacterQuestData Empty { get; } = new([], []);
}

/// <summary>
/// Persistence of quest progress and known flight paths. Quest writes are deltas (the rows that
/// changed), applied in order by the world daemon's quest save queue.
/// </summary>
public interface ICharacterQuestStore
{
    Task<CharacterQuestData> LoadAsync(int characterId, CancellationToken cancellationToken = default);

    Task SaveQuestsAsync(int characterId, IReadOnlyList<CharacterQuestStatus> upserts, CancellationToken cancellationToken = default);

    /// <summary>Replace the known taxi node mask (8 words, vmangos PlayerTaxi::m_taximask).</summary>
    Task SaveTaxiMaskAsync(int characterId, IReadOnlyList<uint> mask, CancellationToken cancellationToken = default);
}
