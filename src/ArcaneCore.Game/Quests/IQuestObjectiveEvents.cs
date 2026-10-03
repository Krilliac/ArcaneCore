using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Quests;

/// <summary>
/// Quest objective credit raised by other areas (vmangos calls these Player methods from
/// Unit::Kill, Spell effects, StoreNewItem/DestroyItemCount, area triggers and scripts).
/// World thread. Unknown players or quests are ignored.
/// </summary>
public interface IQuestObjectiveEvents
{
    /// <summary>vmangos Player::KilledMonsterCredit (also kill credit given by scripts).</summary>
    void KilledMonsterCredit(Player player, uint entry, ObjectGuid guid);

    /// <summary>vmangos Player::CastedCreatureOrGO (spell objectives; <paramref name="isCreature"/> false for a game object).</summary>
    void CastedCreatureOrGo(Player player, uint entry, ObjectGuid guid, bool isCreature, uint spellId);

    /// <summary>vmangos Player::TalkedToCreature.</summary>
    void TalkedToCreature(Player player, uint entry, ObjectGuid guid);

    /// <summary>vmangos Player::ItemAddedQuestCheck.</summary>
    void ItemAdded(Player player, uint entry, uint count);

    /// <summary>vmangos Player::ItemRemovedQuestCheck.</summary>
    void ItemRemoved(Player player, uint entry, uint count);

    /// <summary>vmangos Player::AreaExploredOrEventHappens (area triggers, scripted events).</summary>
    void AreaExploredOrEventHappens(Player player, uint questId);

    /// <summary>vmangos Player::FailQuest.</summary>
    void FailQuest(Player player, uint questId);

    /// <summary>
    /// vmangos Player::ModifyMoney → MoneyChanged: call after <see cref="Player.Money"/> was changed
    /// by another area (loot, mail, trade) so quests that require money complete or revert.
    /// </summary>
    void MoneyChanged(Player player);
}
