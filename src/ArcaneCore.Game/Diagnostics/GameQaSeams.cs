using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Loot;

namespace ArcaneCore.Game.Diagnostics;

/// <summary>
/// Why the server refused a vendor, trainer, inventory or loot request: one value per decision site, so bot QA can tell a refusal of play (no money,
/// bags full, too far) from one that should not happen (the vendor does not sell the item, the trainer cannot teach a valid spell). The protocol code
/// the client got (BuyResult, SellResult, the trainer reason, InventoryResult, LootResult) travels next to it.
/// </summary>
public enum InteractionFailure : byte
{
    /// <summary>CMSG_BUY_ITEM of an item with no item_template row (BUY_ERR_CANT_FIND_ITEM, no vendor guid).</summary>
    BuyItemTemplateMissing,

    /// <summary>CMSG_BUY_ITEM at a vendor that is out of reach, not a vendor, hostile or dead (BUY_ERR_DISTANCE_TOO_FAR).</summary>
    BuyVendorUnreachable,

    /// <summary>The vendor does not list the item, or not for this player (BUY_ERR_CANT_FIND_ITEM).</summary>
    BuyNotSold,

    /// <summary>Limited stock that ran out (BUY_ERR_ITEM_ALREADY_SOLD).</summary>
    BuySoldOut,

    /// <summary>A reputation rank the player lacks (BUY_ERR_REPUTATION_REQUIRE).</summary>
    BuyReputation,

    /// <summary>An honor rank or level the player lacks (BUY_ERR_RANK_REQUIRE).</summary>
    BuyHonorRank,

    /// <summary>Not enough money (BUY_ERR_NOT_ENOUGHT_MONEY).</summary>
    BuyNoMoney,

    /// <summary>The bought item cannot be stored (SMSG_INVENTORY_CHANGE_FAILURE: bags full, unique, ...).</summary>
    BuyCannotStore,

    /// <summary>The item could be stored by the check and then could not be stored: nothing was sent.</summary>
    BuyStoreFailed,

    /// <summary>CMSG_BUYBACK_ITEM at a vendor out of reach.</summary>
    BuybackVendorUnreachable,

    /// <summary>CMSG_BUYBACK_ITEM of an empty buyback slot.</summary>
    BuybackEmptySlot,

    /// <summary>CMSG_BUYBACK_ITEM without the money.</summary>
    BuybackNoMoney,

    /// <summary>The bought-back item cannot be stored.</summary>
    BuybackCannotStore,

    /// <summary>CMSG_SELL_ITEM at a vendor out of reach (SELL_ERR_CANT_FIND_VENDOR).</summary>
    SellVendorUnreachable,

    /// <summary>The server has no item owner to sell through.</summary>
    SellNoItemService,

    /// <summary>The item owner refused the sale (the SellResult code says why).</summary>
    SellRefused,

    /// <summary>CMSG_TRAINER_BUY_SPELL at a trainer out of reach, or one that is not a trainer.</summary>
    TrainerUnreachable,

    /// <summary>The trainer does not train this character (another class, race or profession).</summary>
    TrainerNotForPlayer,

    /// <summary>The server has no spell owner to teach through.</summary>
    TrainerNoSpellService,

    /// <summary>The trainer does not list the spell.</summary>
    TrainerSpellNotListed,

    /// <summary>The trainer lists the spell but its teaching spell is not in the spell table.</summary>
    TrainerSpellUnknown,

    /// <summary>The spell is not GREEN for the player (level, skill or a previous rank missing; TRAIN_FAIL_NOT_ENOUGH_SKILL).</summary>
    TrainerSpellNotReady,

    /// <summary>Not enough money (TRAIN_FAIL_NOT_ENOUGH_MONEY).</summary>
    TrainerNoMoney,

    /// <summary>The spell is listed, GREEN and paid for, and the teaching cast failed: the trainer cannot teach a valid spell.</summary>
    TrainerTeachingFailed,

    /// <summary>SMSG_INVENTORY_CHANGE_FAILURE (the InventoryResult code says why).</summary>
    InventoryChangeFailed,

    /// <summary>CMSG_LOOT refused (the LootResult code says why).</summary>
    LootRefused,
}

