using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;

namespace ArcaneCore.Game.Loot;

/// <summary>
/// Opens a container item (CMSG_OPEN_ITEM → vmangos Player::SendLoot for an item). Implemented by <see cref="ItemLootSource"/>;
/// <see cref="LootService.OpenItem"/> asks it once the item is known to be lootable and its owner alive.
/// </summary>
public interface IItemLootSource
{
    LootResult Open(Player player, Item item);
}

/// <summary>
/// What happens when a player closes the window of a special loot source (a fishing bobber or hole, a pickpocketed
/// creature, a disenchanted item): vmangos Player::DoLootRelease branches on the source type, here the owner of the source
/// supplies it. Called by <see cref="LootService.Release"/> after the window was closed and the client answered, instead of the
/// corpse / chest / item settlement. World thread.
/// </summary>
public interface ILootReleaseHandler
{
    /// <summary>The window of <paramref name="bag"/> closed for <paramref name="player"/> (<see cref="LootBag.Viewers"/> no longer holds them).</summary>
    void OnReleased(Player player, LootBag bag);
}