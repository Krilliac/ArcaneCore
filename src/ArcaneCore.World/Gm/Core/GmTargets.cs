using ArcaneCore.Game.Entities;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;

namespace ArcaneCore.World.Gm.Core;

/// <summary>Target resolution shared by the GM commands.</summary>
public static class GmTargets
{
    /// <summary>
    /// vmangos ExtractPlayerTarget for online players: a name or <c>|Hplayer:|</c> link from
    /// <paramref name="args"/> when present, else the selected player (the invoker when nothing is
    /// selected). Replies "Player not found!" and returns false when there is none.
    /// </summary>
    public static bool TryPlayer(CommandContext context, CommandArgs args, out Player target)
    {
        if (PlayerTargetResolver.TryExtract(args, name => context.World.FindOnlinePlayer(name), context.SelectedPlayerOrSelf, out Player? found, out _)
            && found is not null)
        {
            target = found;
            return true;
        }

        context.Reply(GmStrings.PlayerNotFound);
        target = null!;
        return false;
    }

    /// <summary><see cref="TryPlayer(CommandContext, CommandArgs, out Player)"/> for an already extracted name argument (null: the selection).</summary>
    public static bool TryPlayer(CommandContext context, string? nameArg, out Player target)
        => TryPlayer(context, new CommandArgs(nameArg ?? string.Empty), out target);
}
