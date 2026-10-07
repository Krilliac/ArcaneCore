using System.Globalization;
using System.Reflection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Server;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Commands;

/// <summary>
/// The M6 command set, brought to the vmangos behaviour: help/commands/save/server info/server
/// motd for everyone; gps, gm chat for moderators; gm, kick for ticket masters; announce, notify and
/// modify money for basic administrators; saveall for administrators (the levels are applied by
/// <see cref="RetailCommandLevels"/>, Chat.cpp:1185-1366). Texts are the vmangos ones (GmStrings).
/// </summary>
public static class BuiltinCommands
{
    /// <summary>Player-requested saves are throttled to one per 20 seconds, as vmangos HandleSaveCommand does.</summary>
    public const uint PlayerSaveIntervalMs = 20_000;

    /// <summary>vmangos/cmangos MAX_MONEY_AMOUNT.</summary>
    public const uint MaxMoney = 0x7FFFFFFF - 1;

    public static CommandTable Create() => new(
    [
        new ChatCommand("help", AccountSecurity.Player, "Syntax: .help [command]\nDisplay usage instructions for the given command; without a command, the commands you can use.", Help),
        new ChatCommand("commands", AccountSecurity.Player, "Syntax: .commands\nDisplay a list of the commands available to you.", ListCommands),
        new ChatCommand("save", AccountSecurity.Player, "Syntax: .save\nSave your character.", Save),
        new ChatCommand("saveall", AccountSecurity.Moderator, "Syntax: .saveall\nSave all characters in the game.", SaveAll),
        new ChatCommand("server", AccountSecurity.Player, "Server status.", Children:
        [
            new ChatCommand("info", AccountSecurity.Player, "Syntax: .server info\nDisplay the server version, the players online and the uptime.", ServerInfo),
            new ChatCommand("motd", AccountSecurity.Player, "Syntax: .server motd\nShow the server message of the day.", ServerMotd),
            .. Ops.Lifecycle.ServerLifecycleCommands.Children, // shutdown/restart/idle* (docs/areas/ops-perf.md)
        ]),
        new ChatCommand("gps", AccountSecurity.Moderator, "Syntax: .gps\nDisplay the position of the selected player, or yours.", Gps),
        new ChatCommand("announce", AccountSecurity.Moderator, "Syntax: .announce $MessageToBroadcast\nSend a global message to all players online in chat log.", Announce),
        new ChatCommand("notify", AccountSecurity.Moderator, "Syntax: .notify $MessageToBroadcast\nSend a global message to all players online in screen.", Notify),
        new ChatCommand("gm", AccountSecurity.Moderator, "Syntax: .gm [on/off]\nEnable or disable GM mode, or show the current state.", GmMode,
        [
            new ChatCommand("chat", AccountSecurity.Moderator, "Syntax: .gm chat [on/off]\nEnable or disable the GM badge on your chat, or show the current state.", GmChat),
        ]),
        new ChatCommand("kick", AccountSecurity.GameMaster, "Syntax: .kick [$charactername]\nKick the given character, or the selected one, from the world.", Kick),
        new ChatCommand("modify", AccountSecurity.Moderator, "Syntax: .modify $subcommand", Children:
        [
            new ChatCommand("money", AccountSecurity.Moderator, "Syntax: .modify money #money\nAdd or remove money to the selected player; negative values take money (all of it when it would reach zero).", ModifyMoney),
        ]),
    ]);

    // vmangos HandleHelpCommand / HandleCommandsCommand (MiscCommands.cpp:37-58).
    private static bool Help(CommandContext context, string args)
    {
        IReadOnlyList<ChatCommand> roots = context.Commands.Roots;
        if (args.Length == 0)
        {
            context.Commands.ShowHelpForCommand(context, roots, "help");
            context.Commands.ShowHelpForCommand(context, roots, string.Empty);
        }
        else if (!context.Commands.ShowHelpForCommand(context, roots, args))
        {
            context.Reply(GmStrings.NoSuchCommand);
        }

        return true;
    }

