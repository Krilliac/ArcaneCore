using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Net.SessionTrace;

/// <summary>
/// <c>.debug trace session [$name] on|off [#minutes]</c> and <c>.debug trace session list</c>: turn the per-session trace
/// (<see cref="SessionTraceFeature"/>) of one character on or off, or list the ones that are on. Game Master (retail level 3, the level of
/// the rest of <c>.debug</c>). World thread, like every command handler.
/// </summary>
public sealed class SessionTraceCommands : ICommandExtension
{
    private const string SessionHelp =
        "Syntax: .debug trace session [$name] on|off [#minutes]\n"
        + "Turn the per-session trace of an online character on (for #minutes, default Diagnostics:SessionTrace:DefaultMinutes, at most MaxMinutes) or off. "
        + "Without a name the selected player, or you, is traced. The trace writes the character's packets, movement, auras, deaths and anti-cheat verdicts "
        + "to the log category ArcaneCore.SessionTrace, and survives a relog until it is turned off or runs out.";

    /// <inheritdoc />
    public string Path => "debug";

    /// <inheritdoc />
    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("trace", AccountSecurity.GameMaster, "Syntax: .debug trace session [$name] on|off [#minutes]\nPer-session diagnostics traces.",
            Children:
            [
                new ChatCommand("session", AccountSecurity.GameMaster, SessionHelp, Session,
                    Children:
                    [
                        new ChatCommand("list", AccountSecurity.GameMaster, "Syntax: .debug trace session list\nList the session traces that are on: character, guid, endpoint, minutes left, lines written and dropped.", List, RetailLevel: 3),
                    ],
                    RetailLevel: 3),
            ],
            RetailLevel: 3),
    ];

    private static bool Session(CommandContext context, string args)
    {
        string[] words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int next = 0;
        string? name = null;
        if (words.Length > 0 && !IsSwitch(words[0]))
        {
            name = words[0];
            next = 1;
        }

        if (next >= words.Length || !IsSwitch(words[next]))
        {
            return false;
        }

        bool on = string.Equals(words[next], "on", StringComparison.OrdinalIgnoreCase);
        next++;
        int? minutes = null;
        if (words.Length - next > (on ? 1 : 0))
        {
            return false;
        }

        if (words.Length > next)
        {
            if (!long.TryParse(words[next], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                return false;
            }

            minutes = (int)Math.Clamp(parsed, int.MinValue, int.MaxValue);
        }

        if (FeatureOf(context) is not { } feature)
        {
            context.Reply("Session tracing is not available.");
            return true;
        }

        return on ? TurnOn(context, feature, name, minutes) : TurnOff(context, feature, name);
    }

    private static bool IsSwitch(string word) => string.Equals(word, "on", StringComparison.OrdinalIgnoreCase) || string.Equals(word, "off", StringComparison.OrdinalIgnoreCase);

    private static SessionTraceFeature? FeatureOf(CommandContext context) => context.Session.Services.GetService<SessionTraceFeature>();

    private static bool TurnOn(CommandContext context, SessionTraceFeature feature, string? name, int? minutes)
    {
        Player? player;
        if (name is null)
        {
            player = context.SelectedPlayerOrSelf();
            if (player is null)
            {
                context.Reply("No player selected.");
                return true;
            }

            if (!context.CanActOn(player))
            {
                return true;
            }
        }
        else
        {
            player = context.World.FindOnlinePlayer(name);
            if (player is null)
            {
                context.Reply("Player is not online.");
                return true;
            }

            // The same security check as the selected-player form: a GM cannot trace a character of higher security.
            if (!context.CanActOn(player))
            {
                return true;
            }
        }

        SessionTraceStartResult result = feature.Start(player, minutes);
        switch (result.Status)
        {
            case SessionTraceStartStatus.Disabled:
                context.Reply($"Session tracing is disabled by {SessionTraceOptions.SectionName}:Enabled.");
                break;
            case SessionTraceStartStatus.TooMany:
                context.Reply(string.Create(CultureInfo.InvariantCulture, $"At most {feature.Options.MaxTracedCharacters} characters can be traced at once."));
                break;
            default:
                context.Reply(string.Create(CultureInfo.InvariantCulture,
                    $"Session trace on for {player.Name} (guid {player.Guid.Low}) for {result.Minutes} min{(result.Clamped ? $" (clamped to {result.Minutes} min)" : string.Empty)}."));
                break;
        }

        return true;
    }

    private static bool TurnOff(CommandContext context, SessionTraceFeature feature, string? name)
    {
        ObjectGuid guid;
        string display;
        if (name is null)
        {
            Player? player = context.SelectedPlayerOrSelf();
            if (player is null)
            {
                context.Reply("No player selected.");
                return true;
            }

            if (!context.CanActOn(player))
            {
                return true;
            }

            guid = player.Guid;
            display = player.Name;
        }
        else if (context.World.FindOnlinePlayer(name) is { } online)
        {
            if (!context.CanActOn(online))
            {
                return true;
            }

            guid = online.Guid;
            display = online.Name;
        }
        else if (feature.FindByName(name) is { } offline)
        {
            // A trace outlives the connection: a character that dropped off can still be turned off by name.
            guid = offline.Guid;
            display = offline.Character;
        }
        else
        {
            context.Reply($"{name} is not traced.");
            return true;
        }

        if (!feature.Stop(guid, out SessionTrace? stopped) || stopped is null)
        {
            context.Reply($"{display} is not traced.");
            return true;
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture, $"Session trace off for {display}: {stopped.Lines} lines, {stopped.Dropped} dropped."));
        return true;
    }

    private static bool List(CommandContext context, string args)
    {
        if (FeatureOf(context) is not { } feature)
        {
            context.Reply("Session tracing is not available.");
            return true;
        }

        IReadOnlyList<string> rows = feature.DescribeActive();
        if (rows.Count == 0)
        {
            context.Reply("No session traces.");
            return true;
        }

        context.Reply(string.Join('\n', rows));
        return true;
    }
}
