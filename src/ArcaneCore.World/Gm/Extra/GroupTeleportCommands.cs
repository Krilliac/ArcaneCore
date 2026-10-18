using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Teleport;
using ArcaneCore.World.Social;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Extra;

/// <summary>Group lookups shared by the group commands.</summary>
internal static class GmGroups
{
    /// <summary>The group of <paramref name="player"/>, or null (also without the social feature).</summary>
    public static Group? Of(CommandContext context, Player player)
    {
        try
        {
            return context.Session.Services.GetService<SocialFeature>()?.Context.Groups.GetGroup(player.Guid);
        }
        catch (InvalidOperationException)
        {
            return null; // the social feature is not attached in this host
        }
    }

    /// <summary>The online members of <paramref name="group"/>.</summary>
    public static IEnumerable<Player> Online(CommandContext context, Group group)
        => group.Members.Select(m => context.World.FindOnlinePlayer(m.Guid)).OfType<Player>();
}

/// <summary>
/// <c>.tele group $location</c> (vmangos HandleTeleGroupCommand, TeleportCommands.cpp: the selected player's group to a game_tele
/// location) and <c>.groupgo [$player]</c> (vmangos HandleGroupgoCommand: summon the group of the named or selected player to you). Every
/// member must be one the invoker may act on (the command stops at the first that is not); a member already being teleported is skipped
/// with a line, and .groupgo leaves the invoker where it is.
/// </summary>
public sealed class TeleGroupExtension : ICommandExtension
{
    public string Path => "tele";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("group", AccountSecurity.Moderator,
            "Syntax: .tele group $location\nTeleport the group of the selected player (or your own) to a location from the game_tele table.",
            TeleGroup, RetailLevel: 2),
    ];

    private static bool TeleGroup(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        TeleportFeature feature = TeleportCommands.Feature(context);
        GameTele? tele = null;
        if (args.ExtractGameTele(out uint id, out string? name))
        {
            tele = name is null ? feature.Maps.GameTeles.FirstOrDefault(t => t.Id == id) : feature.Maps.FindGameTele(name);
        }

        if (tele is null)
        {
            context.Reply(GmStrings.TeleNotFound);
            return true;
        }

        if (!GmTargets.TryPlayer(context, (string?)null, out Player leader) || !context.CanActOn(leader))
        {
            return true;
        }

        Player[] members = GmGroups.Of(context, leader) is { } group ? [.. GmGroups.Online(context, group)] : [leader];
        foreach (Player member in members)
        {
            if (!context.CanActOn(member))
            {
                return true; // vmangos stops at the first member of higher security
            }
        }

        foreach (Player member in members)
        {
            string link = GmStrings.PlayerLink(member.Name);
            if (feature.Teleports.IsBeingTeleported(member))
            {
                context.Reply(GmStrings.IsTeleported(link));
                continue;
            }

            context.Reply(GmStrings.TeleportingTo(link, string.Empty, tele.Name));
            if (!context.IsInvoker(member))
            {
                member.SendSystemMessage(GmStrings.TeleportedToBy(GmStrings.PlayerLink(context.InvokerName)));
            }

            TeleportCommands.GoHelper(context, member, tele.MapId, tele.X, tele.Y, tele.Z, tele.Orientation);
        }

        return true;
    }
}

/// <summary><c>.groupgo [$player]</c>: summon the group of the named or selected player to you.</summary>
public sealed class GroupGoCommand : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("groupgo", AccountSecurity.Moderator,
            "Syntax: .groupgo [$charactername]\nSummon the group of the named or selected player to you (the player alone when not in a group).",
            GroupGo, RetailLevel: 2),
    ];

    private static bool GroupGo(CommandContext context, string text)
    {
        if (!GmTargets.TryPlayer(context, new CommandArgs(text), out Player target) || !context.CanActOn(target))
        {
            return true;
        }

        Player caller = context.Player;
        Player[] members = GmGroups.Of(context, target) is { } group ? [.. GmGroups.Online(context, group)] : [target];
        foreach (Player member in members)
        {
            if (!context.CanActOn(member))
            {
                return true;
            }
        }

        TeleportFeature feature = TeleportCommands.Feature(context);
        int moved = 0;
        foreach (Player member in members)
        {
            if (ReferenceEquals(member, caller))
            {
                continue;
            }

            string link = GmStrings.PlayerLink(member.Name);
            if (feature.Teleports.IsBeingTeleported(member))
            {
                context.Reply(GmStrings.IsTeleported(link));
                continue;
            }

            context.Reply(GmStrings.Summoning(link));
            member.SendSystemMessage(GmStrings.SummonedBy(GmStrings.PlayerLink(caller.Name)));
            GmTeleports.BeginCommandTeleport(context, member);
            if (feature.Teleports.TeleportTo(member, caller.MapId, caller.X, caller.Y, caller.Z, caller.Orientation))
            {
                moved++;
            }
            else
            {
                TeleportCommands.ReplyInvalid(context, caller.X, caller.Y, caller.MapId);
            }
        }

        if (moved == 0 && members.All(m => ReferenceEquals(m, caller)))
        {
            context.Reply(GmStrings.CantTeleportSelf);
        }

        return true;
    }
}
