using System.Globalization;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.EventScenarios;

/// <summary>
/// <c>.playerbot eventscenario list|status [#event]</c> (Administrator; an ArcaneCore operator command), added under the
/// <c>.playerbot</c> root <see cref="PlayerbotCommands"/> defines. Read only: a scenario starts and stops with its game event
/// (<c>.event start|stop</c>), and needs no GM position. The replies are the lines <see cref="EventScenarioEngine"/> builds from its state.
/// </summary>
public sealed class EventScenarioCommands : ICommandExtension
{
    public string Path => "playerbot";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("eventscenario", AccountSecurity.Administrator,
            "Syntax: .playerbot eventscenario list\n.playerbot eventscenario status [#event]\nBot scenarios that start and stop with a game event (game_event_playerbot_* tables): which events have one, and for a running one how far each bot group is (claimed, online, at level, placed, posts or siege).",
            List,
            Children:
            [
                new ChatCommand("list", AccountSecurity.Administrator,
                    "Syntax: .playerbot eventscenario list\nThe game events that have a bot scenario, and whether each is running.",
                    List),
                new ChatCommand("status", AccountSecurity.Administrator,
                    "Syntax: .playerbot eventscenario status [#event]\nPer bot group of the event (or of every scenario event): bots claimed of wanted, online, at level, placed, posts set or the siege's state or last refusal, and bots short for lack of room.",
                    Status),
            ]),
    ];

    private static PlayerbotEventScenarioFeature? Feature(CommandContext context)
    {
        if (context.Session.Services.GetService<PlayerbotEventScenarioFeature>() is { } feature) return feature;
        context.Reply("The playerbot event scenarios are unavailable.");
        return null;
    }

    private static bool List(CommandContext context, string text)
    {
        if (text.Trim().Length != 0) return false;
        if (Feature(context) is not { } feature) return true;
        foreach (string line in feature.ListLines()) context.Reply(line);
        return true;
    }

    private static bool Status(CommandContext context, string text)
    {
        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1) return false;
        ushort? eventId = null;
        if (words.Length == 1)
        {
            if (!ushort.TryParse(words[0].TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out ushort parsed)) return false;
            eventId = parsed;
        }

        if (Feature(context) is not { } feature) return true;
        foreach (string line in feature.StatusLines(eventId)) context.Reply(line);
        return true;
    }
}
