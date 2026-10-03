using System.Globalization;
using System.Reflection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Packets;

namespace ArcaneCore.World.Commands;

/// <summary>
/// The M6 command set. Names and security levels follow the cmangos-classic command table
/// (Chat.cpp): help/commands/save/server info/server motd for everyone; gps, announce,
/// notify, gm, saveall and modify money for moderators; kick for game masters.
/// </summary>
public static class BuiltinCommands
{
    /// <summary>Player-requested saves are throttled to one per 20 seconds, as vmangos HandleSaveCommand does.</summary>
    public const uint PlayerSaveIntervalMs = 20_000;

    /// <summary>vmangos/cmangos MAX_MONEY_AMOUNT.</summary>
    public const uint MaxMoney = 0x7FFFFFFF - 1;

    public static CommandTable Create() => new(
    [
        new ChatCommand("help", AccountSecurity.Player, "Syntax: .help [command] — what a command does; without a command, the commands you can use.", Help),
        new ChatCommand("commands", AccountSecurity.Player, "Syntax: .commands — the commands you can use.", ListCommands),
        new ChatCommand("save", AccountSecurity.Player, "Syntax: .save — save your character.", Save),
        new ChatCommand("saveall", AccountSecurity.Moderator, "Syntax: .saveall — save every online character.", SaveAll),
        new ChatCommand("server", AccountSecurity.Player, "Server status.", Children:
        [
            new ChatCommand("info", AccountSecurity.Player, "Syntax: .server info — version, players online and uptime.", ServerInfo),
            new ChatCommand("motd", AccountSecurity.Player, "Syntax: .server motd — the message of the day.", ServerMotd),
        ]),
        new ChatCommand("gps", AccountSecurity.Moderator, "Syntax: .gps — position of the selected player, or yours.", Gps),
        new ChatCommand("announce", AccountSecurity.Moderator, "Syntax: .announce $text — a system message to every player.", Announce),
        new ChatCommand("notify", AccountSecurity.Moderator, "Syntax: .notify $text — an on-screen notification to every player.", Notify),
        new ChatCommand("gm", AccountSecurity.Moderator, "Syntax: .gm [on|off] — GM mode (shows the current state without an argument).", GmMode,
        [
            new ChatCommand("chat", AccountSecurity.Moderator, "Syntax: .gm chat [on|off] — the GM badge on your chat.", GmChat),
        ]),
        new ChatCommand("kick", AccountSecurity.GameMaster, "Syntax: .kick $name — disconnect a player.", Kick),
        new ChatCommand("modify", AccountSecurity.Moderator, "Change a player.", Children:
        [
            new ChatCommand("money", AccountSecurity.Moderator, "Syntax: .modify money #copper — give (or, if negative, take) copper from the selected player or yourself.", ModifyMoney),
        ]),
    ]);

    private static bool Help(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            return ListCommands(context, args);
        }

        ChatCommand? command = context.Commands.Resolve(args, context.Security);
        if (command is null)
        {
            context.Reply("There is no such command.");
            return true;
        }

        context.Reply(command.Help);
        if (command.SubCommands.Any(c => context.Commands.IsAvailable(c, context.Security)))
        {
            context.Reply($"Subcommands: {context.Commands.ListNames(command.SubCommands, context.Security)}");
        }

