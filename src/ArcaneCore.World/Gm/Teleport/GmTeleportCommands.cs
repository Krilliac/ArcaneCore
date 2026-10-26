using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Battlegrounds;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Teleport;

/// <summary>
/// <c>.recall</c>, <c>.goname</c> and <c>.namego</c> (vmangos TeleportCommands.cpp:1106-1346;
/// levels Chat.cpp:1236-1237,1259). Online players only: vmangos also moves offline characters
/// by writing their saved position, which ArcaneCore answers with "Player not found!" (see
/// docs/integration/gm-commands.md). <c>.goname</c> to a player in a battleground binds the GM to that
/// match, and to a player in a dungeon instance binds the GM to that instance (TeleportCommands.cpp:1242-1303);
/// <c>.namego</c> from a battleground binds the summoned player to the caller's match (:1163-1168).
/// The bind is what lets the teleport service's own battleground and instance rules admit the move.
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

        // vmangos HandleNamegoCommand: summoned from a battleground, the player is bound to the caller's match
        // (a player in another match is taken out of it once the teleport was accepted, with no Deserter debuff).
        BattlegroundFeature? battlegrounds = context.Session.Services.GetService<BattlegroundFeature>();
        GmBattlegroundGo bound = battlegrounds?.BindToMatchOf(target, caller, leaveOtherMatch: true) ?? GmBattlegroundGo.NotBattleground;

        context.Reply(GmStrings.Summoning(link));
        target.SendSystemMessage(GmStrings.SummonedBy(GmStrings.PlayerLink(caller.Name)));

        GmTeleports.BeginCommandTeleport(context, target);
        if (!feature.Teleports.TeleportTo(target, caller.MapId, caller.X, caller.Y, caller.Z, caller.Orientation))
        {
            if (bound == GmBattlegroundGo.Bound)
            {
                battlegrounds!.RevertGmBinding(target);
            }

            TeleportCommands.ReplyInvalid(context, caller.X, caller.Y, caller.MapId);
        }
        else if (bound == GmBattlegroundGo.Bound)
        {
            battlegrounds!.CommitGmBinding(target);
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

        // vmangos HandleGonameCommand (:1242-1303): the target's map decides first. A battleground: refuse from another
        // battleground, else bind the GM to the target's match. A dungeon: refuse from another group, else bind the GM to
        // the target's instance. Without the bind the teleport service refuses the battleground map, or opens a new instance.
        BattlegroundFeature? battlegrounds = context.Session.Services.GetService<BattlegroundFeature>();
        GmBattlegroundGo bound = battlegrounds?.BindToMatchOf(caller, target, leaveOtherMatch: false) ?? GmBattlegroundGo.NotBattleground;
        if (bound == GmBattlegroundGo.InOtherMatch)
        {
            context.Reply(GmStrings.CannotGoToBgFromBg);
            return true;
        }

        if (bound == GmBattlegroundGo.NotBattleground
            && context.Session.Services.GetService<InstanceFeature>()?.Instances.PrepareGmGoToInstanceOf(caller, target) == GmInstanceGo.GroupMismatch)
        {
            context.Reply(GmStrings.CannotGoToInstParty);
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
            if (bound == GmBattlegroundGo.Bound)
            {
                battlegrounds!.RevertGmBinding(caller);
            }

            TeleportCommands.ReplyInvalid(context, target.X, target.Y, target.MapId);
        }
        else if (bound == GmBattlegroundGo.Bound)
        {
            battlegrounds!.CommitGmBinding(caller);
        }

        return true;
    }
}

/// <summary>
/// <c>.tele name [$player] $location</c> (TeleportCommands.cpp:657-711, SEC_TICKETMASTER,
/// Chat.cpp:1006). With only a location the selected player (or the caller) is moved.
/// <c>.tele group</c> is in Gm/Extra/GroupTeleportCommands.cs; <c>.tele add</c> and <c>.tele del</c> are not provided here (see the lane doc).
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
        if (!context.IsInvoker(target))
        {
            target.SendSystemMessage(GmStrings.TeleportedToBy(GmStrings.PlayerLink(context.InvokerName)));
        }

        return TeleportCommands.GoHelper(context, target, tele.MapId, tele.X, tele.Y, tele.Z, tele.Orientation);
    }
}
