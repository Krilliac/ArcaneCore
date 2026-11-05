using System.Runtime.InteropServices;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Schema.Upgrade;
using ArcaneCore.Data.Schema.Upgrade.Cli;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.World.Clustering;
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
    /// (<see cref="DatabaseStartup"/>): a second world on the same characters database is refused before anything opens, upgrades or checkpoints it. With Cluster:Fencing:Scope=Worker the lock is the worker's shard lock, so several workers start on one database and only the same shard twice is refused. A lock that is held elsewhere, and one that cannot be created at all (permissions,
    /// a full disk) and a Worker-scope configuration that cannot run (a bad shard id, SQLite without the test switch), are all one line on <paramref name="error"/> and the refusal exit code instead of an unhandled crash.
    /// </summary>
    /// <returns>0 when ready, otherwise the exit code the process should return.</returns>
    public static async Task<int> PrepareDatabasesAsync(IServiceProvider services, Func<Task> initialize, TextWriter error)
    {
        if (services.GetService<WorldWriterGuard>() is { } guard)
        {
            // Through the same start-up wrapper as the schema step, so a MariaDB/MySQL/PostgreSQL server that is not reachable yet gets the Resilience:Database:Bootstrap backoff and the
            // "database unreachable" exit code. An ownership refusal is NEVER retried or classified as unreachable (a CharacterDatabaseAlreadyOwnedException wraps an IOException, which the
            // transience classifier would call transient): it is captured inside the wrapped step and reported as the refusal, at once, on every provider. So is a lock that cannot be created
            // (permissions, a full disk): its own message, not "owned by another world".
            Exception? refusal = null;
            int lockCode = await DatabaseStartup.InitializeAsync(() =>
            {
                try
                {
                    guard.Acquire();
                }
                catch (Exception ex) when (ex is CharacterDatabaseAlreadyOwnedException or IOException or UnauthorizedAccessException or WorkerScopeConfigurationException)
                {
                    refusal = ex;
                }

                return Task.CompletedTask;
            }, services, error).ConfigureAwait(false);
            if (refusal is not null)
            {
                await error.WriteLineAsync($"database refused: {DatabaseStartup.Scrub(refusal.Message, services)}").ConfigureAwait(false);
                return DbUpgradeExitCodes.Refused;
            }

            if (lockCode != 0) return lockCode;
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
    /// stopped in order (the world saved, the WAL checkpointed), then the container is disposed and the clean exit code returned. A world port that is already in use
    /// (<see cref="Net.WorldPortInUseException"/>, from the early probe or the real bind) is a failed start that ends the same way with <see cref="ExitCodes.Failure"/>, logged and in the fault ledger, not an unhandled
    /// exception. Any other start failure, and a cancellation without a stop request (a start timeout), surfaces unchanged.
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
            await DisposeAsync(host, logger).ConfigureAwait(false);
            return startup;
        }

        if (Volatile.Read(ref stopRequest) is { } stopped)
        {
            int code = exitCode();
            logger.LogWarning("World start-up stopped on request ({Request}) before the world started; exit code {ExitCode}", stopped, code);
            return Finish(code, await DisposeAsync(host, logger).ConfigureAwait(false));
        }

        bool failed = false;
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
            catch (Exception ex) when (IsPortInUse(ex))
            {
                // The early probe in WorldHost or the real bind in WorldServer: a failed start, ended with a failure code instead of an unhandled exception (the crash handler's fail-fast and dump).
                // The services that did start are stopped in order below, as for a stop request during start.
                failed = true;
                RecordFailure(logger, "world.start-port-in-use", "The world could not start: its port is in use", ex);
            }

            // The stop runs here, not in the disposal: WaitForShutdownAsync stops the host once the shutdown was requested, and a failed start that was a stop request stops it directly.
            // A hosted service that throws from its stop (WorldHost: "World feature shutdown failed") surfaces from this call; it is a failure of the run, not an unhandled crash.
            try
            {
                if (started) await host.WaitForShutdownAsync().ConfigureAwait(false);
                else await host.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed = true;
                RecordFailure(logger, "world.host-stop", "Stopping the host failed", ex);
            }
        }
        finally
        {
            // Disposal is still attempted after a failed stop (the container releases the writer lock and drains the log queue last).
            failed |= await DisposeAsync(host, logger).ConfigureAwait(false);
        }

        return Finish(exitCode(), failed);
    }

    /// <summary>True for the port-in-use refusal, also when the host wrapped it in an <see cref="AggregateException"/> (concurrent start).</summary>
    private static bool IsPortInUse(Exception ex)
        => ex is Net.WorldPortInUseException || (ex is AggregateException aggregate && aggregate.InnerExceptions.Any(static inner => inner is Net.WorldPortInUseException));

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

    /// <summary>
    /// A failure of the host's stop or of its disposal (the container disposing a service whose <c>Dispose</c> throws): logged critical, recorded once in the <see cref="FaultLedger"/> under <paramref name="site"/>, and
    /// a still-zero <see cref="ExitCodes.Current"/> becomes <see cref="ExitCodes.Failure"/>, so the supervisor sees a failed run. Nothing is rethrown: an exception that escaped <c>Main</c> here would be turned by the crash
    /// handler into a fail-fast with a full dump. That removes the dump, not the relaunch delay: a supervisor relaunches after exit code 1 with the same backoff it applies after a fail-fast. Combined mode with no supervisor just
    /// ends with the code. Runtime errors elsewhere keep the crash handler's fail-fast.
    /// </summary>
    private static void RecordFailure(ILogger logger, string site, string what, Exception ex)
    {
        logger.LogCritical(ex, "{What} ({Type}); exiting with a failure code instead of crashing", what, ex.GetType().Name);
        FaultLedger.Shared.Record(site, ex);
        if (ExitCodes.Current == ExitCodes.Success) ExitCodes.Current = ExitCodes.Failure;
    }

    /// <summary>
    /// Disposes the host, which disposes the container and with it every service (the writer lock last). A service whose disposal throws escapes from here;
    /// anything but OutOfMemoryException is handled by <see cref="RecordFailure"/> (site <c>world.host-dispose</c>) and not rethrown.
    /// </summary>
    /// <returns>True when the disposal threw.</returns>
    private static async Task<bool> DisposeAsync(IHost host, ILogger logger)
    {
        try
        {
            if (host is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else host.Dispose();
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RecordFailure(logger, "world.host-dispose", "Disposing the host failed", ex);
            return true;
        }
    }

    /// <summary>
    /// The code to return: the supervisor-visible one, but never 0 after a failed stop or disposal. A code that is already set stays as it is, including 2 (a restart request): a restart that was asked for is
    /// still a restart when its stop fails (the failure is logged and in the fault ledger either way).
    /// </summary>
    private static int Finish(int code, bool failed) => failed && code == ExitCodes.Success ? ExitCodes.Failure : code;

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