        return true;
    }

    private static bool ListCommands(CommandContext context, string args)
    {
        context.Reply($"Commands available to you: {context.Commands.ListNames(context.Commands.Roots, context.Security)}");
        return true;
    }

    private static bool Save(CommandContext context, string args)
    {
        Player player = context.Player;
        if (context.Security > AccountSecurity.Player)
        {
            context.World.SavePlayer(player);
            context.Reply("Player saved.");
            return true;
        }

        // Players: silently at most once per interval (vmangos gives no feedback either, so
        // the command cannot be used to probe save timing).
        uint now = context.World.NowMs;
        if (player.LastSaveRequestMs is not { } last || now - last >= PlayerSaveIntervalMs)
        {
            player.LastSaveRequestMs = now;
            context.World.SavePlayer(player);
        }

        return true;
    }

    private static bool SaveAll(CommandContext context, string args)
    {
        context.World.SaveAll();
        context.Reply("All players saved.");
        return true;
    }

    private static bool ServerInfo(CommandContext context, string args)
    {
        string version = typeof(BuiltinCommands).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        TimeSpan uptime = context.World.Uptime;
        context.Reply($"ArcaneCore {version} (WoW 1.12.1 build 5875)");
        context.Reply($"Players online: {context.World.OnlinePlayerCount}. Uptime: {FormatDuration(uptime)}.");
        return true;
    }

    private static bool ServerMotd(CommandContext context, string args)
    {
        foreach (string line in context.World.Options.Motd.Split('@', StringSplitOptions.RemoveEmptyEntries))
        {
            context.Reply(line);
        }

        return true;
    }

    private static bool Gps(CommandContext context, string args)
    {
        Player? target = context.SelectedPlayerOrSelf();
        if (target is null)
        {
            context.Reply("No player selected.");
            return true;
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"{target.Name}: map {target.MapId}, zone {target.ZoneId}, X {target.X:F3}, Y {target.Y:F3}, Z {target.Z:F3}, orientation {target.Orientation:F3}"));
        return true;
    }

    private static bool Announce(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        context.World.BroadcastToAll(WorldOpcode.SmsgMessagechat,
            ChatPackets.BuildSystemMessage($"|cffff0000[{context.Player.Name} announces]:|r {args}"));
        return true;
    }

    private static bool Notify(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        context.World.BroadcastToAll(WorldOpcode.SmsgNotification, ChatPackets.BuildNotification(args));
        return true;
    }

    private static bool GmMode(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            context.Reply(context.Player.IsGameMaster ? "GM mode is ON." : "GM mode is OFF.");
            return true;
        }

        if (!TryParseOnOff(args, out bool on))
        {
            return false;
        }

        context.Player.SetGameMaster(on);
        string text = on ? "GM mode is ON." : "GM mode is OFF.";
        context.Reply(text);
        context.Session.Send(WorldOpcode.SmsgNotification, ChatPackets.BuildNotification(text));
        return true;
    }

    private static bool GmChat(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            context.Reply(context.Player.GmChat ? "GM chat badge is ON." : "GM chat badge is OFF.");
            return true;
        }

        if (!TryParseOnOff(args, out bool on))
        {
            return false;
        }

        context.Player.GmChat = on;
        context.Reply(on ? "GM chat badge is ON." : "GM chat badge is OFF.");
        return true;
    }

    private static bool Kick(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        Player? target = context.World.FindOnlinePlayer(args.Split(' ')[0]);
        if (target is null)
        {
            context.Reply("Player not found.");
            return true;
        }

        if (ReferenceEquals(target, context.Player))
        {
            context.Reply("You cannot kick yourself; log out instead.");
            return true;
        }

        if (!context.CanActOn(target))
        {
            return true;
        }

        context.Reply($"{target.Name} was kicked.");
        target.Session.Kick();
        return true;
    }

    private static bool ModifyMoney(CommandContext context, string args)
    {
        if (!int.TryParse(args, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int delta))
        {
            return false;
        }

        Player? target = context.SelectedPlayerOrSelf();
        if (target is null)
        {
            context.Reply("No player selected.");
            return true;
        }

        if (!context.CanActOn(target))
        {
            return true;
        }

        if (target.IsQuestSettlementPending)
        {
            context.Reply("This player's quest reward is still settling.");
            return true;
        }

        long updated = Math.Clamp((long)target.Money + delta, 0L, MaxMoney);
        target.Money = (uint)updated;
        context.Reply($"{target.Name} now has {updated} copper.");
        if (!ReferenceEquals(target, context.Player))
        {
            target.Session.Send(WorldOpcode.SmsgMessagechat,
                ChatPackets.BuildSystemMessage($"{context.Player.Name} changed your money by {delta} copper."));
        }

        return true;
    }

    /// <summary>"on"/"off" (and 1/0), as vmangos ExtractOnOff accepts.</summary>
    private static bool TryParseOnOff(string args, out bool on)
    {
        switch (args.Split(' ')[0].ToLowerInvariant())
        {
            case "on" or "1":
                on = true;
                return true;
            case "off" or "0":
                on = false;
                return true;
            default:
                on = false;
                return false;
        }
    }

    private static string FormatDuration(TimeSpan span)
        => span.TotalDays >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Hours}h {span.Minutes}m {span.Seconds}s");
}
