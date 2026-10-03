using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Npc;

/// <summary>UNIT_NPC_FLAGS bits (vmangos UnitDefines.h NPCFlags, build 5875).</summary>
[Flags]
public enum NpcFlags : uint
{
    None = 0x0000,
    Gossip = 0x0001,
    QuestGiver = 0x0002,
    Vendor = 0x0004,
    FlightMaster = 0x0008,
    Trainer = 0x0010,
    SpiritHealer = 0x0020,
    SpiritGuide = 0x0040,
    Innkeeper = 0x0080,
    Banker = 0x0100,
    Petitioner = 0x0200,
    TabardDesigner = 0x0400,
    BattleMaster = 0x0800,
    Auctioneer = 0x1000,
    StableMaster = 0x2000,
    Repair = 0x4000,
}

/// <summary>creature_template.trainer_type (vmangos SharedDefines.h TrainerType).</summary>
public enum TrainerType : byte
{
    Class = 0,
    Mounts = 1,
    TradeSkills = 2,
    Pets = 3,
}

/// <summary>
/// What the NPC services need to know about one creature, as seen by one player at the time
/// of the lookup. The creatures owner fills it from its spawn and template data
/// (creature_template npcflag, gossip_menu_id, trainer_*; vmangos Creature/CreatureInfo).
/// </summary>
/// <param name="Guid">Creature GUID (HighGuid.Unit, entry and spawn counter).</param>
/// <param name="Entry">creature_template entry.</param>
/// <param name="SpawnId">creature.guid (spawn id, the key of npc_gossip).</param>
/// <param name="NpcFlags">UNIT_NPC_FLAGS as currently set on the creature.</param>
/// <param name="IsHostile">Whether the creature is hostile to the asking player (vmangos Unit::IsHostileTo).</param>
/// <param name="IsInCombat">vmangos Unit::IsInCombat.</param>
/// <param name="IsNotSelectable">UNIT_FLAG_NOT_SELECTABLE is set.</param>
/// <param name="GossipMenuId">creature_template.gossip_menu_id (vmangos GetDefaultGossipMenuId).</param>
/// <param name="FactionId">Faction (Faction.dbc id) of the creature's faction template: reputation-ranked vendor items without their own faction use it.</param>
public sealed record NpcInfo(
    ObjectGuid Guid,
    uint Entry,
    uint SpawnId,
    NpcFlags NpcFlags,
    uint MapId,
    float X,
    float Y,
    float Z,
    float BoundingRadius,
    bool IsAlive,
    bool IsHostile,
    bool IsInCombat,
    bool IsNotSelectable,
    uint GossipMenuId,
    TrainerType TrainerType = TrainerType.Class,
    byte TrainerClass = 0,
    byte TrainerRace = 0,
    uint TrainerSpell = 0,
    uint FactionId = 0);

/// <summary>
/// Finds a creature in the player's map (owned by the creatures area). Returns null when no
/// such creature exists for the player. World thread.
/// </summary>
public interface ICreatureLookup
{
    NpcInfo? Find(Player player, ObjectGuid guid);
}

/// <summary>vmangos InventoryResult values the NPC services interpret (ItemDefines.h).</summary>
public enum InventoryResult : byte
{
    Ok = 0,
    ItemNotFound = 23,
    InventoryFull = 50,
    CantCarryMoreOfThis = 17,
}

/// <summary>item_template data the vendor and quest packets carry (vmangos ItemPrototype).</summary>
public sealed record ItemInfo(
    uint Entry,
    uint DisplayId,
    uint BuyPrice,
    uint BuyCount,
    uint MaxDurability,
    uint AllowableClass,
    uint AllowableRace,
    uint Bonding,
    uint RequiredReputationFaction,
    uint RequiredReputationRank,
    uint RequiredHonorRank);

/// <summary>vmangos SellResult (Player.h) — the reason byte of SMSG_SELL_ITEM.</summary>
public enum SellResult : byte
{
    CantFindItem = 1,
    CantSellItem = 2,
    CantFindVendor = 3,
    YouDontOwnThatItem = 4,
    Unk = 5,
    OnlyEmptyBag = 6,
}

/// <summary>Outcome of <see cref="IItemService.SellToVendor"/>: sold for <see cref="Money"/>, ignored, or an error.</summary>
public readonly record struct ItemSale(bool Sold, SellResult? Error, uint Money)
{
    public static ItemSale Ignored => new(false, null, 0);

    public static ItemSale Failed(SellResult error) => new(false, error, 0);

    public static ItemSale Succeeded(uint money) => new(true, null, money);
}

/// <summary>
/// The inventory operations the quest and vendor code needs (owned by the items area). The
/// implementation reports added/removed items to <c>IQuestObjectiveEvents</c> itself, as
/// vmangos StoreNewItem/DestroyItemCount call ItemAddedQuestCheck/ItemRemovedQuestCheck.
/// World thread.
/// </summary>
public interface IItemService
{
    /// <summary>The item prototype, or null when the item does not exist.</summary>
    ItemInfo? GetItem(uint itemId);

    /// <summary>vmangos Player::GetItemCount.</summary>
    uint GetItemCount(Player player, uint itemId, bool inBankAlso);

    /// <summary>vmangos Player::CanStoreNewItem(NULL_BAG, NULL_SLOT, …).</summary>
    InventoryResult CanStoreNewItem(Player player, uint itemId, uint count);

    /// <summary>Store new items and tell the client (vmangos StoreNewItem + SendNewItem). False if it could not.</summary>
    bool StoreNewItem(Player player, uint itemId, uint count);

