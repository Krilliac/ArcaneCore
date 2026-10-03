using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Items;

/// <summary>
/// Online upkeep of timed and map/area-limited items (vmangos Player::UpdateItemDuration,
/// Item::UpdateDuration, Player::DestroyZoneLimitedItem).
/// <para>
/// Durations: every stored item with ITEM_FIELD_DURATION &gt; 0 is tracked from the moment it is
/// first seen (vmangos AddItemDurations: SMSG_ITEM_TIME_UPDATE is sent then) and loses the elapsed
/// whole seconds each tick (Player.cpp:1155); an item whose duration is &lt;= the elapsed time is
/// destroyed through the normal destroy path (Item.cpp:243-262). An item moved between bags keeps
/// its remaining time because tracking is by item identity.
/// </para>
/// <para>
/// Limits: an item whose template names another map or area is destroyed when the player is
/// alive and the map/zone changed (Player.cpp:6643-6656, DestroyZoneLimitedItem 10881-10909) and
/// when the player becomes alive again (the resurrect path). A dead player keeps them (patch 1.7.0).
/// The first observation counts as a change, which covers the login load rule (Player.cpp:15497-15503).
/// </para>
/// <para>Thread affinity: world thread.</para>
/// </summary>
public static class ItemMaintenance
{
    private sealed class State
    {
        public readonly HashSet<Item> Tracked = new(ReferenceEqualityComparer.Instance);
        public long LastTick;
        public bool Seen;
        public bool WasAlive;
        public uint MapId;
        public uint ZoneId;
    }

    private static readonly ConditionalWeakTable<PlayerInventory, State> States = new();

    /// <summary>One maintenance pass for <paramref name="player"/> at wall-clock second <paramref name="nowSeconds"/>.</summary>
    public static void Tick(Player player, long nowSeconds)
    {
        PlayerInventory inventory = player.Inventory;
        if (!inventory.IsLoaded)
        {
            return;
        }

        State state = States.GetOrCreateValue(inventory);
        LimitedItems(player, inventory, state);
        Durations(player, inventory, state, nowSeconds);
    }

    /// <summary>Forget a player's tracking (leaving the map or the world).</summary>
    public static void Forget(Player player) => States.Remove(player.Inventory);

    /// <summary>vmangos Item::IsLimitedToAnotherMapOrZone.</summary>
    public static bool IsLimitedToAnotherMapOrZone(Item item, uint mapId, uint zoneId)
        => (item.Template.MapBound != 0 && item.Template.MapBound != mapId)
            || (item.Template.AreaBound != 0 && item.Template.AreaBound != zoneId);

    private static void LimitedItems(Player player, PlayerInventory inventory, State state)
    {
        bool alive = player.IsAlive;
        bool changed = !state.Seen || state.MapId != player.MapId || state.ZoneId != player.ZoneId;
        bool resurrected = alive && !state.WasAlive;
        state.Seen = true;
        state.MapId = player.MapId;
        state.ZoneId = player.ZoneId;
        state.WasAlive = alive;
        if (!alive || !(changed || resurrected))
        {
            return;
        }

        foreach (Item item in inventory.AllItems.ToList())
        {
            if (IsLimitedToAnotherMapOrZone(item, player.MapId, player.ZoneId))
            {
                inventory.DestroyItem(item.BagSlot, item.Slot);
            }
        }
    }

    private static void Durations(Player player, PlayerInventory inventory, State state, long nowSeconds)
    {
        var timed = inventory.AllItems.Where(i => i.Duration != 0).ToList();
        state.Tracked.RemoveWhere(i => !timed.Contains(i));
        foreach (Item item in timed)
        {
            if (state.Tracked.Add(item))
            {
                SendTimeUpdate(player, item);
            }
        }

        if (state.LastTick == 0)
        {
            state.LastTick = nowSeconds;
            return;
        }

        if (nowSeconds <= state.LastTick)
        {
            return;
        }

        uint elapsed = (uint)Math.Min(nowSeconds - state.LastTick, uint.MaxValue);
        state.LastTick = nowSeconds;
        foreach (Item item in timed)
        {
            if (item.Duration <= elapsed)
            {
                state.Tracked.Remove(item);
                inventory.DestroyItem(item.BagSlot, item.Slot);
            }
            else
            {
                item.Duration -= elapsed;
            }
        }
    }

    /// <summary>SMSG_ITEM_TIME_UPDATE: u64 item guid, u32 remaining seconds (vmangos Item::SendTimeUpdate, Item.cpp:1096-1107).</summary>
    public static byte[] TimeUpdatePacket(Item item)
    {
        var writer = new PacketWriter(12);
        writer.WriteUInt64(item.Guid.Value);
        writer.WriteUInt32(item.Duration);
        return writer.ToArray();
    }

    private static void SendTimeUpdate(Player player, Item item)
        => player.Session.Send(WorldOpcode.SmsgItemTimeUpdate, TimeUpdatePacket(item));
}

/// <summary>Runs <see cref="ItemMaintenance"/> for the players of one map at the configured interval.</summary>
public sealed class ItemMaintenanceUpdater(ItemMechanicsOptions options, TimeProvider clock) : IMapUpdater
{
    private uint _elapsedMs;

    public void Update(Map map, uint diffMs)
    {
        _elapsedMs += diffMs;
        if (_elapsedMs < Math.Max(options.ZoneLimitCheckMs, 1))
        {
            return;
        }

        _elapsedMs = 0;
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        foreach (Player player in map.Players.ToList())
        {
            ItemMaintenance.Tick(player, now);
        }
    }

    public void OnPlayerRemoved(Map map, Player player) => ItemMaintenance.Forget(player);
}
