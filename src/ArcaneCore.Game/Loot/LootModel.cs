using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Loot;

namespace ArcaneCore.Game.Loot;

// Loot model re-implemented from the behaviour of vmangos/cmangos LootMgr (Loot, LootItem,
// QuestItem, LootView) and the 1.12 SMSG_LOOT_RESPONSE layout documented by gtker/wow_messages.
// No code was copied.

/// <summary>LootType: what is being looted, sent in SMSG_LOOT_RESPONSE.</summary>
public enum LootType : byte
{
    None = 0,
    Corpse = 1,
    Pickpocketing = 2,
    Fishing = 3,
    Disenchanting = 4,
    Skinning = 6,

    /// <summary>Server-side only (vmangos LOOT_FISHINGHOLE): the client is sent <see cref="Fishing"/> (<see cref="LootTypes.ToWire"/>).</summary>
    FishingHole = 20,

    /// <summary>Server-side only (vmangos LOOT_FISHING_FAIL): the client is sent <see cref="Fishing"/>.</summary>
    FishingFail = 21,

    /// <summary>Server-side only (vmangos LOOT_INSIGNIA): the client is sent <see cref="Pickpocketing"/> (Player.cpp:7987-7989).</summary>
    Insignia = 22,
}

/// <summary>
/// The loot type the 1.12 client understands. vmangos Player::SendLoot (Player.cpp:7980-7995): "LOOT_SKINNING,
/// LOOT_PROSPECTING, LOOT_INSIGNIA and LOOT_FISHINGHOLE unsupported by client", so skinning and insignia loot is shown as
/// LOOT_PICKPOCKETING (2) and the fishing hole / failed-fishing types as LOOT_FISHING (3).
/// </summary>
public static class LootTypes
{
    public static byte ToWire(LootType type) => type switch
    {
        LootType.Skinning or LootType.Insignia => (byte)LootType.Pickpocketing,
        LootType.FishingHole or LootType.FishingFail => (byte)LootType.Fishing,
        _ => (byte)type,
    };
}

/// <summary>LootSlotType: per-viewer slot state in SMSG_LOOT_RESPONSE.</summary>
public enum LootSlotType : byte
{
    /// <summary>LOOT_SLOT_NORMAL: the viewer may take it.</summary>
    AllowLoot = 0,

    /// <summary>LOOT_SLOT_VIEW: visible, not takeable (roll ongoing).</summary>
    RollOngoing = 1,

    /// <summary>LOOT_SLOT_MASTER: master looter assigns it.</summary>
    Master = 2,

    /// <summary>LOOT_SLOT_LOCKED: reserved for another player.</summary>
    Locked = 3,
}

/// <summary>What the loot belongs to (decides what happens when it is released or emptied).</summary>
public enum LootSourceKind
{
    Creature,
    GameObject,
    Item,
    Skinning,
}

/// <summary>How the shared items of one loot window are handed out (vmangos Loot permission: ALL, GROUP, MASTER).</summary>
public enum LootPermission : byte
{
    /// <summary>Everyone allowed takes it (solo, free-for-all, round robin with its <see cref="LootBag.Owner"/>, chests without group rules).</summary>
    Open,

    /// <summary>Group loot and need-before-greed: items at or above the threshold are rolled for (<see cref="LootRollManager"/>).</summary>
    Roll,

    /// <summary>Master loot: items at or above the threshold are given by <see cref="LootBag.MasterLooter"/>.</summary>
    Master,
}

/// <summary>One item stack in a loot window (vmangos LootItem).</summary>
public sealed class LootItem
{
    public LootItem(byte slot, uint itemId, uint count, bool isQuestItem, bool isPerPlayer, uint displayId)
    {
        Slot = slot;
        ItemId = itemId;
        Count = count;
        IsQuestItem = isQuestItem;
        IsPerPlayer = isPerPlayer;
        DisplayId = displayId;
    }

    /// <summary>The slot index the client uses for this stack (stable while the loot exists).</summary>
    public byte Slot { get; }

    public uint ItemId { get; }

    public uint Count { get; }

    public uint DisplayId { get; }

