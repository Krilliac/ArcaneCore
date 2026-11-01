using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;

namespace ArcaneCore.World.Gm.Server;

/// <summary>
/// <c>.server set motd</c> (vmangos ServerCommands.cpp:322-323; SEC_ADMINISTRATOR, Chat.cpp:944-998), added under the
/// <c>.server</c> root of the built-in commands. <c>.server shutdown|restart|idleshutdown|idlerestart</c> are the ops
/// lane's <c>Ops.Lifecycle.ServerLifecycleCommands</c> (wave-2 integration: one shutdown implementation, which also owns
/// the process exit code and the supervisor contract).
/// <c>.server info</c> and <c>.server motd</c> are in <see cref="BuiltinCommands"/>.
/// <c>.server plimit</c> (vmangos ServerCommands.cpp:332-393, SEC_ADMINISTRATOR, Chat.cpp:994) sets the player limit or the realm
/// lock level of <see cref="WorldLoginAdmission"/>. <c>corpses</c>, <c>resetallraids</c>, <c>log</c> and <c>exit</c> are not
/// provided (no corpse expiry or raid-reset scheduler to drive; log and exit are console-only in vmangos).
/// </summary>
public sealed class ServerCommands : ICommandExtension
{
    public string Path => "server";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("set", AccountSecurity.Administrator, "Syntax: .server set $subcommand", Children:
        [
            new ChatCommand("motd", AccountSecurity.Administrator, "Syntax: .server set motd $MOTD\nSet the server message of the day.", SetMotd, RetailLevel: 6),
        ], RetailLevel: 6),
        new ChatCommand("plimit", AccountSecurity.Administrator,
            "Syntax: .server plimit [#count|-#level|reset|player|moderator|gamemaster|administrator]\nWithout an argument show the player limit and the minimum security level. A count limits the players online (0 = no limit); a negative number, or a security name, locks the realm to accounts of at least that level; reset restores the configured limit.",
            ServerPlimit, RetailLevel: 6),
    ];

    private static bool SetMotd(CommandContext context, string args)
    {
        context.World.Options.Motd = args;
        context.Reply(GmStrings.MotdChanged(args));
        return true;
    }

    private static readonly (string Name, int Limit)[] LevelNames =
    [
        ("player", -(int)AccountSecurity.Player),
        ("moderator", -(int)AccountSecurity.Moderator),
        ("gamemaster", -(int)AccountSecurity.GameMaster),
        ("administrator", -(int)AccountSecurity.Administrator),
    ];

    /// <summary>
    /// <c>.server plimit</c> (vmangos ServerCommands.cpp:332-393). The argument is a prefix of a level name or of <c>reset</c> (vmangos
    /// <c>strncmp(param, name, strlen(param))</c>), or a number; a number below the lowest lock is raised to it. vmangos then kicks the
    /// logged-in sessions below the lock only when the player-count limit is above 0 (ServerCommands.cpp:364), which a lock never is, so
    /// nothing is kicked: a lock keeps out later logins only. The lock level is written to the realm list off the world thread.
    /// </summary>
    private static bool ServerPlimit(CommandContext context, string args)
    {
        WorldLoginAdmission admission = context.Session.Services.GetRequiredService<WorldLoginAdmission>();
        string word = args.Trim();
        if (word.Length > 0)
        {
            int space = word.IndexOf(' ', StringComparison.Ordinal);
            if (space >= 0)
            {
                word = word[..space];
            }

            int? limit = ParseLimit(word, admission);
            if (limit is null)
            {
                return false;
            }

            admission.SetLimit(limit.Value);
        }

        context.Reply(PlimitStatus(admission));
        return true;
    }

    private static int? ParseLimit(string word, WorldLoginAdmission admission)
    {
        foreach ((string name, int limit) in LevelNames)
        {
            if (name.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            {
                return limit;
            }
        }

        if ("reset".StartsWith(word, StringComparison.OrdinalIgnoreCase))
        {
            return admission.DefaultLimit;
        }

        return int.TryParse(word, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value) ? value : null;
    }

    /// <summary>vmangos ServerCommands.cpp:390: "Player limits: amount %u, min. security level %s."</summary>
    private static string PlimitStatus(WorldLoginAdmission admission)
    {
        string security = admission.SecurityLimit switch
        {
            AccountSecurity.Player => "Player",
            AccountSecurity.Moderator => "Moderator",
            AccountSecurity.GameMaster => "Gamemaster",
            AccountSecurity.Administrator => "Administrator",
            _ => "<unknown>",
        };
        return string.Create(CultureInfo.InvariantCulture, $"Player limits: amount {admission.AmountLimit}, min. security level {security}.");
    }
}