    /// <summary>vmangos Player::DestroyItemCount(item, count, update=true).</summary>
    void DestroyItemCount(Player player, uint itemId, uint count);

    /// <summary>
    /// The item checks of vmangos HandleSellItemOpcode after the vendor check (ownership, bank,
    /// bag, sell price, durability) and moving the item to buyback. Money is added by the caller.
    /// </summary>
    ItemSale SellToVendor(Player player, ObjectGuid vendor, ObjectGuid item, byte count);

    /// <summary>SMSG_INVENTORY_CHANGE_FAILURE for a failed store of a new item (vmangos Player::SendEquipError(msg, null, null, 0, item)).</summary>
    void SendEquipError(Player player, InventoryResult result, uint itemId);
}

/// <summary>
/// Spell data of one trainer entry, from the teaching spell (npc_trainer.spell) and its
/// EffectTriggerSpell[0] — the inputs of vmangos Player::GetTrainerSpellState and
/// SendTrainerSpellHelper.
/// </summary>
/// <param name="LearnedSpell">The teaching spell's EffectTriggerSpell[0].</param>
/// <param name="SpellLevel">The learned spell's spellLevel.</param>
/// <param name="ChainPrev">Spell chain previous rank (0 none).</param>
/// <param name="ChainReq">Spell chain additional requirement (0 none).</param>
/// <param name="LearnedIsPrimaryProfessionFirstRank">SpellMgr::IsPrimaryProfessionFirstRankSpell(learned spell).</param>
/// <param name="IsPrimaryProfessionLearn">The teaching spell trains a primary profession skill (state gating).</param>
/// <param name="TeachesPrimaryProfessionFirstRank">The teaching spell (or the spell it teaches) is a primary profession's first rank.</param>
public sealed record TrainerSpellInfo(
    uint LearnedSpell,
    uint SpellLevel,
    uint ChainPrev,
    uint ChainReq,
    bool LearnedIsPrimaryProfessionFirstRank,
    bool IsPrimaryProfessionLearn,
    bool TeachesPrimaryProfessionFirstRank);

/// <summary>Spell knowledge and teaching (owned by the spells area). World thread.</summary>
public interface ISpellLearner
{
    /// <summary>Trainer data for a teaching spell, or null when the spell does not exist.</summary>
    TrainerSpellInfo? DescribeTrainerSpell(uint teachingSpell);

    bool HasSpell(Player player, uint spellId);

    /// <summary>vmangos Player::IsSpellFitByClassAndRace.</summary>
    bool IsSpellFitByClassAndRace(Player player, uint spellId);

    /// <summary>vmangos Player::GetSkillValueBase (0 when the skill is unknown).</summary>
    uint GetSkillValueBase(Player player, uint skill);

    /// <summary>vmangos Player::GetSkillValue (quest skill requirements).</summary>
    uint GetSkillValue(Player player, uint skill);

    /// <summary>vmangos Player::GetFreePrimaryProfessionPoints.</summary>
    uint GetFreePrimaryProfessionPoints(Player player);

    /// <summary>Cast the teaching spell on the player (vmangos HandleTrainerBuySpellOpcode). True when the cast succeeded.</summary>
    bool CastTeachingSpell(Player player, ObjectGuid trainer, uint teachingSpell);

    /// <summary>Cast a quest reward spell on the player (vmangos RewardQuest RewSpellCast/RewSpell).</summary>
    void CastQuestRewardSpell(Player player, ObjectGuid questEnder, uint spellId);
}

/// <summary>Experience gain (owned by the combat/levels area). World thread.</summary>
public interface IPlayerExperience
{
    /// <summary>vmangos Player::GiveXP(xp, victim = nullptr), including level-ups.</summary>
    void GiveXp(Player player, uint xp);
}

/// <summary>Reputation standing (owned by the reputation owner). World thread.</summary>
public interface IPlayerReputation
{
    /// <summary>vmangos ReputationMgr::GetReputation(factionId).</summary>
    int GetReputation(Player player, uint factionId);

    /// <summary>vmangos Player::GetReputationRank(factionId) as ReputationRank (0 hated … 7 exalted).</summary>
    byte GetReputationRank(Player player, uint factionId);

    /// <summary>vmangos Player::GetReputationPriceDiscount(creature): 1.0 is no discount.</summary>
    float GetPriceDiscount(Player player, NpcInfo npc);
}

/// <summary>
/// Taxi flight movement (owned by the movement area). The NPC services validate a flight request
/// and charge it; the owner mounts the player and moves it along the path. World thread.
/// </summary>
public interface ITaxiFlights
{
    /// <summary>
    /// Start a flight along <paramref name="pathId"/> (vmangos Player::ActivateTaxiPathTo after
    /// the checks: CombatStop, mount, SMSG_ACTIVATETAXIREPLY OK, MoveTaxiFlight). False when it
    /// could not start; nothing has been charged then.
    /// </summary>
    bool StartFlight(Player player, uint sourceNode, uint destinationNode, uint pathId, uint mountCreatureEntry);
}

/// <summary>Map properties (owned by the maps area). World thread.</summary>
public interface IMapInfo
{
    /// <summary>vmangos Map::Instanceable (dungeon, raid or battleground).</summary>
    bool IsInstanceable(uint mapId);

    /// <summary>The area id of a position (vmangos GetAreaId); 0 when unknown.</summary>
    uint GetAreaId(uint mapId, float x, float y, float z);
}

/// <summary>The conditions table evaluation (vmangos IsConditionSatisfied). World thread.</summary>
public interface IConditionEvaluator
{
    bool IsSatisfied(uint conditionId, Player player, NpcInfo? source);
}
