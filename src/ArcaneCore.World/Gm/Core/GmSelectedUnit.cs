using ArcaneCore.Game.Entities;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Atlas;
using ArcaneCore.World.Gm.Npc;

namespace ArcaneCore.World.Gm.Core;

/// <summary>vmangos ChatHandler::GetSelectedUnit (Chat.cpp:2616): self, online player, or a creature on the caller's map.</summary>
internal static class GmSelectedUnit
{
    public static Unit? Of(CommandContext context)
    {
        // A console-like invoker driven by a tool (the action channel) selects by guid: the unit the tool selected, else none.
        if (context.IsConsole && GmActionContext.Of(context) is { Primary: not 0 } click)
        {
            return GmActionContext.FindUnit(context, click.Primary);
        }

        if (context.Player.Selection.IsEmpty)
        {
            return context.Player;
        }

        return context.World.FindOnlinePlayer(context.Player.Selection)
            ?? (Unit?)GmNpcCommands.SystemOf(context)?.FindCreature(context.Player.Selection);
    }

    public static Unit? Require(CommandContext context)
    {
        Unit? target = Of(context);
        if (target is null)
        {
            context.Reply(GmStrings.SelectCharOrCreature);
        }

        return target;
    }
}
