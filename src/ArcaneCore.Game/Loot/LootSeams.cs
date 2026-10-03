using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;

namespace ArcaneCore.Game.Loot;

/// <summary>
/// What loot and game objects need from the quest journal (owned by the quests area; the world
/// feature adapts QuestNpcServices). World thread. Every member must be cheap: dynamic flags
/// ask it per viewer.
/// </summary>
public interface ILootQuestJournal
{
    /// <summary>vmangos Player::HasQuestForItem: an incomplete logged quest still needs <paramref name="itemId"/>.</summary>
    bool NeedsQuestItem(Player player, uint itemId);

    /// <summary>The quest is in the log and incomplete (goober/chest quest gates).</summary>
    bool IsQuestIncomplete(Player player, uint questId);

    /// <summary>Items entered the inventory from loot (vmangos ItemAddedQuestCheck).</summary>
    void ItemLooted(Player player, uint itemId, uint count);

    /// <summary>Money changed through loot (vmangos ModifyMoney → quest money check; save the character).</summary>
    void MoneyLooted(Player player);

    /// <summary>A game object was used (vmangos CastedCreatureOrGO with spell 0 for goobers).</summary>
    void GameObjectUsed(Player player, uint entry, ObjectGuid guid);
}

/// <summary>The player's group, when the groups area is present. World thread.</summary>
public interface ILootGroups
{
    Group? GroupOf(Player player);
}

/// <summary>Loot tunables.</summary>
public sealed class LootOptions
{
    /// <summary>INTERACTION_DISTANCE: how close a looter must stay to the corpse/chest (plus both radii).</summary>
    public float LootDistance { get; set; } = 5.0f;

    /// <summary>vmangos CONFIG_FLOAT_GROUP_XP_DISTANCE: group members within it share loot and money.</summary>
    public float GroupLootDistance { get; set; } = 74.0f;

    /// <summary>vmangos CONFIG_FLOAT_RATE_CORPSE_DECAY_LOOTED: a looted-out corpse stays this share of its decay time.</summary>
    public float LootedCorpseDecayRate { get; set; } = 0.5f;

    /// <summary>vmangos Rate.Drop.Money.</summary>
    public float MoneyRate { get; set; } = 1.0f;
}

/// <summary>Why a loot request was refused.</summary>
public enum LootResult
{
    Ok,
    NotFound,
    TooFar,
    NotAllowed,
    Dead,
    NothingToLoot,
    NotLootable,
    Locked,

    /// <summary>Nothing can be stored for this chest, so opening it would reroll awards after its map is recreated.</summary>
    Unsupported,
}