    private static bool ListCommands(CommandContext context, string args)
    {
        context.Commands.ShowHelpForCommand(context, context.Commands.Roots, string.Empty);
        return true;
    }

    // vmangos HandleSaveCommand (CharacterCommands.cpp:1241-1259): staff save at once; everybody is told
    // "Player saved." (it says nothing about whether the save happened, so it cannot be used to probe
    // the save timer). A player's save is throttled to one per 20 seconds, ArcaneCore's stand-in for
    // vmangos' "only when the next autosave is more than 20 seconds away".
    private static bool Save(CommandContext context, string args)
    {
        Player player = context.Player;
        if (context.Security > AccountSecurity.Player)
        {
            context.World.SavePlayer(player);
            context.Reply(GmStrings.PlayerSaved);
            return true;
        }

        uint now = context.World.NowMs;
        if (player.LastSaveRequestMs is not { } last || now - last >= PlayerSaveIntervalMs)
        {
            player.LastSaveRequestMs = now;
            context.World.SavePlayer(player);
        }

        context.Reply(GmStrings.PlayerSaved);
        return true;
    }

    private static bool SaveAll(CommandContext context, string args)
    {
        context.World.SaveAll();
        context.Reply(GmStrings.PlayersSaved);
        return true;
    }

    private static bool ServerInfo(CommandContext context, string args)
    {
        // vmangos HandleServerInfoCommand (ServerCommands.cpp:302-316).
        string version = typeof(BuiltinCommands).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        ServerStats? stats = context.Session.Services.GetService<ServerStats>();
        int active = stats?.Active(context.World) ?? context.World.OnlinePlayerCount;
        context.Reply("Core revision: ArcaneCore " + version + " (WoW 1.12.1 build 5875)");
        context.Reply(GmStrings.PlayersOnline(active, 0, Math.Max(stats?.MaxActive ?? 0, active), 0));
        context.Reply(GmStrings.Uptime(GmDuration.SecsToTimeString((long)context.World.Uptime.TotalSeconds)));
        foreach (string line in ServerInfoDiagnostics.Format(ServerInfoDiagnostics.Capture(context.World, context.Session.Services)))
            context.Reply("Server diagnostics: " + line);
        return true;
    }

    private static bool ServerMotd(CommandContext context, string args)
    {
        // vmangos prints the motd as one text (no '@' split; that is the login greeting only).
        context.Reply(GmStrings.MotdCurrent(context.World.Options.Motd));
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

    // vmangos HandleAnnounceCommand: World::SendWorldText(LANG_SYSTEMMESSAGE) (ServerCommands.cpp:46-53).
    private static bool Announce(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        context.World.BroadcastToAll(WorldOpcode.SmsgMessagechat, ChatPackets.BuildSystemMessage(GmStrings.SystemMessage(args)));
        return true;
    }

    // vmangos HandleNotifyCommand: "Global notify: " + text as an SMSG_NOTIFICATION (ServerCommands.cpp:55-69).
    private static bool Notify(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        context.World.BroadcastToAll(WorldOpcode.SmsgNotification, ChatPackets.BuildNotification(GmStrings.GlobalNotifyPrefix + args));
        return true;
    }

    private static void SendNotification(CommandContext context, string text)
        => context.Session.Send(WorldOpcode.SmsgNotification, ChatPackets.BuildNotification(text));

    // vmangos HandleGMCommand (MiscCommands.cpp:103-123): no argument answers the state as a
    // notification only; "on"/"off" (only those, ExtractOnOff) switch it, with the chat line AND the
    // notification (Player::SetGameMaster(on, notify), Player.cpp:2624-2688).
    private static bool GmMode(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            SendNotification(context, context.Player.IsGameMaster ? GmStrings.GmOn : GmStrings.GmOff);
            return true;
        }

        if (!new CommandArgs(args).ExtractOnOff(out bool on))
        {
            context.Reply(GmStrings.UseOnOff);
            return true;
        }

        context.Player.SetGameMaster(on);
        string text = on ? GmStrings.GmOn : GmStrings.GmOff;
        context.Reply(text);
        SendNotification(context, text);
        return true;
    }