    /// <summary>Quest drop (negative chance): only players who need it see it (vmangos QuestItem).</summary>
    public bool IsQuestItem { get; }

    /// <summary>ITEM_FLAG_PARTY_LOOT (0x800, vmangos freeforall): every allowed looter gets a copy.</summary>
    public bool IsPerPlayer { get; }

    /// <summary>For a quest or conditioned item: the players eligible when the loot was generated.</summary>
    public HashSet<ObjectGuid> AllowedLooters { get; } = [];

    /// <summary>Taken by somebody (shared item), or by everyone allowed (per-player item).</summary>
    public bool IsLooted { get; internal set; }

    /// <summary>Who took their copy of a per-player item.</summary>
    public HashSet<ObjectGuid> LootedBy { get; } = [];

    /// <summary>
    /// False when the group loot threshold puts this shared item under roll or master-give control (vmangos is_underthreshold
    /// is 1 by default). Only <see cref="LootService"/> sets it, at generation; per-player and quest items stay under the threshold.
    /// </summary>
    public bool IsUnderThreshold { get; internal set; } = true;

    /// <summary>A need/greed roll for this item is running (vmangos is_blocked): viewers see it as view-only and nobody can take it.</summary>
    public bool RollActive { get; internal set; }

    /// <summary>
    /// The roll winner whose bags were full (vmangos LootItem::winner): only that player may take the item afterwards.
    /// Empty when nobody holds a claim.
    /// </summary>
    public ObjectGuid Winner { get; internal set; }
}

/// <summary>
/// One loot window's contents (vmangos Loot): money, up to 16 normal items plus quest items,
/// the players allowed to loot it and the players looking at it now. World thread only.
/// </summary>
public sealed class LootBag
{
    private readonly List<LootItem> _items = [];

    public LootBag(ObjectGuid source, LootSourceKind kind, LootType type)
    {
        Source = source;
        Kind = kind;
        Type = type;
    }

    public ObjectGuid Source { get; }

    public LootSourceKind Kind { get; }

    public LootType Type { get; }

    /// <summary>
    /// The loot is not distance-checked when taken (vmangos LootHandler.cpp:56-70: an owned fishing bobber and every fishing
    /// hole are exempt from the interaction distance of the autostore handler).
    /// </summary>
    public bool IgnoreDistance { get; set; }

    /// <summary>
    /// Whether corpse money is split between the recipients (vmangos LootHandler.cpp:274-290 sets shareMoneyWithGroup false
    /// for a rogue's pickpocketed creature and for item loot). Only creature loot is ever split.
    /// </summary>
    public bool ShareMoney { get; set; } = true;

    /// <summary>What happens when the last viewer's window closes; null keeps the corpse / chest / item behaviour.</summary>
    public ILootReleaseHandler? ReleaseHandler { get; set; }

    /// <summary>Called after an item or the money was taken from this bag (a container item keeps its remaining loot in step with it).</summary>
    public Action<LootBag>? Changed { get; set; }

    /// <summary>
    /// Replaces the source validity test of item and money takes (the default checks the source type and the loot distance).
    /// Special sources (a pickpocketed creature, a fishing bobber) answer for themselves.
    /// </summary>
    public Func<Player, bool>? SourceCheck { get; set; }

    public uint Gold { get; internal set; }

    public IReadOnlyList<LootItem> Items => _items;

    /// <summary>Players allowed to loot (killer and group members in range); empty means anyone.</summary>
    public HashSet<ObjectGuid> Recipients { get; } = [];

    /// <summary>
    /// The only player allowed to take shared items right now (round robin looter, the opener
    /// of a chest), or empty when every recipient may.
    /// </summary>
    public ObjectGuid Owner { get; internal set; }

    /// <summary>How shared items are handed out (rolls, master give or open). World thread; set once at generation.</summary>
    public LootPermission Permission { get; internal set; }

    /// <summary>The group's master looter when <see cref="Permission"/> is <see cref="LootPermission.Master"/>.</summary>
    public ObjectGuid MasterLooter { get; internal set; }

    /// <summary>Rolls were started for the items above the threshold (they start once, when the first player opens the loot).</summary>
    public bool RollsStarted { get; internal set; }