/// <summary>Why the server refused a quest exchange, or failed a quest.</summary>
public enum QuestFailure : byte
{
    /// <summary>SMSG_QUESTGIVER_QUEST_INVALID at an accept or a share (the QuestInvalidReason code says why).</summary>
    Invalid,

    /// <summary>The quest needs behaviour the server lacks (QuestNpcServices.MissingAdapters): the accept is silently withheld.</summary>
    Unsupported,

    /// <summary>SMSG_QUESTGIVER_QUEST_FAILED at a turn-in or a reward (the QuestInvalidReason code says why).</summary>
    TurnInRefused,

    /// <summary>SMSG_QUESTUPDATE_FAILED(TIMER): the quest failed (a timer ran out, an escort died, a scripted failure).</summary>
    Failed,
}

/// <summary>
/// What the game tells bot QA (docs/areas/playbots-qa.md, interaction signals): server decisions that a request was refused or that a feature is
/// missing, at the place the decision is made. Every method defaults to doing nothing, and every caller passes ids and constants only, so a site
/// that is not a failure costs nothing and a failure costs one interface call. The implementation decides whether the unit is a managed playerbot
/// (real players are never reported). World thread.
/// </summary>
public interface IGameQaObserver
{
    /// <summary>A vendor, trainer, inventory or loot request of <paramref name="player"/> was refused.</summary>
    /// <param name="player">Who asked.</param>
    /// <param name="failure">The decision site.</param>
    /// <param name="code">The protocol code the client got (0 when none was sent).</param>
    /// <param name="npcEntry">The vendor or trainer creature entry (0 when unknown).</param>
    /// <param name="item">The item entry (0 when none).</param>
    /// <param name="spell">The trainer spell (0 when none).</param>
    void OnInteractionFailed(Player player, InteractionFailure failure, int code, uint npcEntry, uint item, uint spell)
    {
    }

    /// <summary>CMSG_LOOT of <paramref name="player"/> on <paramref name="source"/> (a corpse or a game object) was refused with <paramref name="result"/>.</summary>
    void OnLootRefused(Player player, ObjectGuid source, LootResult result)
    {
    }

    /// <summary>A quest exchange of <paramref name="player"/> was refused or the quest failed.</summary>
    /// <param name="player">Who asked.</param>
    /// <param name="questId">The quest.</param>
    /// <param name="failure">What happened.</param>
    /// <param name="code">The QuestInvalidReason the client got (0 when none).</param>
    /// <param name="detail">A constant or a short text (the missing adapters of an unsupported quest).</param>
    void OnQuestRefused(Player player, uint questId, QuestFailure failure, int code, string detail)
    {
    }

    /// <summary>A unit (a player, or null for a script) used <paramref name="go"/> and the server does not implement that use.</summary>
    /// <param name="user">The user, null when unknown.</param>
    /// <param name="go">The object.</param>
    /// <param name="gap">A constant naming the gap (<c>type</c>, <c>loot-store</c>, <c>spell-action</c>, ...).</param>
    /// <param name="value">A number the gap names (the spell action, the spell); 0 when none.</param>
    void OnObjectUnsupported(Unit? user, GameObject go, string gap, uint value)
    {
    }

    /// <summary>A player used <paramref name="go"/> successfully (a quest objective object, a chest).</summary>
    void OnObjectUsed(Player player, GameObject go)
    {
    }

    /// <summary>
    /// A creature AI or script needs what the server lacks: an unknown AIName, unsupported EventAI parts, a summon of a missing creature_template, an
    /// escort with no points. Reported once per map and gap.
    /// </summary>
    /// <param name="creature">The creature, null when an instance or object script asked.</param>
    /// <param name="entry">The creature entry the gap is about (the summoner, or 0 for an instance or object script).</param>
    /// <param name="gap">A constant naming the gap (<c>ai-name</c>, <c>event-ai</c>, <c>summon</c>, <c>escort</c>).</param>
    /// <param name="value">A number the gap names (the missing summoned entry); 0 when none.</param>
    /// <param name="detail">What it names in words (the AIName, the unsupported parts, the path).</param>
    void OnCreatureAiGap(Creature? creature, uint entry, string gap, uint value, string detail)
    {
    }
}
