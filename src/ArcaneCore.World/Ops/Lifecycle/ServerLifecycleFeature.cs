using System.Diagnostics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
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

    /// <summary>A countdown is pending or the stop has been issued (readiness probes report draining).</summary>
    public bool IsDraining => _countdown.IsPending || _countdown.StopRequested;

    /// <summary>Raised on the world thread when the process must stop, with the exit code.</summary>
    public event Action<int>? StopRequested;

    public void Attach(WorldRuntime world) => _world = world;

    public async Task StopAsync()
    {
        await _stopTimer.CancelAsync().ConfigureAwait(false);
        try
        {
            TimerRun? run = Volatile.Read(ref _run);
        if (run is not null)
        {
            await run.Task.ConfigureAwait(false);
        }
        }
        catch (OperationCanceledException)
        {
        }

        _stopTimer.Dispose();
    }

    /// <summary>vmangos HandleServerShutDown/Restart/Idle* (ServerCommands.cpp:409-502); world thread.</summary>
    public void Request(uint seconds, ShutdownMask options, byte exitCode)
    {
        Publish(_countdown.Request(seconds, options, exitCode, ActiveSessionCount()));
        AfterChange();
    }

    /// <summary>vmangos HandleServerShutDownCancelCommand; world thread.</summary>
    public void Cancel()
    {
        Publish(_countdown.Cancel());
        AfterChange();
    }

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
        if (message is not { } value || _world is null)
        {
            return;
        }

        // vmangos SendGlobalMessage reaches only sessions in the world (World.cpp:2152-2166).
        _world.BroadcastToAll(WorldOpcode.SmsgServerMessage, ServerMessagePackets.Build(value));
        logger.LogInformation("Server message {Type}: {Text}", value.Type, value.Text);
    }

    // The timer's lifetime is decided here, on the world thread only: a countdown that is pending
    // always has exactly one timer run, and a run ends only through EndTimer. The timer thread
    // never judges whether it should exit, so a cancel followed at once by a new request cannot
    // leave a pending countdown without a timer.
    private void AfterChange()
    {
        if (_countdown.StopRequested && !_stopIssued)
        {
            _stopIssued = true;
            int code = _countdown.ExitCode;
            ExitCodes.Current = code;
            logger.LogWarning("Server stopping on request (exit code {ExitCode})", code);
            StopRequested?.Invoke(code);
            services.GetService<IHostApplicationLifetime>()?.StopApplication();
        }

        if (_countdown.IsPending)
        {
            if (_run is null && _world is not null)
            {
                _run = StartTimer(_world, ++_runIds);
            }
        }
        else if (_run is { } run)
        {
            _run = null;
            run.Cancel();
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
                if (seconds == 0)
                {
                    continue;
                }

                last += seconds * Stopwatch.Frequency;
                world.Post(() => AdvanceFromTimer(runId, (uint)seconds));
            }
        }
        catch (OperationCanceledException)
        {
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
            }
        }
    }
}
