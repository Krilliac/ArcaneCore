using System.Diagnostics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Ops.Announce;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Ops.Lifecycle;

/// <summary>
/// Owns the shutdown/restart countdown (vmangos World::ShutdownServ and friends). Requests and
/// advances run on the world thread; a one second timer posts the elapsed whole seconds to it
/// only while a countdown is pending. When the countdown expires the exit code is stored in
/// <see cref="ExitCodes.Current"/> and the host is asked to stop, which flows into the
/// existing <c>WorldHost.StopAsync</c> order (world stop and final save, features, save queue),
/// so no second shutdown path exists.
/// </summary>
public sealed class ServerLifecycleFeature(IServiceProvider services, ILogger<ServerLifecycleFeature> logger) : IWorldFeature
{
    private readonly ShutdownCountdown _countdown = new();
    private readonly CancellationTokenSource _stopTimer = new();
    private WorldRuntime? _world;
    private TimerRun? _run; // world thread decides start and end; StopAsync only reads it
    private int _runIds;
    private bool _stopIssued;
    private ShutdownMask _announcedMask;
    private bool _announcedPending;

    /// <summary>A countdown is pending or the stop has been issued (readiness probes report draining).</summary>
    public bool IsDraining => _countdown.IsPending || _countdown.StopRequested;

    /// <summary>Raised on the world thread when the process must stop, with the exit code.</summary>
    public event Action<int>? StopRequested;

    /// <summary>
    /// Raised on the world thread when a countdown starts, changes its kind, or ends without stopping (cancelled): the mask of the
    /// pending request and whether one is pending. Not raised for the one-second advances, nor when the countdown expires (that is a stop,
    /// <see cref="StopRequested"/>). Subscribers must not block: record the state and hand any I/O to another thread.
    /// </summary>
    public event Action<ShutdownMask, bool>? CountdownChanged;

    public void Attach(WorldRuntime world) => _world = world;

