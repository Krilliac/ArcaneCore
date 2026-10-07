using ArcaneCore.Game.Quests;

namespace ArcaneCore.Game.WorldState.Events;

/// <summary>
/// Event quests (vmangos <c>GameEventMgr::UpdateEventQuests</c>, GameEventMgr.cpp:1023-1036, and the load rule at :578): every quest
/// listed in <c>game_event_quest</c> is inactive from the moment the tables load and becomes active while its event runs. An
/// inactive quest is not offered by its NPC, not in the NPC's quest menu and cannot be accepted; a quest already in a player's log
/// keeps its state when the event ends (only the active flag changes).
/// <para>
/// vmangos writes <c>SetQuestActiveState(Activate)</c> per event, so a quest listed under two events is switched off when
/// the first of them stops even if the other still runs. Here a quest is active while ANY event it is listed under runs
/// (the state is derived from the running set, like the spawn gate); the data has no such quest, so the difference is theoretical.
/// </para>
/// <para>
/// The quest objects belong to <see cref="QuestStore"/>, which the quest feature may build or rebuild at any time, so the store is
/// looked up on every use and <see cref="Resync"/> re-applies the state when it changed. A quest-template reload swaps the
/// store, so the new quest objects carry their own state until the next <see cref="Resync"/>, which the world feature runs on every world
/// tick (pinned by <c>GameEventQuestWorldTests.AQuestTemplateReload_DoesNotLoseTheEventState_TheNextWorldTickReappliesIt</c>).
/// </para>
/// </summary>
public sealed class GameEventQuests : IGameEventEffects
{
    private readonly IGameEventState _state;
    private readonly Func<QuestStore?> _store;
    private readonly Dictionary<ushort, uint[]> _questsByEvent;
    private readonly Dictionary<uint, ushort[]> _eventsByQuest;
    private QuestStore? _applied;

    public GameEventQuests(IGameEventState state, GameEventRows rows, Func<QuestStore?> store)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        ArgumentNullException.ThrowIfNull(rows);
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _questsByEvent = rows.Quests.ToDictionary(p => p.Key, p => p.Value.ToArray());
        _eventsByQuest = _questsByEvent
            .SelectMany(p => p.Value.Select(q => (Quest: q, Event: p.Key)))
            .GroupBy(t => t.Quest)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Event).ToArray());
    }

    /// <summary>The quest ids listed in some event.</summary>
    public IReadOnlyCollection<uint> ListedQuests => _eventsByQuest.Keys;

    /// <summary>
    /// Put every listed quest of the current store into the state its events dictate (inactive when none of them runs), if the store
    /// is not the one already handled. Returns the quest ids that are listed but have no quest template (vmangos skips them with an error).
    /// </summary>
    public IReadOnlyList<uint> Resync()
    {
        QuestStore? store = _store();
        if (store is null || store.Count == 0 || ReferenceEquals(store, _applied))
        {
            return [];
        }

        _applied = store;
        var missing = new List<uint>();
        foreach ((uint questId, ushort[] events) in _eventsByQuest)
        {
            if (store.Get(questId) is { } quest)
            {
                quest.SetEventState(events.Any(_state.IsActiveEvent));
            }
            else
            {
                missing.Add(questId);
            }
        }

        return missing;
    }

    /// <summary>Give every listed quest back to its own <c>Method</c> (the event system is going away or being replaced).</summary>
    public void Release()
    {
        if (_applied is not { } store)
        {
            return;
        }

        foreach (uint questId in _eventsByQuest.Keys)
        {
            store.Get(questId)?.SetEventState(null);
        }

        _applied = null;
    }

    public void UpdateEventQuests(ushort eventId, bool activate)
    {
        Resync();
        QuestStore? store = _store();
        if (store is null || !_questsByEvent.TryGetValue(eventId, out uint[]? quests))
        {
            return;
        }

        foreach (uint questId in quests)
        {
            store.Get(questId)?.SetEventState(_eventsByQuest[questId].Any(_state.IsActiveEvent));
        }
    }
}