    // vmangos HandleGMChatCommand (MiscCommands.cpp:125-151) and Player::SetGMChat (Player.cpp:2602-2622).
    private static bool GmChat(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            SendNotification(context, context.Player.GmChat ? GmStrings.GmChatOn : GmStrings.GmChatOff);
            return true;
        }

        if (!new CommandArgs(args).ExtractOnOff(out bool on))
        {
            context.Reply(GmStrings.UseOnOff);
            return true;
        }

        context.Player.GmChat = on;
        string text = on ? GmStrings.GmChatOn : GmStrings.GmChatOff;
        context.Reply(text);
        SendNotification(context, text);
        return true;
    }

    // vmangos HandleKickPlayerCommand (AccountCommands.cpp:489-514): a name, a link or the selection;
    // not yourself; the HasLowerSecurity check; then "Player %s kicked.".
    private static bool Kick(CommandContext context, string args)
    {
        if (!GmTargets.TryPlayer(context, new CommandArgs(args), out Player target))
        {
            return true;
        }

        if (ReferenceEquals(target, context.Player))
        {
            context.Reply(GmStrings.CommandKickSelf);
            return true;
        }

        if (!context.CanActOn(target))
        {
            return true;
        }

        context.Reply(GmStrings.CommandKickMessage(GmStrings.PlayerLink(target.Name)));
        target.Session.Kick();
        return true;
    }

    /// <summary>
    /// vmangos HandleModifyMoneyCommand (CharacterCommands.cpp:4460-4523): a negative amount takes
    /// copper (everything when it would reach zero), a positive one gives it, and an amount of
    /// MAX_MONEY or more sets the purse to the maximum. The target is told unless it is the
    /// invoker. The quest-settlement guard is an ArcaneCore addition: a purse mid-settlement is
    /// not touched.
    /// </summary>
    private static bool ModifyMoney(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        Player? target = context.SelectedPlayerOrSelf();
        if (target is null)
        {
            context.Reply(GmStrings.NoCharSelected);
            return true;
        }

        if (!context.CanActOn(target))
        {
            return true;
        }

        if (!new CommandArgs(args).ExtractInt32(out int add))
        {
            return false;
        }

        if (target.IsQuestSettlementPending)
        {
            context.Reply("This player's quest reward is still settling.");
            return true;
        }

        string link = GmStrings.PlayerLink(target.Name);
        string caller = GmStrings.PlayerLink(context.Player.Name);
        bool report = !ReferenceEquals(target, context.Player);
        long money = target.Money;
        if (add < 0)
        {
            long remaining = money + add;
            if (remaining <= 0)
            {
                context.Reply(GmStrings.YouTakeAllMoney(link));
                if (report)
                {
                    target.SendSystemMessage(GmStrings.YoursAllMoneyGone(caller));
                }

                target.Money = 0;
            }
            else
            {
                context.Reply(GmStrings.YouTakeMoney(-(long)add, link));
                if (report)
                {
                    target.SendSystemMessage(GmStrings.YoursMoneyTaken(caller, -(long)add));
                }

                target.Money = (uint)Math.Min(remaining, MaxMoney);
            }
        }
        else
        {
            context.Reply(GmStrings.YouGiveMoney(add, link));
            if (report)
            {
                target.SendSystemMessage(GmStrings.YoursMoneyGiven(caller, add));
            }

            target.Money = add >= MaxMoney ? MaxMoney : (uint)Math.Min(money + add, MaxMoney);
        }

        return true;
    }
}
