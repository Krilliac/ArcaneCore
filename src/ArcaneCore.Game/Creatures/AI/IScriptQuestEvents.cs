using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// The quest log as DB scripts and player-linked escorts reach it (cmangos <c>Player</c> methods called from
/// ScriptAction::ExecuteDbscriptCommand, DBScripts/ScriptMgr.cpp, and from vmangos npc_escortAI, AI/ScriptedEscortAI.cpp). Bound by the
/// world to its quest service through <see cref="CreatureAiServices.ScriptQuests"/>; null there: these commands credit nothing.
/// </summary>
public interface IScriptQuestEvents
{
    /// <summary>cmangos Player::AreaExploredOrEventHappens (SCRIPT_COMMAND_QUEST_EXPLORED).</summary>
    void AreaExploredOrEventHappens(Player player, uint questId);

    /// <summary>cmangos Player::FailQuest (SCRIPT_COMMAND_QUEST_EXPLORED out of range or with a dead partner).</summary>
    void FailQuest(Player player, uint questId);

    /// <summary>cmangos Player::KilledMonsterCredit (SCRIPT_COMMAND_KILL_CREDIT without group credit).</summary>
    void KilledMonsterCredit(Player player, uint creatureEntry, ObjectGuid source);

    /// <summary>vmangos Player::GroupEventFailHappens (Player.cpp): the quest fails for every online member of the player's group.</summary>
    void GroupEventFailHappens(Player player, uint questId);

    /// <summary>The online members of the player's group, the player included; empty when not grouped (vmangos IsPlayerOrGroupInRange).</summary>
    IReadOnlyList<Player> GroupMembersOf(Player player);
}
