using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Teleport;
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
/// docs/integration/gm-commands.md). <c>.goname</c> to a player in a battleground sends the GM to that
/// match, and to a player in a dungeon instance binds the GM to that instance (TeleportCommands.cpp:1242-1303);
/// <c>.namego</c> from a battleground sends the summoned player to the caller's match (:1163-1168).
/// A battleground move is a pending record (<see cref="BattlegroundFeature.BeginGmMove"/>) that changes no binding until the subject
/// arrives in the match's map; the record is what lets the teleport service's own battleground rules admit the one move.
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

        // vmangos HandleNamegoCommand: summoned from a battleground, the player goes to the caller's match. Nothing is bound here: a move
        // record is made, and the arrival in the match's map binds the player (a player in another match is taken out of it then, with
        // no Deserter debuff; until then it is still a member of that match).
        BattlegroundFeature? battlegrounds = context.Session.Services.GetService<BattlegroundFeature>();
        GmBattlegroundGo bound = battlegrounds?.BeginGmMove(target, caller, leaveOtherMatch: true) ?? GmBattlegroundGo.NotBattleground;
        if (bound == GmBattlegroundGo.MoveInProgress)
        {
            context.Reply(GmStrings.IsTeleported(link));
            return true;
        }

        context.Reply(GmStrings.Summoning(link));
        target.SendSystemMessage(GmStrings.SummonedBy(GmStrings.PlayerLink(caller.Name)));

        GmTeleports.BeginCommandTeleport(context, target);
        // vmangos HandleNamegoCommand (TeleportCommands.cpp:1187) passes TELE_TO_NOT_LEAVE_COMBAT: a summon inside the target's map keeps
        // its combat (a far teleport ends it, Player.cpp:2021).
        if (!MoveBound(feature.Teleports, battlegrounds, bound, target, caller, caller.X, caller.Y, caller.Z, caller.Orientation, TeleportOptions.NotLeaveCombat))
        {
            TeleportCommands.ReplyInvalid(context, caller.X, caller.Y, caller.MapId);
        }

        return true;
    }

    /// <summary>
    /// Teleport <paramref name="subject"/> to the position on <paramref name="anchor"/>'s map. A battleground move that
    /// <see cref="BattlegroundFeature.BeginGmMove"/> recorded (<see cref="GmBattlegroundGo.Bound"/>) binds nothing here: the arrival
    /// (<c>BattlegroundFeature.OnPlayerEnteredMap</c>) commits it in the match's map and drops it anywhere else, since a far teleport can
    /// still be refused at map entry; the record is cancelled here when the teleport is refused or throws. A teleport onto another instance
    /// of the subject's own map id is forced far (vmangos <c>.goname</c> sets TELE_TO_FORCE_MAP_CHANGE when the battleground id changes;
    /// it would otherwise be a near teleport, which has no arrival); one inside the subject's own map object has no arrival either, so it is
    /// committed at once.
    /// </summary>
    private static bool MoveBound(TeleportService teleports, BattlegroundFeature? battlegrounds, GmBattlegroundGo bound, Player subject, Player anchor, float x, float y, float z, float o, TeleportOptions baseOptions = TeleportOptions.None)
    {
        bool binds = bound == GmBattlegroundGo.Bound && battlegrounds is not null;
        bool sameMap = ReferenceEquals(subject.Map, anchor.Map);
        TeleportOptions options = baseOptions | (binds && !sameMap ? TeleportOptions.ForceFar : TeleportOptions.None); // vmangos TELE_TO_FORCE_MAP_CHANGE
        bool moved = false;
        try
        {
            moved = teleports.TeleportTo(subject, anchor.MapId, x, y, z, o, options);
        }
        finally
        {
            if (binds && !moved)
            {
                battlegrounds!.CancelGmMove(subject);
            }
        }

        if (binds && moved && sameMap)
        {
            battlegrounds!.CommitGmMove(subject);
        }

        return moved;
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

        // vmangos has the IsBeingTeleported check only in .namego; a GM that is still loading would race its own pending move.
        TeleportFeature feature = TeleportCommands.Feature(context);
        if (feature.Teleports.IsBeingTeleported(caller))
        {
            context.Reply(GmStrings.IsTeleported(GmStrings.PlayerLink(caller.Name)));
            return true;
        }

        // vmangos HandleGonameCommand (:1242-1303): the target's map decides first. A battleground: refuse from another
        // battleground, else send the GM to the target's match (a move record; the arrival binds). A dungeon: refuse from another group,
        // else bind the GM to the target's instance. Without the record the teleport service refuses the battleground map, or opens a new instance.
        BattlegroundFeature? battlegrounds = context.Session.Services.GetService<BattlegroundFeature>();
        GmBattlegroundGo bound = battlegrounds?.BeginGmMove(caller, target, leaveOtherMatch: false) ?? GmBattlegroundGo.NotBattleground;
        if (bound == GmBattlegroundGo.MoveInProgress)
        {
            context.Reply(GmStrings.IsTeleported(GmStrings.PlayerLink(caller.Name)));
            return true;
        }

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
        if (!MoveBound(feature.Teleports, battlegrounds, bound, caller, target, target.X, target.Y, target.Z + 5.0f, angle))
        {
            TeleportCommands.ReplyInvalid(context, target.X, target.Y, target.MapId);
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
