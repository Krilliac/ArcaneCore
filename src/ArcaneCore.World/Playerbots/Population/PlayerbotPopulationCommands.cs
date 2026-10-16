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
            "Syntax: .playerbot population [status|enable|disable|adopt|target #count|rush #count [#perSecond]|rush stop]\nKeep a number of bots online, spread over the level bands, as long as the world's tick time allows.",
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
                new ChatCommand("adopt", AccountSecurity.Administrator,
                    "Syntax: .playerbot population adopt\nTake over the homes an operator set (.playerbot send ... SentinelHill, .playerbot home) for the bots of the population: over the next sweeps each gets the home of its level again (a level 1 bot its start area, a graduate its capital and level-band town). Homes of bots an operator created, and homes an operator cleared, are left alone. World:Playerbots:Population:AdoptOperatorHomes does the same for good.",
                    Adopt),
                new ChatCommand("rush", AccountSecurity.Administrator,
                    "Syntax: .playerbot population rush $count [$perSecond]\n.playerbot population rush stop\nCreate and start $count new population bots at $perSecond per second (default 5; at most " + MaxRushRate
                    + "), bypassing the add-budget windows. A brake pauses it while the tick p95 stays over World:Playerbots:Population:RushBrakeP95Ms. The existing bot, account and character limits apply. Progress is in .playerbot population status.",
                    Rush),
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
        PopulationStatus status = feature.Status;
        foreach (string line in PlayerbotPopulationFeature.DescribeRush(feature.RushStatus, status.P95Ms)) context.Reply(line);
        return true;
    }

    /// <summary>The fastest rush an operator may ask for (bots per second).</summary>
    internal const int MaxRushRate = 50;

    internal const int DefaultRushRate = 5;

    private static bool Rush(CommandContext context, string text)
    {
        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        if (context.Session.Services.GetService<PlayerbotPopulationFeature>() is not { } feature)
        {
            context.Reply("The playerbot population manager is unavailable.");
            return true;
        }

        if (words.Length == 1 && words[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            if (!feature.StopRush()) { context.Reply("No playerbot population rush is running."); return true; }
            PopulationRushStatus r = feature.RushStatus;
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"Playerbot population rush stopped: {r.Created} of {r.Requested} created; those bots keep running."));
            return true;
        }

        if (words.Length > 2 || !int.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out int count)) return false;
        int perSecond = DefaultRushRate;
        if (words.Length == 2 && !int.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out perSecond)) return false;
        if (count < 1 || count > PlayerbotOptions.MaxBotsCeiling || perSecond < 1 || perSecond > MaxRushRate)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture,
                $"Playerbot population rush refused: the count must be 1..{PlayerbotOptions.MaxBotsCeiling} and the rate 1..{MaxRushRate} per second."));
            return true;
        }

        if (feature.StartRush(count, perSecond) is { } refusal)
        {
            context.Reply("Playerbot population rush refused: " + refusal + ".");
            return true;
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"Playerbot population rush started: {count} new bots at {perSecond} per second{(feature.Options.FreshLaunch ? " (fresh launch: level 1 at their race start)" : string.Empty)}. Watch .playerbot population status; stop it with .playerbot population rush stop. World:Playerbots:MaxBots and MaxRegisteredBots still apply, and the ordinary population parks bots above its TargetOnline."));
        return true;
    }

    private static bool Adopt(CommandContext context, string text)
    {
        if (text.Trim().Length != 0) return false;
        if (context.Session.Services.GetService<PlayerbotPopulationFeature>() is not { } feature)
        {
            context.Reply("The playerbot population manager is unavailable.");
            return true;
        }

        feature.RequestAdopt();
        context.Reply("Playerbot population: the operator homes of its bots are taken over at the next home sweeps (at most "
            + PlayerbotPopulationFeature.MaxHomeChangesPerSweep + " per sweep, one sweep per World:Playerbots:Population:IntervalSeconds) and replaced by the home of each bot's level. Needs FreshLaunch and FreshStartHome.");
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
