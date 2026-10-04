using System.Collections.Frozen;
using ArcaneCore.Kernel.Quests;

namespace ArcaneCore.Game.Quests;

/// <summary>
/// The immutable quest content (built once at startup, read on the world thread): quests by
/// id, creature starter/ender relations by creature entry, and exclusive groups — the
/// relevant parts of vmangos ObjectMgr::LoadQuests / LoadCreatureQuestRelations.
/// </summary>
public sealed class QuestStore
{
    private readonly FrozenDictionary<uint, Quest> _quests;
    private readonly FrozenDictionary<uint, uint[]> _starters;
    private readonly FrozenDictionary<uint, uint[]> _enders;
    private readonly FrozenDictionary<uint, uint[]> _gameObjectStarters;
    private readonly FrozenDictionary<uint, uint[]> _gameObjectEnders;
    private readonly FrozenDictionary<uint, uint[]> _areaTriggerQuests;
    private readonly FrozenDictionary<uint, uint[]> _areaTriggerOfQuest;
    private readonly FrozenDictionary<int, uint[]> _exclusiveGroups;
    private readonly bool _hasRewXp;

    public QuestStore(QuestContent content)
    {
        var quests = new Dictionary<uint, Quest>();
        foreach (QuestTemplate t in content.Templates)
        {
            quests[t.Entry] = new Quest(t);
        }

        var exclusive = new Dictionary<int, List<uint>>();
        foreach (Quest q in quests.Values.OrderBy(q => q.Id))
        {
            QuestTemplate t = q.Template;

            // NextQuestInChain to a missing quest is cleared; otherwise the target learns its predecessor.
            if (t.NextQuestInChain != 0 && quests.TryGetValue(t.NextQuestInChain, out Quest? next))
            {
                q.NextQuestInChain = t.NextQuestInChain;
                next.AddPrevChainQuest(q.Id);
            }

            // PrevQuestId: kept unless missing or itself a breadcrumb (vmangos logs and skips both).
            if (t.PrevQuestId != 0 && quests.TryGetValue((uint)Math.Abs(t.PrevQuestId), out Quest? prev)
                && prev.Template.BreadcrumbForQuestId == 0)
            {
                q.AddPrevQuest(t.PrevQuestId);
            }

            // NextQuestId: the target gets this quest as a (signed) previous quest.
            if (t.NextQuestId != 0 && quests.TryGetValue((uint)Math.Abs(t.NextQuestId), out Quest? nextQuest))
            {
                nextQuest.AddPrevQuest(t.NextQuestId < 0 ? -(int)q.Id : (int)q.Id);
            }

            if (t.ExclusiveGroup != 0)
            {
                if (!exclusive.TryGetValue(t.ExclusiveGroup, out List<uint>? members))
                {
                    exclusive[t.ExclusiveGroup] = members = [];
                }

                members.Add(q.Id);
            }

            q.BreadcrumbForQuestId = t.BreadcrumbForQuestId != 0 && quests.ContainsKey(t.BreadcrumbForQuestId)
                ? t.BreadcrumbForQuestId
                : 0;
        }

        // Breadcrumb loops are cut; every target learns the breadcrumbs that lead to it.
        foreach (Quest q in quests.Values.OrderBy(q => q.Id))
        {
            var seen = new HashSet<uint>();
            Quest current = q;
            uint target = current.BreadcrumbForQuestId;
            while (target != 0)
            {
                if (!seen.Add(current.Id))
                {
                    current.BreadcrumbForQuestId = 0;
                    break;
                }

                current = quests[target];
                current.AddDependentBreadcrumb(q.Id);
                target = current.BreadcrumbForQuestId;
            }
        }

        _hasRewXp = quests.Values.Any(q => q.Template.RewXP > 0);
        _quests = quests.ToFrozenDictionary();
        _exclusiveGroups = exclusive.ToFrozenDictionary(p => p.Key, p => p.Value.ToArray());
        _starters = Group(content.Starters, quests);
        _enders = Group(content.Enders, quests);
        _gameObjectStarters = Group(content.GameObjectStarters, quests);
        _gameObjectEnders = Group(content.GameObjectEnders, quests);

        // areatrigger_involvedrelation: a trigger credits its quests (vmangos LoadQuestAreaTriggers skips a quest that is not loaded);
        // the reverse index answers "has this quest an area trigger" without scanning the table.
        _areaTriggerQuests = Group(content.AreaTriggerQuests, quests);
        _areaTriggerOfQuest = _areaTriggerQuests
            .SelectMany(p => p.Value.Select(quest => (quest, trigger: p.Key)))
            .GroupBy(p => p.quest)
            .ToFrozenDictionary(g => g.Key, g => g.Select(p => p.trigger).ToArray());
    }

