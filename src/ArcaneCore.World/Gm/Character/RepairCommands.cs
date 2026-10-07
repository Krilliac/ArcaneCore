using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;

namespace ArcaneCore.World.Gm.Character;

/// <summary><c>.repairitems</c>: free GM durability repair for an online player or the invoker.</summary>
public sealed class RepairCommands : ICommandGroup
{
    private const string SettlingText = "This player's quest reward is still settling.";

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("repairitems", AccountSecurity.GameMaster,
            "Syntax: .repairitems [#itemGuid]\nRepair durability on the selected online player or yourself.", Repair, RetailLevel: 3),
    ];

    private static bool Repair(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractOptUInt32(out uint itemGuid, 0) || !args.IsEmpty)
        {
            return false;
        }

        Player? target = context.SelectedPlayerOrSelf();
        if (target is null)
        {
            context.Reply(GmStrings.PlayerNotFound);
            return true;
        }

        if (!context.CanActOn(target))
        {
            return true;
        }

        if (!target.CanMutateQuestSettlementState)
        {
            context.Reply(SettlingText);
            return true;
        }

        IReadOnlyList<Item> candidates = target.Inventory.RepairCandidates(
            itemGuid == 0 ? default : ObjectGuid.Item(itemGuid));
        int repaired = 0;
        foreach (Item item in candidates)
        {
            uint before = item.Durability;
            target.Inventory.RepairDurability(item);
            if (item.Durability != before)
            {
                repaired++;
            }
        }

        if (repaired > 0)
        {
            context.World.SavePlayer(target);
        }

        context.Reply(repaired == 0 ? "No damaged items found." : $"Repaired {repaired} item(s).");
        return true;
    }
}
