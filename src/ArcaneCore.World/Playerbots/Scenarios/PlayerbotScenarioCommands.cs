using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>
/// <c>.playerbot scenario list|run &lt;name&gt;</c> (Administrator): run a registered scenario against the live world with the
/// real clock and print its report. Refused unless <c>World:Playerbots:Scenarios:Enabled</c> (and playerbots) are on;
/// one run at a time.
/// </summary>
internal static class PlayerbotScenarioCommands
{
    private static int s_running;

    internal static ChatCommand Command { get; } = new("scenario", AccountSecurity.Administrator,
        "Syntax: .playerbot scenario <list|run $name>\nRun scripted bot scenarios against the live world (World:Playerbots:Scenarios:Enabled).",
        Children:
        [
            new ChatCommand("list", AccountSecurity.Administrator,
                "Syntax: .playerbot scenario list\nList the registered bot scenarios.", List),
            new ChatCommand("run", AccountSecurity.Administrator,
                "Syntax: .playerbot scenario run $name\nRun one scenario with scripted bots and print its report.", Run),
        ]);

    private static bool List(CommandContext context, string text)
    {
        if (text.Trim().Length != 0) return false;
        if (!Enabled(context, out _)) return true;
        foreach (IPlayerbotScenario scenario in PlayerbotScenarioCatalog.All(context.Session.Services))
            context.Reply($"{scenario.Name}: {scenario.Description}");
        return true;
    }

    private static bool Run(CommandContext context, string text)
    {
        CommandArgs args = new(text);
        string? name = args.ExtractArg();
        if (name is null || !args.IsEmpty) return false;
        if (!Enabled(context, out PlayerbotOptions? options)) return true;
        IServiceProvider services = context.Session.Services;
        if (PlayerbotScenarioCatalog.Find(services, name) is not { } scenario)
        {
            context.Reply($"Unknown scenario '{name}'. Use .playerbot scenario list.");
            return true;
        }

        if (services.GetService<ManagedPlayerbotFeature>() is not { } bots)
        {
            context.Reply("Playerbot service is unavailable.");
            return true;
        }

        if (Interlocked.CompareExchange(ref s_running, 1, 0) != 0)
        {
            context.Reply("A playerbot scenario is already running.");
            return true;
        }

        context.Reply($"Playerbot scenario {scenario.Name} started.");
        var runOptions = new ScenarioRunOptions
        {
            MaxDuration = TimeSpan.FromSeconds(options!.Scenarios.MaxDurationSeconds),
            StepTimeout = TimeSpan.FromSeconds(options.Scenarios.StepTimeoutSeconds),
        };
        _ = Task.Run(async () =>
        {
            try
            {
                ScenarioReport report = await ScenarioRunner.RunAsync(scenario, bots, context.Session.World, services,
                    ScenarioClock.Real, runOptions).ConfigureAwait(false);
                foreach (string line in report.ToLines()) context.Reply(line);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                context.Reply($"Playerbot scenario {scenario.Name} failed: {error.GetType().Name}.");
            }
            finally
            {
                Volatile.Write(ref s_running, 0);
            }
        });
        return true;
    }

    private static bool Enabled(CommandContext context, out PlayerbotOptions? options)
    {
        options = context.Session.Services.GetService<IOptions<PlayerbotOptions>>()?.Value;
        if (options is { Enabled: true, Scenarios.Enabled: true }) return true;
        context.Reply("Playerbot scenarios are disabled (World:Playerbots:Scenarios:Enabled).");
        return false;
    }
}
