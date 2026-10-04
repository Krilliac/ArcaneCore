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

    /// <summary>
    /// The group's looter pointer or loot method changed on a kill: resend SMSG_GROUP_LIST to its
    /// members (vmangos Group::SendUpdate after SetLooterGuid, Group.cpp:2530-2542). No-op by default.
    /// </summary>
    void LooterChanged(Group group)
    {
    }

    /// <summary>
    /// Whether the member is online anywhere (vmangos ObjectAccessor::FindPlayer), used to decide if
    /// a master looter is still available. Defaults to true when the groups area cannot tell.
    /// </summary>
    bool IsMemberOnline(ObjectGuid guid) => true;
}

/// <summary>Loot tunables.</summary>
public sealed class LootOptions
{
    /// <summary>INTERACTION_DISTANCE: how close a looter must stay to the corpse/chest (plus both radii).</summary>
    public float LootDistance { get; set; } = 5.0f;

    /// <summary>vmangos CONFIG_FLOAT_GROUP_XP_DISTANCE: group members within it share loot and money.</summary>
    public float GroupLootDistance { get; set; } = 74.0f;

    /// <summary>Extra yards for a world boss victim (vmangos Object.cpp:1494). 0 restores the plain limit.</summary>
    public float BossRewardDistanceBonus { get; set; } = 150.0f;

    /// <summary>Raid maps have no reward distance limit (vmangos Object.cpp:1482-1483). False applies <see cref="GroupLootDistance"/> there too.</summary>
    public bool RaidMapsUnlimitedRewardDistance { get; set; } = true;

    /// <summary>The group reward distance rule these options describe (see <see cref="GroupRewardRange"/>).</summary>
    public GroupRewardOptions RewardRange => new()
    {
        Distance = GroupLootDistance,
        BossDistanceBonus = BossRewardDistanceBonus,
        RaidMapsUnlimited = RaidMapsUnlimitedRewardDistance,
    };

    /// <summary>
    /// vmangos CONFIG_FLOAT_RATE_CORPSE_DECAY_LOOTED (Rate.Corpse.Decay.Looted, mangosd.conf.dist.in:1542; cmangos World.cpp:457 too): a looted-out
    /// corpse stays this share of its decay time. The retail default 0 means a third of the creature's respawn delay (Creature.cpp:3369-3370).
    /// </summary>
    public float LootedCorpseDecayRate { get; set; }

    /// <summary>
    /// How long a need/greed roll waits for votes before the players who did not vote count as passed, in milliseconds
    /// (vmangos Group.cpp:72 LOOT_ROLL_TIMEOUT, 1 minute; the same value goes into SMSG_LOOT_START_ROLL as the countdown).
    /// </summary>
    public uint RollTimeoutMs { get; set; } = 60000;

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
