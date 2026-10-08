using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;

namespace ArcaneCore.World.Gm.Items;

/// <summary>
/// <c>.additem</c> and <c>.deleteitem</c> (vmangos CharacterCommands.cpp:3267-3450, both
/// SEC_GAMEMASTER, Chat.cpp:1277-1278).
/// <para>
/// Differences from vmangos, all deliberate and documented in docs/integration/gm-commands.md:
/// the target-rank check (<see cref="CommandContext.CanActOn"/>; vmangos has none on these commands); the quest-settlement guard (a player whose quest reward is settling cannot have items changed);
/// offline players are refused with "Player not found!" (vmangos edits the characters DB);
/// a worn item that cannot come off right now (combat) is not removed (vmangos skips the unequip
/// check); the "not enough items" reply of a negative <c>.additem</c> is the English text of
/// <c>.deleteitem</c> (vmangos' own is French).
/// </para>
/// </summary>
public sealed class ItemCommands : ICommandGroup
{
    private const string SettlingText = "This player's quest reward is still settling.";

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("additem", AccountSecurity.GameMaster, "Syntax: .additem #itemId|[#itemName]|#shift-click-item-link #itemCount\nAdds the item to the selected player (or yourself); a negative count removes items, bank included in the check.", AddItem, RetailLevel: 3),
        new ChatCommand("deleteitem", AccountSecurity.GameMaster, "Syntax: .deleteitem #itemId|[#itemName]|#shift-click-item-link #itemCount [#playerName]\nRemoves items from the named or selected player (or yourself), bank included.", DeleteItem, RetailLevel: 3),
    ];

    private enum IdRead
    {
        /// <summary>No argument: show the syntax.</summary>
        Syntax,

        /// <summary>An error reply was already sent.</summary>
        Replied,

        Ok,
    }

    /// <summary>
    /// The item id of an <c>Hitem</c> link, number or exact item name (vmangos queries
    /// <c>item_template.name = '...'</c>, which MySQL matches case-insensitively and answers in
    /// primary-key order; the lowest entry wins among duplicate names).
    /// </summary>
    private static IdRead ReadItemId(CommandContext context, CommandArgs args, out uint itemId)
    {
        itemId = 0;
        string? read = args.ExtractKeyFromLink("Hitem", out _, out _);
        if (read is null)
        {
            return IdRead.Syntax;
        }

        if (new CommandArgs(read).ExtractUInt32(out itemId))
        {
            return IdRead.Ok;
        }

        if (LiveItemTemplateStore.Unwrap(context.Player.Inventory.Templates) is { } store
            && store.All.Where(t => t.Name.Equals(read, StringComparison.OrdinalIgnoreCase)).MinBy(t => t.Entry) is { } match)
        {
            itemId = match.Entry;
            return IdRead.Ok;
        }

        context.Reply(GmStrings.CouldNotFind(read));
        return IdRead.Replied;
    }

    private static bool AddItem(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        switch (ReadItemId(context, args, out uint itemId))
        {
            case IdRead.Syntax:
                return false;
            case IdRead.Replied:
                return true; // vmangos SetSentErrorMessage: no syntax line
        }

        if (!args.ExtractOptInt32(out int count, 1))
        {
            return false;
        }

        Player caller = context.Player;
        Player target = context.SelectedPlayerOrSelf() ?? caller;   // vmangos: a non-player selection falls back to the caller
        if (!context.CanActOn(target))
        {
            return true;
        }

        PlayerInventory inventory = target.Inventory;
        if (inventory.Templates.Find(itemId) is not { } template)
        {
            context.Reply(GmStrings.ItemIdInvalid(itemId));
            return true;
        }

        if (target.IsQuestSettlementPending)
        {
            context.Reply(SettlingText);
            return true;
        }

        if (count < 0)
        {
            uint remove = (uint)-(long)count;
            uint held = inventory.GetItemCount(itemId, inBankAlso: true);
            if (held < remove)
            {
                context.Reply(GmStrings.CannotRemoveItems(remove, itemId, held));
                return true;
            }

            inventory.DestroyItemCount(itemId, remove, includeBank: false);   // CharacterCommands.cpp:3350 passes no inBankAlso
            context.Reply(GmStrings.RemoveItem(itemId, remove, GmStrings.PlayerLink(target.Name)));
            return true;
        }

        var dest = new List<ItemPosCount>();
        uint wanted = (uint)count;
        InventoryResult result = inventory.CanStoreNewItem(itemId, wanted, dest, out uint noSpace);
        if (result != InventoryResult.Ok)
        {
            wanted -= noSpace;   // convert to the possible store amount
        }
        else
        {
            noSpace = 0;
        }

        if (wanted == 0 || dest.Count == 0)
        {
            context.Reply(GmStrings.ItemCannotCreate(itemId, noSpace));
            return true;
        }

        Item item = inventory.StoreNewItem(dest, template, wanted);

        // Remove the binding on a self grant, so the GM can hand the item to someone else later.
        if (ReferenceEquals(caller, target))
        {
            foreach (ItemPosCount position in dest)
            {
                inventory.GetItem(position.Bag, position.Slot)?.SetBinding(false);
            }
        }

        // CharacterCommands.cpp:3369-3372: the caller sees "you created", the target "you received".
        caller.Session.Send(WorldOpcode.SmsgItemPushResult, ItemPackets.ItemPushResult(caller.Guid, item, wanted, received: false, created: true, showInChat: true));
        if (!ReferenceEquals(caller, target))
        {
            target.Session.Send(WorldOpcode.SmsgItemPushResult, ItemPackets.ItemPushResult(target.Guid, item, wanted, received: true, created: false, showInChat: true));
        }

        if (noSpace > 0)
        {
            context.Reply(GmStrings.ItemCannotCreate(itemId, noSpace));
        }

        return true;
    }

    private static bool DeleteItem(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        switch (ReadItemId(context, args, out uint itemId))
        {
            case IdRead.Syntax:
                return false;
            case IdRead.Replied:
                return true;
        }

        if (!args.ExtractOptUInt32(out uint count, 1))
        {
            return false;
        }

        if (!PlayerTargetResolver.TryExtract(args, name => context.World.FindOnlinePlayer(name), context.SelectedPlayerOrSelf,
                out Player? target, out _) || target is null)
        {
            context.Reply(GmStrings.PlayerNotFound);
            return true;
        }

        if (!context.CanActOn(target))
        {
            return true;
        }

        if (target.IsQuestSettlementPending)
        {
            context.Reply(SettlingText);
            return true;
        }

        uint held = target.Inventory.GetItemCount(itemId, inBankAlso: true);
        if (held < count)
        {
            context.Reply(GmStrings.CannotRemoveItems(count, itemId, held));
            return true;
        }

        target.Inventory.DestroyItemCount(itemId, count, includeBank: true);
        context.World.SavePlayer(target);   // vmangos SaveInventoryAndGoldToDB
        return true;
    }
}