    /// <summary>
    /// The group whose loot method set <see cref="Permission"/> (vmangos Creature::GetGroupLootRecipient): its members roll, whoever
    /// opens the loot first. Null when no group method applies. World thread.
    /// </summary>
    internal Groups.Group? DistributionGroup { get; set; }

    /// <summary>Players with this loot window open (vmangos m_playersLooting).</summary>
    public HashSet<Player> Viewers { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Set once the source was released with nothing left (no more reopen).</summary>
    public bool IsClosed { get; internal set; }

    /// <summary>
    /// The durable key of an instance chest's bag (see <see cref="ILootStateCoordinator"/>), or null
    /// for loot that only lives in memory (corpses, shared-copy chests).
    /// </summary>
    internal LootStateKey? DurableKey { get; set; }

    /// <summary>
    /// The durable record of this chest: the generation, who may take what and what was taken.
    /// The record has no money column, so a bag with gold cannot be stored; the durable open path generates no money
    /// (<c>LootService.OpenDurableGameObject</c>) although the template's <c>mingold..maxgold</c> are imported.
    /// </summary>
    internal LootStateRecord ToRecord(LootStateKey key, uint sourceEntry, uint generation, long respawnAtUnix)
    {
        if (Gold != 0)
        {
            throw new InvalidOperationException("chest loot with money cannot be stored");
        }

        bool consumed = IsEmpty;
        return new LootStateRecord(key, sourceEntry, CharacterIdOf(Owner), generation, consumed, consumed ? respawnAtUnix : 0,
            [.. Recipients.Select(CharacterIdOf).Order()],
            [.. _items.Select(i => new LootStateItem(i.Slot, i.ItemId, i.Count, i.IsQuestItem, i.IsPerPlayer, i.IsLooted,
                [.. i.AllowedLooters.Select(CharacterIdOf).Order()], [.. i.LootedBy.Select(CharacterIdOf).Order()]))]);
    }

    /// <summary>
    /// A bag rebuilt from its durable record. The client display ids come from the item
    /// templates; null when a template is missing (the chest is then refused rather than shown
    /// with wrong items).
    /// </summary>
    internal static LootBag? FromRecord(ObjectGuid source, LootType type, LootStateRecord record, Func<uint, uint?> displayIdOf)
    {
        var bag = new LootBag(source, LootSourceKind.GameObject, type) { DurableKey = record.Key };
        foreach (int id in record.Recipients)
        {
            bag.Recipients.Add(GuidOf(id));
        }

        bag.Owner = record.LootOwnerCharacterId == 0 ? default : GuidOf(record.LootOwnerCharacterId);
        foreach (LootStateItem stored in record.Items)
        {
            if (displayIdOf(stored.ItemId) is not { } display)
            {
                return null;
            }

            var item = new LootItem(stored.Slot, stored.ItemId, stored.Count, stored.IsQuest, stored.IsPerPlayer, display)
            {
                IsLooted = stored.IsLooted,
            };
            foreach (int id in stored.AllowedLooters)
            {
                item.AllowedLooters.Add(GuidOf(id));
            }

            foreach (int id in stored.LootedBy)
            {
                item.LootedBy.Add(GuidOf(id));
            }

            bag.Add(item);
        }

        return bag;
    }

    /// <summary>
    /// Bring the live bag in line with a committed record of the same generation: taken stacks and
    /// who took them, and the recipients (a late opener that is not stored yet stays). The
    /// round-robin owner can only be cleared (released), never set again.
    /// </summary>
    internal void ApplyRecord(LootStateRecord record)
    {
        foreach (LootStateItem stored in record.Items)
        {
            if (FindSlot(stored.Slot) is not { } item)
            {
                continue;
            }

            item.IsLooted = stored.IsLooted;
            foreach (int id in stored.LootedBy)
            {
                item.LootedBy.Add(GuidOf(id));
            }
        }

        foreach (int id in record.Recipients)
        {
            Recipients.Add(GuidOf(id));
        }

        // A release since the operation began stays released: the owner is only ever cleared here.
        if (record.LootOwnerCharacterId == 0)
        {
            Owner = default;
        }
    }

    /// <summary>The character id the characters database uses for a player (the GUID counter).</summary>
    internal static int CharacterIdOf(ObjectGuid guid) => guid.IsEmpty ? 0 : checked((int)guid.Low);

    internal static ObjectGuid GuidOf(int characterId) => ObjectGuid.Player(checked((uint)characterId));

    internal void Add(LootItem item) => _items.Add(item);

    /// <summary>vmangos <c>Loot::leaveOnlyQuestItems</c> (<c>clear(false)</c>): drop every ordinary item and the money, keep the quest items.</summary>
    internal void KeepOnlyQuestItems()
    {
        _items.RemoveAll(i => !i.IsQuestItem);
        Gold = 0;
    }

    public LootItem? FindSlot(byte slot) => _items.Find(i => i.Slot == slot);

    /// <summary>Whether <paramref name="player"/> is a recipient (or the loot is open to anyone).</summary>
    public bool IsRecipient(Player player) => Recipients.Count == 0 || Recipients.Contains(player.Guid);

    /// <summary>
    /// What <paramref name="player"/> sees in <paramref name="item"/>'s slot, or null when it is
    /// hidden from them (taken, someone else's quest item, their per-player copy taken).
    /// </summary>
    public LootSlotType? SlotFor(Player player, LootItem item)
    {
        if (!IsRecipient(player) || item.IsLooted)
        {
            return null;
        }

        if ((item.IsQuestItem || item.AllowedLooters.Count > 0) && !item.AllowedLooters.Contains(player.Guid))
        {
            return null;
        }

        if (item.IsPerPlayer || item.IsQuestItem)
        {
            return item.LootedBy.Contains(player.Guid) ? null : LootSlotType.AllowLoot;
        }

        if (!Owner.IsEmpty && Owner != player.Guid)
        {
            return null;
        }

        // A roll winner whose bags were full keeps the only claim (vmangos LootItem::winner).
        if (!item.Winner.IsEmpty && item.Winner != player.Guid)
        {
            return null;
        }

        switch (Permission)
        {
            case LootPermission.Roll:
                return item.RollActive ? LootSlotType.RollOngoing : LootSlotType.AllowLoot;
            case LootPermission.Master:
                // Under the threshold (or a claim by this player): a plain take. Above it only the master sees the slot, as a master slot.
                if (item.IsUnderThreshold || item.Winner == player.Guid)
                {
                    return LootSlotType.AllowLoot;
                }

                return player.Guid == MasterLooter ? LootSlotType.Master : null;
            default:
                return LootSlotType.AllowLoot;
        }
    }

    /// <summary>Whether <paramref name="player"/> would see money or any item (vmangos Loot::IsLootedFor negated).</summary>
    public bool HasSomethingFor(Player player)
    {
        if (!IsRecipient(player))
        {
            return false;
        }

        if (Gold > 0 && (Owner.IsEmpty || Owner == player.Guid))
        {
            return true;
        }

        return _items.Exists(i => SlotFor(player, i) is not null);
    }

    /// <summary>Nothing left for anybody (vmangos Loot::isLooted: no money, every item taken).</summary>
    public bool IsEmpty => Gold == 0 && _items.TrueForAll(i => i.IsLooted);

    /// <summary>Mark <paramref name="item"/> taken by <paramref name="player"/>.</summary>
    internal void MarkTaken(LootItem item, Player player)
    {
        if (item.IsQuestItem)
        {
            item.LootedBy.Add(player.Guid);
            item.IsLooted = item.AllowedLooters.All(item.LootedBy.Contains);
        }
        else if (item.IsPerPlayer)
        {
            // Open loot (no recipient list) keeps per-player copies for whoever comes next.
            item.LootedBy.Add(player.Guid);
            IEnumerable<ObjectGuid> eligible = item.AllowedLooters.Count > 0 ? item.AllowedLooters : Recipients;
            item.IsLooted = eligible.Any() && eligible.All(item.LootedBy.Contains);
        }
        else
        {
            item.IsLooted = true;
        }
    }
}
