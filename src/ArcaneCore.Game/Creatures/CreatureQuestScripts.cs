using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
/// The quest hooks of a ScriptName-bound script that has no AI of its own (<see cref="CreatureAiFactory.RegisterNpcScript"/>): ScriptDev2
/// <c>pQuestAcceptNPC</c> and <c>pQuestRewardedNPC</c> of every creature whose template carries that ScriptName. World thread.
/// </summary>
public interface INpcQuestScript
{
    /// <summary>The player took <paramref name="questId"/> from <paramref name="creature"/> (the quest is in the player's log). Nothing by default.</summary>
    void OnQuestAccept(Player player, Creature creature, uint questId)
    {
    }

    /// <summary>
    /// The player was rewarded <paramref name="questId"/> by <paramref name="creature"/> (the reward is committed). An exception is logged and
    /// recorded in the <see cref="FaultLedger"/> and the reward carries on (see <see cref="CreatureAI.OnQuestRewarded"/>). Nothing by default.
    /// </summary>
    void OnQuestRewarded(Player player, Creature creature, uint questId)
    {
    }
}

/// <summary>
/// Starts the quest and gossip DB scripts of the quest service on the creature system of the player's map (mangos-classic
/// Map::ScriptsStart): <c>quest_template.StartScript</c> when a quest is taken from a creature or game object (Player::AddQuest,
/// Player.cpp:12517-12535, after the giver's <see cref="IQuestScriptAI"/>), <c>quest_template.CompleteScript</c> when one is rewarded
/// (Player::RewardQuest, :12695-12713), and the gossip scripts (<see cref="QuestNpcServices.GossipScriptStarted"/>). The giver is the
/// script's source and the player its target; the gossip scripts say which is which. A quest shared by another player has that player as
/// its source (WorldSession::HandleQuestgiverAcceptQuestOpcode passes the sharer to AddQuest). A quest started from an item has no world
/// source: as in cmangos ScriptAction::HandleScriptStep (DBScripts/ScriptMgr.cpp:1720-1760), where an item is no world object, only the
/// steps whose buddy search finds a source run, with the player as target. vmangos runs no start script for an item at all
/// (Player::AddQuest, Objects/Player.cpp:12889-12891).
/// </summary>
public static class CreatureQuestScripts
{
    private static readonly ConditionalWeakTable<CreatureAiContent, IReadOnlySet<uint>> s_explored = new();

    /// <summary>
    /// Subscribe to <paramref name="npcs"/>; <paramref name="systemOf"/> finds the creature system of a map (null: none, and nothing runs).
    /// With <paramref name="contentOf"/> (the loaded creature content, read at each query) the quest service also learns which
    /// exploration/event quests the scripts complete (<see cref="QuestNpcServices.ScriptCreditedQuests"/>): the QUEST_EXPLORED quests of
    /// the content's DB scripts and the escort quests of <paramref name="factory"/>'s entry scripts. With <paramref name="factory"/> the
    /// ScriptName-bound gossip and quest handlers of <see cref="CreatureAiFactory.RegisterNpcScript"/> are reached too: the gossip handler
    /// through <see cref="QuestNpcServices.GossipByScriptName"/>, the quest handler beside the creature AI's own hook. A throwing accept or reward hook is
    /// logged to <paramref name="logger"/> and recorded in <see cref="FaultLedger.Shared"/>; the reward carries on. Returns the unsubscription.
    /// </summary>
    public static IDisposable Attach(QuestNpcServices npcs, Func<Map, CreatureMapSystem?> systemOf, Func<CreatureContent?>? contentOf = null,
        Func<CreatureAiFactory?>? factory = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(npcs);
        ArgumentNullException.ThrowIfNull(systemOf);
        if (contentOf is not null)
        {
            npcs.ScriptCreditedQuests = () => CreditedEventQuests(contentOf(), factory?.Invoke());
        }

        ILogger log = logger ?? NullLogger.Instance;
        Func<string, INpcGossipScript?>? gossipOf = factory is null ? null : name => factory()?.NpcGossipOf(name);
        if (gossipOf is not null)
        {
            npcs.GossipByScriptName = gossipOf;
        }

        void Accepted(Player player, ObjectGuid giver, Quest quest) => OnQuestAccepted(systemOf, factory, log, player, giver, quest);
        void Rewarded(Player player, ObjectGuid giver, Quest quest) => OnQuestRewarded(systemOf, factory, log, player, giver, quest);
        void Gossip(Player player, ObjectGuid npc, uint scriptId, bool playerIsSource) => OnGossipScript(systemOf, log, player, npc, scriptId, playerIsSource);
        npcs.QuestAccepted += Accepted;
        npcs.QuestRewarded += Rewarded;
        npcs.GossipScriptStarted += Gossip;
        return new Subscription(() =>
        {
            npcs.QuestAccepted -= Accepted;
            npcs.QuestRewarded -= Rewarded;
            npcs.GossipScriptStarted -= Gossip;
            if (gossipOf is not null && ReferenceEquals(npcs.GossipByScriptName, gossipOf))
            {
                npcs.GossipByScriptName = null;
            }
        });
    }

