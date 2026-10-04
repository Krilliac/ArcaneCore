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

    /// <summary>
    /// <c>gameobject_questrelation</c>: the game object entry <see cref="CreatureQuestRelation.Id"/> starts
    /// <see cref="CreatureQuestRelation.Quest"/> (same row shape as the creature relations; the two entry
    /// spaces are independent, vmangos keeps separate GO and creature relation maps).
    /// </summary>
    public IReadOnlyList<CreatureQuestRelation> GameObjectStarters { get; init; } = [];

    /// <summary><c>gameobject_involvedrelation</c>: the game object entry ends the quest.</summary>
    public IReadOnlyList<CreatureQuestRelation> GameObjectEnders { get; init; } = [];

    /// <summary>
    /// <c>areatrigger_involvedrelation</c> (vmangos/classic-db): stepping on the area trigger <see cref="CreatureQuestRelation.Id"/>
    /// credits the exploration objective of <see cref="CreatureQuestRelation.Quest"/> (same row shape as the creature relations).
    /// </summary>
    public IReadOnlyList<CreatureQuestRelation> AreaTriggerQuests { get; init; } = [];
}

/// <summary>
/// Reads the quest content tables. Called once at startup off the world thread; the result is
/// turned into immutable in-memory stores (ROADMAP: no database reads on the world thread).
/// </summary>
public interface IQuestContentStore
{
    Task<QuestContent> LoadAsync(CancellationToken cancellationToken = default);
}
