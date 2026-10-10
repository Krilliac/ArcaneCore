using System.Globalization;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// <c>.playerbot population [status|enable|disable|target N]</c> (Administrator; an ArcaneCore operator command, not a vmangos one),
/// added under the <c>.playerbot</c> root <see cref="PlayerbotCommands"/> defines. <c>enable</c>, <c>disable</c> and <c>target</c> set the
/// live <c>World:Playerbots:Population</c> options in place; like every command-set value they last until the next <c>.reload config</c>
/// (which re-applies the file) or restart, so the permanent value belongs in the configuration file.
/// </summary>
public sealed class PlayerbotPopulationCommands : ICommandExtension
{
    private const string Persistence = "Set for this run only: .reload config or a restart restores World:Playerbots:Population from the configuration file.";

    public string Path => "playerbot";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("population", AccountSecurity.Administrator,
            "Syntax: .playerbot population [status|enable|disable|target #count]\nKeep a number of bots online, spread over the level bands, as long as the world's tick time allows.",
            Status,
            Children:
            [
                new ChatCommand("status", AccountSecurity.Administrator,
                    "Syntax: .playerbot population status\nShow the population manager: its state and why, the tick p95 against the budget, the target and the running, owned and parked bots, and want against have for each level band.",
                    Status),
                new ChatCommand("enable", AccountSecurity.Administrator,
                    "Syntax: .playerbot population enable\nTurn the population manager on until the next .reload config or restart (World:Playerbots:Population:Enabled).",
                    Enable),
                new ChatCommand("disable", AccountSecurity.Administrator,
                    "Syntax: .playerbot population disable\nTurn the population manager off until the next .reload config or restart. Bots it started keep running.",
                    Disable),
                new ChatCommand("target", AccountSecurity.Administrator,
                    "Syntax: .playerbot population target #count\nSet the number of bots to keep online (0.." + PlayerbotOptions.MaxBotsCeiling
                    + ") until the next .reload config or restart (World:Playerbots:Population:TargetOnline). The effective target is also capped by MaxBots.",
                    Target),
            ]),
    ];

    private static bool Status(CommandContext context, string text)
    {
        if (text.Trim().Length != 0) return false;
        if (context.Session.Services.GetService<PlayerbotPopulationFeature>() is not { } feature)
        {
            context.Reply("The playerbot population manager is unavailable.");
            return true;
        }

        foreach (string line in PlayerbotPopulationFeature.Describe(feature.Status)) context.Reply(line);
        return true;
    }

    private static bool Enable(CommandContext context, string text) => SetEnabled(context, text, true);

    private static bool Disable(CommandContext context, string text) => SetEnabled(context, text, false);

    private static bool SetEnabled(CommandContext context, string text, bool enabled)
    {
        if (text.Trim().Length != 0) return false;
        if (context.Session.Services.GetService<PlayerbotPopulationFeature>() is not { } feature)
        {
            context.Reply("The playerbot population manager is unavailable.");
            return true;
        }

        feature.Options.Enabled = enabled;
        context.Reply(enabled ? "Playerbot population enabled. " + Persistence : "Playerbot population disabled; bots it started keep running. " + Persistence);
        return true;
    }

    private static bool Target(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractInt32(out int target) || !args.IsEmpty) return false;
        if (PlayerbotPopulationOptions.CheckTarget(target) is { } problem)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"Population target {target} refused: it {problem}."));
            return true;
        }

        if (context.Session.Services.GetService<PlayerbotPopulationFeature>() is not { } feature)
        {
            context.Reply("The playerbot population manager is unavailable.");
            return true;
        }

        feature.Options.TargetOnline = target;
        context.Reply(string.Create(CultureInfo.InvariantCulture, $"Playerbot population target set to {target}. {Persistence}"));
        return true;
    }
}