    private static void OnQuestAccepted(Func<Map, CreatureMapSystem?> systemOf, Func<CreatureAiFactory?>? factory, ILogger log, Player player,
        ObjectGuid giverGuid, Quest quest)
    {
        if (giverGuid.High == HighGuid.Item)
        {
            if (quest.Template.StartScript != 0 && player.Map is { } itemMap && systemOf(itemMap) is { } itemSystem)
            {
                try
                {
                    itemSystem.StartDbScript(DbScriptKind.QuestStart, quest.Template.StartScript, source: null, player);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("creature-quest-script", ex);
                    log.LogError(ex, "quest start script {ScriptId} failed for item quest {QuestId}", quest.Template.StartScript, quest.Id);
                }
            }

            return;
        }

        if (Giver(player, giverGuid) is not { } giver)
        {
            return;
        }

        // The quest is already in the log when the hooks run: a script's fault must not skip the DB StartScript, the later subscribers, the
        // party fan-out, the source spell, nor reach the caller's exception handler (which kicks the session).
        if (giver is Creature { AI: IQuestScriptAI script } aiCreature)
        {
            try
            {
                script.OnQuestAccept(player, quest.Id);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogHookFault(log, AcceptSite, ex, script.GetType().Name, aiCreature, quest.Id);
            }
        }

        if (giver is Creature creature && ScriptQuestHandler(factory, creature) is { } registered)
        {
            try
            {
                registered.OnQuestAccept(player, creature, quest.Id);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogHookFault(log, AcceptSite, ex, creature.Template.ScriptName ?? string.Empty, creature, quest.Id);
            }
        }

        if (quest.Template.StartScript != 0 && player.Map is { } map && systemOf(map) is { } system)
        {
            try
            {
                system.StartDbScript(DbScriptKind.QuestStart, quest.Template.StartScript, giver, player);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("creature-quest-script", ex);
                log.LogError(ex, "quest start script {ScriptId} failed for quest {QuestId}", quest.Template.StartScript, quest.Id);
            }
        }
    }

    private static INpcQuestScript? ScriptQuestHandler(Func<CreatureAiFactory?>? factory, Creature creature)
        => factory?.Invoke() is { } f && !string.IsNullOrEmpty(creature.Template.ScriptName) ? f.NpcQuestOf(creature.Template.ScriptName) : null;

    private static void OnQuestRewarded(Func<Map, CreatureMapSystem?> systemOf, Func<CreatureAiFactory?>? factory, ILogger log, Player player,
        ObjectGuid giverGuid, Quest quest)
    {
        WorldObject? giver = Giver(player, giverGuid);
        if (giver is Creature creature)
        {
            // The quest is committed when the hooks run: a script's fault must not stop the DB CompleteScript, the other subscribers or the
            // next-quest offer, nor reach the reward publication's exception handler (which quarantines the character and kicks the session).
            if (creature.AI is { } script)
            {
                try
                {
                    script.OnQuestRewarded(player, quest.Id);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    LogHookFault(log, RewardSite, ex, script.GetType().Name, creature, quest.Id);
                }
            }

            if (ScriptQuestHandler(factory, creature) is { } registered)
            {
                try
                {
                    registered.OnQuestRewarded(player, creature, quest.Id);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    LogHookFault(log, RewardSite, ex, creature.Template.ScriptName ?? string.Empty, creature, quest.Id);
                }
            }
        }

        // The creature's hook never claims a quest, so the DB script always runs (Player.cpp:12712-12713).
        if (quest.Template.CompleteScript != 0 && giver is not null && player.Map is { } map && systemOf(map) is { } system)
        {
            try
            {
                system.StartDbScript(DbScriptKind.QuestEnd, quest.Template.CompleteScript, giver, player);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("creature-quest-script", ex);
                log.LogError(ex, "quest complete script {ScriptId} failed for quest {QuestId}", quest.Template.CompleteScript, quest.Id);
            }
        }
    }

    private const string AcceptSite = "CreatureQuestScripts.OnQuestAccept";
    private const string RewardSite = "CreatureQuestScripts.OnQuestRewarded";

    private static void LogHookFault(ILogger log, string site, Exception ex, string scriptName, Creature creature, uint questId)
        => log.LogFault(site, ex,
            "{Site} hook of script {Script} failed (quest {Quest}, creature entry {Entry}); the quest carries on", site, scriptName, questId, creature.Template.Entry);

    private static void OnGossipScript(Func<Map, CreatureMapSystem?> systemOf, ILogger log, Player player, ObjectGuid npcGuid, uint scriptId, bool playerIsSource)
    {
        if (Giver(player, npcGuid) is not { } npc || player.Map is not { } map || systemOf(map) is not { } system)
        {
            return;
        }

        try
        {
            if (playerIsSource)
            {
                system.StartDbScript(DbScriptKind.Gossip, scriptId, player, npc);
            }
            else
            {
                system.StartDbScript(DbScriptKind.Gossip, scriptId, npc, player);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("creature-quest-script", ex);
            log.LogError(ex, "gossip script {ScriptId} failed for npc {NpcGuid}", scriptId, npcGuid);
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

    /// <summary>The creature, game object or sharing player on the player's map; anything else (an item) is no world giver.</summary>
    private static WorldObject? Giver(Player player, ObjectGuid guid)
        => player.Map?.FindObject(guid) is { } found && found is Creature or GameObject or Player ? found : null;

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
