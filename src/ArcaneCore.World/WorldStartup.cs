using System.Runtime.InteropServices;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World;

/// <summary>The world daemon's start-up and run-to-end order, extracted from Program so tests can drive it.</summary>
internal static class WorldStartup
{
    /// <summary>
    /// First the single-writer lock (<see cref="WorldWriterGuard"/>: the SQLite sidecar file, or the MariaDB/MySQL/PostgreSQL session advisory lock), then the schema initializers
    /// (<see cref="DatabaseStartup"/>): a second world on the same characters database is refused before anything opens, upgrades or checkpoints it. A lock that is held elsewhere, and one that cannot be created at all (permissions,
    /// a full disk), are both one line on <paramref name="error"/> and the refusal exit code instead of an unhandled crash.
    /// </summary>
    /// <returns>0 when ready, otherwise the exit code the process should return.</returns>
    public static async Task<int> PrepareDatabasesAsync(IServiceProvider services, Func<Task> initialize, TextWriter error)
    {
        try
        {
            if (services.GetService<WorldWriterGuard>() is { } guard)
            {
                // Through the same start-up wrapper as the schema step: a MariaDB/MySQL/PostgreSQL server that is not reachable yet gets the Resilience:Database:Bootstrap
                // backoff and the "database unreachable" exit code, as it does for the initializers. A held lock is not transient and surfaces as the refusal below.
                int lockCode = await DatabaseStartup.InitializeAsync(() =>
                {
                    guard.Acquire();
                    return Task.CompletedTask;
                }, services, error).ConfigureAwait(false);
                if (lockCode != 0) return lockCode;
            }
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
    /// <para>
    /// The host's console lifetime listens for Ctrl-C, Ctrl-Break and SIGTERM only once the host starts, so <paramref name="listenForStop"/> covers the database
    /// step before it. Without it a Ctrl-C there ended the process at once with STATUS_CONTROL_C_EXIT (0xC000013A) and no line anywhere (2026-10-16 22:44: the
    /// log stopped at the writer-lock line; no exception, no crash report, no event log entry, nothing on the console). Now the request is logged, the database
    /// step runs to its end (an upgrade is never cut in half), the world does not start, and the container is disposed (lock released, log queue drained) before
    /// the clean exit code is returned. A refusal of the database step is logged as well as written to <paramref name="error"/>.
    /// </para>
    /// </summary>
    /// <param name="listenForStop">Starts listening for stop requests and calls back with the request's name; disposing stops listening. Default: <see cref="ListenForStopSignals"/>.</param>
    public static async Task<int> RunAsync(IHost host, Func<Task> initialize, TextWriter error, Func<int> exitCode, Func<Action<string>, IDisposable>? listenForStop = null)
    {
        ILogger logger = host.Services.GetService<ILoggerFactory>()?.CreateLogger(typeof(WorldStartup).FullName!) ?? NullLogger.Instance;
        IHostApplicationLifetime lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        string? stopRequest = null;
        var refusal = new StringWriter();
        int startup;
        using (IDisposable listening = (listenForStop ?? ListenForStopSignals)(request =>
               {
                   if (Interlocked.CompareExchange(ref stopRequest, request, null) is not null) return;
                   logger.LogWarning("Stop requested ({Request}) during start-up, in the database step: the step runs to its end and the world does not start", request);
                   lifetime.StopApplication();
               }))
        {
            startup = await PrepareDatabasesAsync(host.Services, initialize, refusal).ConfigureAwait(false);
        }

        if (refusal.ToString() is { Length: > 0 } refused)
        {
            await error.WriteAsync(refused).ConfigureAwait(false);
            logger.LogError("World start-up refused by the database step (exit code {ExitCode}): {Reason}", startup, refused.Trim());
        }

        if (startup != 0)
        {
            await DisposeAsync(host).ConfigureAwait(false);
            return startup;
        }

        if (Volatile.Read(ref stopRequest) is { } stopped)
        {
            int code = exitCode();
            logger.LogWarning("World start-up stopped on request ({Request}) before the world started; exit code {ExitCode}", stopped, code);
            await DisposeAsync(host).ConfigureAwait(false);
            return code;
        }

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
            await DisposeAsync(host).ConfigureAwait(false);
        }

        return exitCode();
    }

    /// <summary>
    /// Listens for the stop requests the host's console lifetime handles once it starts (SIGINT = Ctrl-C, SIGQUIT = Ctrl-Break, SIGTERM; on Windows the
    /// console control events) and turns each into a call of <paramref name="onStop"/> instead of the runtime's immediate termination.
    /// </summary>
    public static IDisposable ListenForStopSignals(Action<string> onStop)
    {
        ArgumentNullException.ThrowIfNull(onStop);
        void Handle(PosixSignalContext context)
        {
            context.Cancel = true;
            onStop(context.Signal.ToString());
        }

        return new SignalRegistrations(
        [
            PosixSignalRegistration.Create(PosixSignal.SIGINT, Handle),
            PosixSignalRegistration.Create(PosixSignal.SIGQUIT, Handle),
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, Handle),
        ]);
    }

    private static async Task DisposeAsync(IHost host)
    {
        if (host is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else host.Dispose();
    }

    private sealed class SignalRegistrations(PosixSignalRegistration[] registrations) : IDisposable
    {
        public void Dispose()
        {
            foreach (PosixSignalRegistration registration in registrations)
            {
                registration.Dispose();
            }
        }
    }
}
