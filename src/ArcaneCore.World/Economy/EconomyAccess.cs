using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Npc;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Npc;

namespace ArcaneCore.World.Economy;

/// <summary>
/// Whether a player may use the mailbox they addressed. Without a registered implementation the
/// feature picks one from <see cref="EconomyOptions.MailboxAccess"/>: <see cref="GameObjectMailboxAccess"/>
/// (retail) or <see cref="PermissiveMailboxAccess"/>. Register a singleton to replace both.
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

/// <summary>
/// Retail mailbox check (vmangos WorldSession::CheckMailBox → Player::GetGameObjectIfCanInteractWith(guid,
/// GAMEOBJECT_TYPE_MAILBOX), cMaNGOS Classic MailHandler.cpp:44 likewise): the player is in the world and
/// alive, and the GUID names a spawned mailbox game object in the player's own map within
/// <see cref="GameObjectMapSystem.InteractionDistance"/> (<see cref="GameObjectMapSystem.FindInteractable"/>).
/// With no game object feature (no content) nothing can be a mailbox, so every request is refused.
/// Not modelled: the taxi-flight and lost-control refusals (no such player state exists yet); the
/// object system also refuses a NoInteract object, which vmangos does not check for mailboxes.
/// </summary>
public sealed class GameObjectMailboxAccess(Func<GameObjectLootFeature?> gameObjects) : IMailboxAccess
{
    public bool CanUseMailbox(Player player, ObjectGuid mailbox)
        => player.IsInWorld && player.IsAlive && mailbox.High == HighGuid.GameObject
            && player.Map is { } map
            && gameObjects()?.FindSystem(map)?.FindInteractable(player, mailbox, GameObjectType.Mailbox) is not null;
}

/// <summary>
/// <see cref="MailboxAccessMode.Permissive"/>: alive, in the world, addressing any game object GUID.
/// A deliberate deviation from retail for synthetic hosts and servers without game object content.
/// </summary>
public sealed class PermissiveMailboxAccess : IMailboxAccess
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