    public async Task StopAsync()
    {
        await _stopTimer.CancelAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _run) is { } run)
            {
                await run.Task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // the stop timer was cancelled: that is how the run ends
        }

        _stopTimer.Dispose();
    }

    /// <summary>vmangos HandleServerShutDown/Restart/Idle* (ServerCommands.cpp:409-502); world thread. <paramref name="actor"/> is who asked, for the staff announcement only.</summary>
    public void Request(uint seconds, ShutdownMask options, byte exitCode, string? actor = null)
    {
        Publish(_countdown.Request(seconds, options, exitCode, ActiveSessionCount()));
        if (_countdown.IsPending)
        {
            AnnounceRequest(seconds, options, exitCode, actor);
        }

        AfterChange();
    }

    /// <summary>vmangos HandleServerShutDownCancelCommand; world thread. <paramref name="actor"/> is who asked, for the staff announcement only.</summary>
    public void Cancel(string? actor = null)
    {
        ShutdownMask mask = _countdown.Mask;
        ServerMessage? message = _countdown.Cancel();
        Publish(message);
        if (message is not null)
        {
            // The countdown reaches every player as the client's own server message (Publish); the announcer adds the staff line and, if asked for, a chat line.
            Announcer()?.Announce(
                AnnounceCategory.Shutdown,
                AnnounceSeverity.Info,
                mask.HasFlag(ShutdownMask.Restart) ? "The scheduled server restart was cancelled" : "The scheduled server shutdown was cancelled",
                actor is null ? null : $"by {actor}",
                dedupKey: "shutdown-cancel");
        }

        AfterChange();
    }

    private void AnnounceRequest(uint seconds, ShutdownMask options, byte exitCode, string? actor)
    {
        bool restart = options.HasFlag(ShutdownMask.Restart);
        bool idle = options.HasFlag(ShutdownMask.Idle);
        string what = restart ? "restart" : "shutdown";
        string when = idle ? "when no players remain" : $"in {ServerTimeText.Format(seconds)}".TrimEnd('.');
        Announcer()?.Announce(
            AnnounceCategory.Shutdown,
            AnnounceSeverity.Warning,
            $"Server {what} scheduled {when}",
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(actor is null ? string.Empty : $"by {actor}; ")}exit code {exitCode}"),
            dedupKey: "shutdown-request");
    }

    private IOpsAnnouncer? Announcer() => services.GetService<IOpsAnnouncer>();

    /// <summary>Advance the countdown by whole seconds (world thread; the timer and tests call it).</summary>
    public void Advance(uint elapsedSeconds)
    {
        Publish(_countdown.Advance(elapsedSeconds, ActiveSessionCount()));
        AfterChange();
    }

    private int ActiveSessionCount()
        => services.GetService<SessionRegistry>()?.Count ?? _world?.OnlinePlayerCount ?? 0;

    private void Publish(ServerMessage? message)
    {
        if (message is not { } value || _world is not { } world)
        {
            return;
        }

        // vmangos SendGlobalMessage reaches only sessions in the world (World.cpp:2152-2166).
        world.BroadcastToAll(WorldOpcode.SmsgServerMessage, ServerMessagePackets.Build(value));
        logger.LogInformation("Server message {Type}: {Text}", value.Type, value.Text);
    }

    // The timer's lifetime is decided here, on the world thread only: a countdown that is pending
    // always has exactly one timer run, and a run ends only through EndTimer. The timer thread
    // never judges whether it should exit, so a cancel followed at once by a new request cannot
    // leave a pending countdown without a timer.
    private void AfterChange()
    {
        RaiseCountdownChanged();
        if (_countdown.StopRequested && !_stopIssued)
        {
            _stopIssued = true;
            int code = _countdown.ExitCode;
            ExitCodes.Current = code;
            logger.LogWarning("Server stopping on request (exit code {ExitCode})", code);
            try
            {
                Announcer()?.Announce(
                    AnnounceCategory.Shutdown,
                    AnnounceSeverity.Warning,
                    _countdown.Mask.HasFlag(ShutdownMask.Restart) ? "The server is restarting now" : "The server is shutting down now",
                    $"exit code {code}",
                    dedupKey: "shutdown-now");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("lifecycle.announce", ex);
            }

            try
            {
                StopRequested?.Invoke(code);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("lifecycle.stop-requested", ex);
            }

            services.GetService<IHostApplicationLifetime>()?.StopApplication();
        }

        if (_countdown.IsPending)
        {
            if (_run is null && _world is { } world)
            {
                _run = StartTimer(world, ++_runIds);
            }
        }
        else if (_run is { } run)
        {
            _run = null;
            run.Cancel();
        }
    }

    private void RaiseCountdownChanged()
    {
        if (_countdown.StopRequested)
        {
            return; // the countdown expired: the stop follows, and the last announced state stands until the world reports it stopped
        }

        bool pending = _countdown.IsPending;
        ShutdownMask mask = pending ? _countdown.Mask : ShutdownMask.None;
        if (pending == _announcedPending && mask == _announcedMask)
        {
            return;
        }

        _announcedPending = pending;
        _announcedMask = mask;
        try
        {
            CountdownChanged?.Invoke(mask, pending);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogFault("lifecycle.countdown-changed", ex, "A countdown subscriber failed");
        }
    }

    private TimerRun StartTimer(WorldRuntime world, int id)
    {
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stopTimer.Token);
        return new TimerRun(id, cancel, Task.Run(() => RunTimerAsync(world, id, cancel)));
    }

    // A tick from a timer run that has since been ended (or replaced) is ignored.
    private void AdvanceFromTimer(int runId, uint elapsedSeconds)
    {
        if (_run is { } run && run.Id == runId)
        {
            Advance(elapsedSeconds);
        }
    }

    private async Task RunTimerAsync(WorldRuntime world, int runId, CancellationTokenSource cancel)
    {
        long last = Stopwatch.GetTimestamp();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancel.Token).ConfigureAwait(false))
            {
                // Whole elapsed seconds since the last post (a stalled timer thread may owe several).
                long seconds = (Stopwatch.GetTimestamp() - last) / Stopwatch.Frequency;
                if (seconds <= 0)
                {
                    continue;
                }

                last += seconds * Stopwatch.Frequency;
                uint step = seconds > uint.MaxValue ? uint.MaxValue : (uint)seconds;
                world.Post(() => AdvanceFromTimer(runId, step));
            }
        }
        catch (OperationCanceledException)
        {
            // cancelled by Stop or a newer timer: the run ends
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("lifecycle.timer", ex);
        }
        finally
        {
            cancel.Dispose();
        }
    }

    private sealed record TimerRun(int Id, CancellationTokenSource Source, Task Task)
    {
        // The timer disposes its source when it exits; a cancel that races a dispose is harmless.
        public void Cancel()
        {
            try
            {
                Source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // see the type's comment: the timer already disposed its source
            }
        }
    }
}
