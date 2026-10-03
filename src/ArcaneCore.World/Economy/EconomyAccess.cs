using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.World.Npc;

namespace ArcaneCore.World.Economy;

/// <summary>
/// Whether a player may use the mailbox they addressed. The gameobjects area replaces the
/// default (register a singleton) with a real range/type check; until then any game object
/// GUID is accepted (docs/integration/economy.md: known gaps).
/// </summary>
public interface IMailboxAccess
{
    bool CanUseMailbox(Player player, ObjectGuid mailbox);
}

/// <summary>Which auction house an auctioneer serves for a player, or null when it cannot be used.</summary>
public interface IAuctioneerAccess
{
    AuctionHouseEntry? FindHouse(Player player, ObjectGuid auctioneer);
}

/// <summary>Default mailbox check: alive, in the world, addressing a game object.</summary>
public sealed class DefaultMailboxAccess : IMailboxAccess
{
    public bool CanUseMailbox(Player player, ObjectGuid mailbox)
        => player.IsInWorld && player.IsAlive && mailbox.High == HighGuid.GameObject;
}

/// <summary>
/// Default auctioneer check: an interactable creature with UNIT_NPC_FLAG_AUCTIONEER
/// (QuestNpcServices.InteractableNpc), house chosen by its faction (goblin factions are neutral)
/// or the player's team (vmangos AuctionHouseMgr::GetAuctionHouseEntry).
/// </summary>
public sealed class DefaultAuctioneerAccess(QuestNpcFeature? npcs, EconomyOptions options) : IAuctioneerAccess
{
    public AuctionHouseEntry? FindHouse(Player player, ObjectGuid auctioneer)
        => npcs?.Services.InteractableNpc(player, auctioneer, NpcFlags.Auctioneer) is { } npc
            ? AuctionHouseRules.HouseFor(options, npc.FactionId, player.Team)
            : null;
}
