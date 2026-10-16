using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World;

/// <summary>The world daemon's start-up and run-to-end order, extracted from Program so tests can drive it.</summary>
internal static class WorldStartup
{
    /// <summary>
    /// First the single-writer lock (<see cref="WorldWriterGuard"/>), then the schema initializers (<see cref="DatabaseStartup"/>): a second world on the same
    /// characters file is refused before anything opens, upgrades or checkpoints it. A lock that is held elsewhere, and one that cannot be created at all (permissions,
    /// a full disk), are both one line on <paramref name="error"/> and the refusal exit code instead of an unhandled crash.
    /// </summary>
    /// <returns>0 when ready, otherwise the exit code the process should return.</returns>
    public static async Task<int> PrepareDatabasesAsync(IServiceProvider services, Func<Task> initialize, TextWriter error)
    {
        try
        {
            services.GetService<WorldWriterGuard>()?.Acquire();
        }
        catch (Exception ex) when (ex is CharacterDatabaseAlreadyOwnedException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"database refused: {DatabaseStartup.Scrub(ex.Message, services)}").ConfigureAwait(false);
            return DbUpgradeExitCodes.Refused;
        }

        return await DatabaseStartup.InitializeAsync(initialize, services, error).ConfigureAwait(false);
    }

    /// <summary>
    /// Program's body after the host is built: the databases, then what <see cref="HostingAbstractionsHostExtensions.RunAsync(IHost, CancellationToken)"/> does (start,
    /// wait for shutdown, dispose), then the exit code. The container is disposed after the hosted services stop, which releases the <see cref="WorldWriterGuard"/> last;
    /// nothing here touches the provider afterwards, and the exit code of a clean stop (2 for a restart) is read from <paramref name="exitCode"/>.
    /// <para>
    /// A stop requested while the host is still starting (Ctrl-C or <see cref="IHostApplicationLifetime.StopApplication"/> during the bot restore) cancels the start: the host
    /// rethrows that cancellation from <c>StartAsync</c> and, inside <c>RunAsync</c>, would dispose the container without stopping a single hosted service. Out of an async
    /// <c>Main</c> it is then an unhandled exception, which the crash handler turns into a fail-fast (2026-10-16). Here it is the shutdown it is: every hosted service is
    /// stopped in order (the world saved, the WAL checkpointed), then the container is disposed and the clean exit code returned. Any other start failure, and a
    /// cancellation without a stop request (a start timeout), surfaces unchanged.
    /// </para>
    /// </summary>
    public static async Task<int> RunAsync(IHost host, Func<Task> initialize, TextWriter error, Func<int> exitCode)
    {
        int startup = await PrepareDatabasesAsync(host.Services, initialize, error).ConfigureAwait(false);
        if (startup != 0) return startup;

        IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        try
        {
            bool started = false;
            try
            {
                await host.StartAsync().ConfigureAwait(false);
                started = true;
            }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
            {
                host.Services.GetService<ILoggerFactory>()?.CreateLogger(typeof(WorldStartup).FullName!)
                    .LogWarning("Shutdown was requested while the world was starting; stopping in order");
            }

            if (started) await host.WaitForShutdownAsync().ConfigureAwait(false);
            else await host.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            if (host is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else host.Dispose();
        }

        return exitCode();
    }
}
