using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Crafting.Enchanting;

/// <summary>
/// The item hook that keeps enchantments paired with the equip state (vmangos Player::_ApplyItemMods, Player.cpp:6827-6860): when an item starts to count as
/// worn the wrapped hook applies the item's own bonuses first and the enchantments follow; when it stops, the enchantments are taken off first. The pair is
/// <see cref="PlayerInventory.StatsApplier"/>'s own (an item counts once while worn and unbroken), so applies and removes stay balanced through equip,
/// unequip, swap, break and repair. Whatever the enchantments changed in the stat fields is then folded into the derived values through the player's stat
/// maintainer (the wrapped hook may already have refreshed them before the enchantments moved).
/// </summary>
public sealed class EnchantStatsApplier(IItemStatsApplier inner) : IItemStatsApplier
{
    /// <summary>The wrapped hook.</summary>
    public IItemStatsApplier Inner => inner;

    public void Apply(Player player, Item item, byte slot, bool apply)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(item);
        if (player.Enchantments is not { } enchantments)
        {
            inner.Apply(player, item, slot, apply);
            return;
        }

        if (apply)
        {
            inner.Apply(player, item, slot, apply: true);
            enchantments.Apply(item, apply: true);
        }
        else
        {
            enchantments.Apply(item, apply: false, applyDuration: false);
            inner.Apply(player, item, slot, apply: false);
        }

        player.StatState.Maintainer?.UpdateAll(player);
    }
}

/// <summary>Ticks the enchantment timers of every player of a map (vmangos Player::Update → UpdateEnchantTime, Player.cpp:1304).</summary>
public sealed class EnchantUpdater : IMapUpdater
{
    public void Update(Map map, uint diffMs)
    {
        ArgumentNullException.ThrowIfNull(map);
        foreach (Player player in map.Players)
        {
            player.Enchantments?.Update(diffMs);
        }
    }

    public void OnPlayerRemoved(Map map, Player player)
    {
    }
}
