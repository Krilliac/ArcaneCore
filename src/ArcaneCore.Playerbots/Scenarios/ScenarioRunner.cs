using ArcaneCore.Game.Maps;

namespace ArcaneCore.World.Playerbots.Scenarios;

/// <summary>A scripted multi-bot scenario: steps against the real world handlers with assertions on server state.</summary>
public interface IPlayerbotScenario
{
    /// <summary>Registry key (lower-case, no spaces): <c>.playerbot scenario run &lt;name&gt;</c>.</summary>
    string Name { get; }

    string Description { get; }

    Task RunAsync(ScenarioContext context);
}

/// <summary>Runs one scenario: logs its bots in scripted mode, runs the script, records the report, releases the bots.</summary>
public static class ScenarioRunner
{
    public static async Task<ScenarioReport> RunAsync(IPlayerbotScenario scenario, ManagedPlayerbotFeature bots, WorldRuntime world,
        IServiceProvider services, IScenarioClock clock, ScenarioRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(bots);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clock);
        options ??= new ScenarioRunOptions();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.MaxDuration);
        var report = new ScenarioReport(scenario.Name);
        var context = new ScenarioContext(bots, world, services, clock, options, report, deadline.Token);
        try
        {
            await scenario.RunAsync(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            report.Fail(context.CurrentStep, $"the run exceeded {options.MaxDuration.TotalSeconds:F0}s");
        }
        catch (Exception error) when (error is not OutOfMemoryException && error is not OperationCanceledException)
        {
            // A failed step already recorded itself; anything thrown outside a step is recorded here.
            report.Fail(context.CurrentStep, error is ScenarioAssertionException or ScenarioTimeoutException
                ? error.Message : $"{error.GetType().Name}: {error.Message}");
        }
        finally
        {
            context.Finish();
            await context.ReleaseBotsAsync().ConfigureAwait(false);
        }

        return report;
    }
}
