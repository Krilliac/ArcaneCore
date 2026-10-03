using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Teleport;

/// <summary>
/// <c>.recall</c>, <c>.goname</c> and <c>.namego</c> (vmangos TeleportCommands.cpp:1106-1346;
/// levels Chat.cpp:1236-1237,1259). Online players only: vmangos also moves offline characters
/// by writing their saved position, which ArcaneCore answers with "Player not found!" (see
/// docs/integration/gm-commands.md). Entering a dungeon through <c>.goname</c> does not create
/// the GM's instance bind and battleground bookkeeping vmangos does (no BG exists here); the
/// teleport service's own instance rules apply.
/// </summary>
public sealed class GmTeleportCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("recall", AccountSecurity.Moderator, "Syntax: .recall [$playername]\nTeleport the selected player (or the named one, or yourself) back to where it was before the last command teleport.", Recall, RetailLevel: 1),
        new ChatCommand("goname", AccountSecurity.Moderator, "Syntax: .goname [$charactername]\nTeleport to the given character, or the selected one.", Goname, RetailLevel: 2),
        new ChatCommand("namego", AccountSecurity.Moderator, "Syntax: .namego [$charactername]\nTeleport the given character, or the selected one, to you.", Namego, RetailLevel: 2),
    ];

    private static bool Recall(CommandContext context, string text)
    {
        if (!GmTargets.TryPlayer(context, new CommandArgs(text), out Player? target) || !context.CanActOn(target))
        {
            return true;
        }

        TeleportFeature feature = TeleportCommands.Feature(context);
        if (feature.Teleports.IsBeingTeleported(target))
        {
            context.Reply(GmStrings.IsTeleported(GmStrings.PlayerLink(target.Name)));
            return true;
        }

        if (!context.Session.Services.GetRequiredService<RecallPositions>().TryGet(target, out RecallPosition recall))
        {
            context.Reply(GmStrings.PlayerNotFound);
            return true;
        }

        TeleportCommands.GoHelper(context, target, recall.MapId, recall.X, recall.Y, recall.Z, recall.Orientation);
        return true;
    }

    private static bool Namego(CommandContext context, string text)
    {
        if (!GmTargets.TryPlayer(context, new CommandArgs(text), out Player? target))
        {
            return true;
        }

        Player caller = context.Player;
        if (ReferenceEquals(target, caller))
        {
            context.Reply(GmStrings.CantTeleportSelf);
            return true;
        }

        string link = GmStrings.PlayerLink(target.Name);
        if (!context.CanActOn(target))
        {
            return true;
        }

        TeleportFeature feature = TeleportCommands.Feature(context);
        if (feature.Teleports.IsBeingTeleported(target))
        {
            context.Reply(GmStrings.IsTeleported(link));
            return true;
        }

        context.Reply(GmStrings.Summoning(link));
        target.SendSystemMessage(GmStrings.SummonedBy(GmStrings.PlayerLink(caller.Name)));

        GmTeleports.BeginCommandTeleport(context, target);
        if (!feature.Teleports.TeleportTo(target, caller.MapId, caller.X, caller.Y, caller.Z, caller.Orientation))
        {
            TeleportCommands.ReplyInvalid(context, caller.X, caller.Y, caller.MapId);
        }

        return true;
    }

    private static bool Goname(CommandContext context, string text)
    {
        if (!GmTargets.TryPlayer(context, new CommandArgs(text), out Player? target))
        {
            return true;
        }

        Player caller = context.Player;
        if (ReferenceEquals(target, caller))
        {
            context.Reply(GmStrings.CantTeleportSelf);
            return true;
        }

        context.Reply(GmStrings.AppearingAt(GmStrings.PlayerLink(target.Name)));
        target.SendSystemMessage(GmStrings.AppearingTo(GmStrings.PlayerLink(caller.Name)));

        // "to point to see at pTarget with same orientation": 5 yards above, facing it (Object::GetAngle).
        float angle = MathF.Atan2(target.Y - caller.Y, target.X - caller.X);
        if (angle < 0)
        {
            angle += MathF.Tau;
        }

        GmTeleports.BeginCommandTeleport(context, caller);
        if (!TeleportCommands.Feature(context).Teleports.TeleportTo(caller, target.MapId, target.X, target.Y, target.Z + 5.0f, angle))
        {
            TeleportCommands.ReplyInvalid(context, target.X, target.Y, target.MapId);
        }

        return true;
    }
}

/// <summary>
/// <c>.tele name [$player] $location</c> (TeleportCommands.cpp:657-711, SEC_TICKETMASTER,
/// Chat.cpp:1006). With only a location the selected player (or the caller) is moved.
/// <c>.tele group</c>, <c>.tele add</c> and <c>.tele del</c> are not provided (see the lane doc).
/// </summary>
public sealed class TeleNameExtension : ICommandExtension
{
    public string Path => "tele";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("name", AccountSecurity.Moderator, "Syntax: .tele name [#playername] #location\nTeleport the named player, or the selected one, to a location from the game_tele table.", TeleName, RetailLevel: 2),
    ];

    private static bool TeleName(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string? nameArg = args.ExtractOptNotLastArg();
        if (!GmTargets.TryPlayer(context, nameArg, out Player? target))
        {
            return true;
        }

        TeleportFeature feature = TeleportCommands.Feature(context);
        GameTele? tele = null;
        if (args.ExtractGameTele(out uint id, out string? name))
        {
            tele = name is null
                ? feature.Maps.GameTeles.FirstOrDefault(t => t.Id == id)
                : feature.Maps.FindGameTele(name);
        }

        if (tele is null)
        {
            context.Reply(GmStrings.TeleNotFound);
            return true;
        }

        if (!context.CanActOn(target))
        {
            return true;
        }

        string link = GmStrings.PlayerLink(target.Name);
        if (feature.Teleports.IsBeingTeleported(target))
        {
            context.Reply(GmStrings.IsTeleported(link));
            return true;
        }

        context.Reply(GmStrings.TeleportingTo(link, string.Empty, tele.Name));
        if (!ReferenceEquals(target, context.Player))
        {
            target.SendSystemMessage(GmStrings.TeleportedToBy(GmStrings.PlayerLink(context.Player.Name)));
        }

        return TeleportCommands.GoHelper(context, target, tele.MapId, tele.X, tele.Y, tele.Z, tele.Orientation);
    }
}
