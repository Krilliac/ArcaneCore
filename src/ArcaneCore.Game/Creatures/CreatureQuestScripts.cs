using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// A creature script that reacts to a quest taken from its creature (ScriptDev2 <c>pQuestAcceptNPC</c>, called by mangos-classic
/// Player::AddQuest before the quest's DB start script, Player.cpp:12517-12535): an escort sets off with its player, for example.
/// </summary>
public interface IQuestScriptAI
{
    /// <summary>The player took <paramref name="questId"/> from this creature (the quest is in the player's log).</summary>
    void OnQuestAccept(Player player, uint questId);
}

/// <summary>
/// Starts the quest and gossip DB scripts of the quest service on the creature system of the player's map (mangos-classic
/// Map::ScriptsStart): <c>quest_template.StartScript</c> when a quest is taken from a creature or game object (Player::AddQuest,
/// Player.cpp:12517-12535, after the giver's <see cref="IQuestScriptAI"/>), <c>quest_template.CompleteScript</c> when one is rewarded
/// (Player::RewardQuest, :12695-12713), and the gossip scripts (<see cref="QuestNpcServices.GossipScriptStarted"/>). The giver is the
/// script's source and the player its target; the gossip scripts say which is which. A quest taken from an item, or shared by another
/// player, has no giver on the map and runs no script.
/// </summary>
public static class CreatureQuestScripts
{
    private static readonly ConditionalWeakTable<CreatureAiContent, IReadOnlySet<uint>> s_explored = new();

    /// <summary>
    /// Subscribe to <paramref name="npcs"/>; <paramref name="systemOf"/> finds the creature system of a map (null: none, and nothing runs).
    /// With <paramref name="contentOf"/> (the loaded creature content, read at each query) the quest service also learns which
    /// exploration/event quests the scripts complete (<see cref="QuestNpcServices.ScriptCreditedQuests"/>): the QUEST_EXPLORED quests of
    /// the content's DB scripts and the escort quests of <paramref name="factory"/>'s entry scripts. Returns the unsubscription.
    /// </summary>
    public static IDisposable Attach(QuestNpcServices npcs, Func<Map, CreatureMapSystem?> systemOf, Func<CreatureContent?>? contentOf = null,
        Func<CreatureAiFactory?>? factory = null)
    {
        ArgumentNullException.ThrowIfNull(npcs);
        ArgumentNullException.ThrowIfNull(systemOf);
        if (contentOf is not null)
        {
            npcs.ScriptCreditedQuests = () => CreditedEventQuests(contentOf(), factory?.Invoke());
        }

        void Accepted(Player player, ObjectGuid giver, Quest quest) => OnQuestAccepted(systemOf, player, giver, quest);
        void Rewarded(Player player, ObjectGuid giver, Quest quest) => OnQuestRewarded(systemOf, player, giver, quest);
        void Gossip(Player player, ObjectGuid npc, uint scriptId, bool playerIsSource) => OnGossipScript(systemOf, player, npc, scriptId, playerIsSource);
        npcs.QuestAccepted += Accepted;
        npcs.QuestRewarded += Rewarded;
        npcs.GossipScriptStarted += Gossip;
        return new Subscription(() =>
        {
            npcs.QuestAccepted -= Accepted;
            npcs.QuestRewarded -= Rewarded;
            npcs.GossipScriptStarted -= Gossip;
        });
    }

    private static void OnQuestAccepted(Func<Map, CreatureMapSystem?> systemOf, Player player, ObjectGuid giverGuid, Quest quest)
    {
        if (Giver(player, giverGuid) is not { } giver)
        {
            return;
        }

        if (giver is Creature { AI: IQuestScriptAI script })
        {
            script.OnQuestAccept(player, quest.Id);
        }

        if (quest.Template.StartScript != 0 && player.Map is { } map && systemOf(map) is { } system)
        {
            system.StartDbScript(DbScriptKind.QuestStart, quest.Template.StartScript, giver, player);
        }
    }

    private static void OnQuestRewarded(Func<Map, CreatureMapSystem?> systemOf, Player player, ObjectGuid giverGuid, Quest quest)
    {
        // No ScriptDev2 OnQuestRewarded script of this server claims a quest, so the DB script always runs (Player.cpp:12712-12713).
        if (quest.Template.CompleteScript != 0 && Giver(player, giverGuid) is { } giver && player.Map is { } map && systemOf(map) is { } system)
        {
            system.StartDbScript(DbScriptKind.QuestEnd, quest.Template.CompleteScript, giver, player);
        }
    }

    private static void OnGossipScript(Func<Map, CreatureMapSystem?> systemOf, Player player, ObjectGuid npcGuid, uint scriptId, bool playerIsSource)
    {
        if (Giver(player, npcGuid) is not { } npc || player.Map is not { } map || systemOf(map) is not { } system)
        {
            return;
        }

        if (playerIsSource)
        {
            system.StartDbScript(DbScriptKind.Gossip, scriptId, player, npc);
        }
        else
        {
            system.StartDbScript(DbScriptKind.Gossip, scriptId, npc, player);
        }
    }

    /// <summary>
    /// The exploration/event quests the creature scripts complete: every SCRIPT_COMMAND_QUEST_EXPLORED (7) quest of the DB scripts, the
    /// relays included (cached per content), and the escort quests of the entry scripts.
    /// </summary>
    public static IReadOnlyCollection<uint> CreditedEventQuests(CreatureContent? content, CreatureAiFactory? factory)
    {
        IReadOnlySet<uint> explored = content is null ? new HashSet<uint>() : s_explored.GetValue(content.Ai, static ai =>
        {
            const uint QuestExplored = 7;
            var quests = new HashSet<uint>(ai.RelayScripts.AllSteps.Where(s => s.Command == QuestExplored).Select(s => s.DataLong));
            quests.UnionWith(ai.DbScripts.AllSteps.Where(s => s.Step.Command == QuestExplored).Select(s => s.Step.DataLong));
            quests.Remove(0);
            return quests;
        });
        return factory is null ? [.. explored] : [.. explored.Union(factory.ScriptedEventQuests)];
    }

    /// <summary>The creature or game object on the player's map; anything else (an item, a player) gives no script.</summary>
    private static WorldObject? Giver(Player player, ObjectGuid guid)
        => player.Map?.FindObject(guid) is { } found && found is Creature or GameObject ? found : null;

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
