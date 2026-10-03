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

    /// <summary>For a quest item: the players who needed it when the loot was generated.</summary>
    public HashSet<ObjectGuid> AllowedLooters { get; } = [];

    /// <summary>Taken by somebody (shared item), or by everyone allowed (per-player item).</summary>
    public bool IsLooted { get; internal set; }

    /// <summary>Who took their copy of a per-player item.</summary>
    public HashSet<ObjectGuid> LootedBy { get; } = [];
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

    public uint Gold { get; internal set; }

    public IReadOnlyList<LootItem> Items => _items;

    /// <summary>Players allowed to loot (killer and group members in range); empty means anyone.</summary>
    public HashSet<ObjectGuid> Recipients { get; } = [];

    /// <summary>
    /// The only player allowed to take shared items right now (round robin looter, the opener
    /// of a chest), or empty when every recipient may.
    /// </summary>
    public ObjectGuid Owner { get; internal set; }

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
    /// Chest loot holds no money (<c>mingold</c> is not imported), so a bag with gold cannot be stored.
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

        if (item.IsQuestItem && !item.AllowedLooters.Contains(player.Guid))
        {
            return null;
        }

        if (item.IsPerPlayer || item.IsQuestItem)
        {
            return item.LootedBy.Contains(player.Guid) ? null : LootSlotType.AllowLoot;
        }

        return Owner.IsEmpty || Owner == player.Guid ? LootSlotType.AllowLoot : null;
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
            item.IsLooted = Recipients.Count > 0 && Recipients.All(item.LootedBy.Contains);
        }
        else
        {
            item.IsLooted = true;
        }
    }
}