    public static QuestStore Empty { get; } = new(QuestContent.Empty);

    public int Count => _quests.Count;

    /// <summary>Every loaded quest (unordered), for startup reports.</summary>
    public IEnumerable<Quest> All => _quests.Values;

    /// <summary>
    /// The templates this store was built from, by quest id. The live reload reads them to keep a
    /// quest whose row has left the table, as vmangos does (a reload never erases a loaded template).
    /// </summary>
    public IEnumerable<QuestTemplate> Templates => _quests.Values.Select(q => q.Template);

    /// <summary>Whether any loaded quest carries a RewXP value (a vmangos-style dataset; classic-db has no such column).</summary>
    public bool HasRewXpColumn => _hasRewXp;

    public Quest? Get(uint questId) => questId != 0 ? _quests.GetValueOrDefault(questId) : null;

    /// <summary>Quests the creature entry starts (creature_questrelation), in table order.</summary>
    public IReadOnlyList<uint> StartersOf(uint creatureEntry) => _starters.GetValueOrDefault(creatureEntry) ?? [];

    /// <summary>Quests the creature entry ends (creature_involvedrelation), in table order.</summary>
    public IReadOnlyList<uint> EndersOf(uint creatureEntry) => _enders.GetValueOrDefault(creatureEntry) ?? [];

    /// <summary>Quests the game object entry starts (gameobject_questrelation), in table order.</summary>
    public IReadOnlyList<uint> GameObjectStartersOf(uint gameObjectEntry) => _gameObjectStarters.GetValueOrDefault(gameObjectEntry) ?? [];

    /// <summary>Quests the game object entry ends (gameobject_involvedrelation), in table order.</summary>
    public IReadOnlyList<uint> GameObjectEndersOf(uint gameObjectEntry) => _gameObjectEnders.GetValueOrDefault(gameObjectEntry) ?? [];

    /// <summary>Quests the area trigger credits (areatrigger_involvedrelation), in table order. World thread, no allocation.</summary>
    public IReadOnlyList<uint> AreaTriggerQuestsOf(uint triggerId) => _areaTriggerQuests.GetValueOrDefault(triggerId) ?? [];

    /// <summary>Whether an area trigger row credits <paramref name="questId"/> (the exploration objective has a source).</summary>
    public bool HasAreaTrigger(uint questId) => _areaTriggerOfQuest.ContainsKey(questId);

    /// <summary>vmangos Object::HasQuest for a creature.</summary>
    public bool Starts(uint creatureEntry, uint questId) => StartersOf(creatureEntry).Contains(questId);

    /// <summary>vmangos Object::HasInvolvedQuest for a creature.</summary>
    public bool Ends(uint creatureEntry, uint questId) => EndersOf(creatureEntry).Contains(questId);

    /// <summary>Members of an exclusive group (vmangos m_ExclusiveQuestGroups).</summary>
    public IReadOnlyList<uint> ExclusiveGroup(int group) => _exclusiveGroups.GetValueOrDefault(group) ?? [];

    private static FrozenDictionary<uint, uint[]> Group(IEnumerable<CreatureQuestRelation> relations, Dictionary<uint, Quest> quests)
        => relations.Where(r => quests.ContainsKey(r.Quest))
            .GroupBy(r => r.Id)
            .ToFrozenDictionary(g => g.Key, g => g.Select(r => r.Quest).Distinct().ToArray());
}
