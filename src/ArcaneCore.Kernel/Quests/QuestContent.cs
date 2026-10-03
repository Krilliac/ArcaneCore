namespace ArcaneCore.Kernel.Quests;

/// <summary>
/// One row of <c>creature_questrelation</c> (the creature entry starts the quest) or
/// <c>creature_involvedrelation</c> (the creature entry ends it) — vmangos/cmangos shape:
/// <c>id</c> = creature_template entry, <c>quest</c> = quest_template entry.
/// </summary>
public sealed class CreatureQuestRelation
{
    /// <summary>creature_questrelation.id / creature_involvedrelation.id (creature entry).</summary>
    public uint Id { get; init; }

    /// <summary>creature_questrelation.quest / creature_involvedrelation.quest.</summary>
    public uint Quest { get; init; }
}

/// <summary>Everything the quest system reads from the world database at startup.</summary>
public sealed record QuestContent(
    IReadOnlyList<QuestTemplate> Templates,
    IReadOnlyList<CreatureQuestRelation> Starters,
    IReadOnlyList<CreatureQuestRelation> Enders)
{
    /// <summary>No quests (no world-content database configured for quests).</summary>
    public static QuestContent Empty { get; } = new([], [], []);
}

/// <summary>
/// Reads the quest content tables. Called once at startup off the world thread; the result is
/// turned into immutable in-memory stores (ROADMAP: no database reads on the world thread).
/// </summary>
public interface IQuestContentStore
{
    Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default);
}
