using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.Server;

/// <summary>
/// Runs the <see cref="ShutdownScheduler"/> for the daemon: a one second wall-clock timer posts
/// its ticks to the world thread (vmangos advances m_ShutdownTimer by the seconds elapsed since
/// the last world update), countdown messages go to every player as SMSG_SERVER_MESSAGE, and when
/// the scheduler stops the daemon the process exit code is set (0 shutdown, 2 restart by default)
/// and the host is asked to stop, which runs the normal save and drain.
/// <para>
/// A supervisor restarts the daemon on exit code 2 (vmangos documents the same convention for
/// its restart scripts); nothing in the process does that itself.
/// </para>
/// </summary>
public sealed class ShutdownFeature(IServiceProvider services, ILogger<ShutdownFeature> logger) : IWorldFeature
{
    private ShutdownScheduler? _scheduler;
    private ITimer? _timer;
    private WorldRuntime? _world;
    private long _lastSecond;
    private int _stopFired;

    /// <summary>The state machine (available after <see cref="Attach"/>).</summary>
    public ShutdownScheduler Scheduler => _scheduler ?? throw new InvalidOperationException("the shutdown feature is not attached");

    /// <summary>
    /// What happens when the scheduler stops the server, with the exit code. The default sets
    /// <see cref="Environment.ExitCode"/> and stops the host (when the daemon runs under one).
    /// </summary>
    public Action<byte> StopAction { get; set; } = code => { };

    public void Attach(WorldRuntime world)
    {
        _world = world;
        _scheduler = new ShutdownScheduler(
            () => services.GetService<SessionRegistry>()?.Count ?? world.OnlinePlayerCount,
            (type, text) => world.BroadcastToAll(WorldOpcode.SmsgServerMessage, ServerMessagePackets.Build(type, text)));

        if (services.GetService<IHostApplicationLifetime>() is { } lifetime)
        {
            StopAction = code =>
            {
                Environment.ExitCode = code;
                lifetime.StopApplication();
            };
        }

        TimeProvider time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _lastSecond = time.GetUtcNow().ToUnixTimeSeconds();
        _timer = time.CreateTimer(_ => OnTimer(time), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public Task StopAsync()
    {
        _timer?.Dispose();
        _timer = null;
        return Task.CompletedTask;
    }

    /// <summary>vmangos World::ShutdownServ (world thread).</summary>
    public void Request(uint delaySeconds, ShutdownMask mask, byte exitCode)
    {
        Scheduler.Request(delaySeconds, mask, exitCode);
        ApplyStop();
    }

    /// <summary>vmangos World::ShutdownCancel (world thread).</summary>
    public void Cancel() => Scheduler.Cancel();

    private void OnTimer(TimeProvider time)
    {
        long now = time.GetUtcNow().ToUnixTimeSeconds();
        long elapsed = now - Interlocked.Exchange(ref _lastSecond, now);
        if (elapsed <= 0)
        {
            return;
        }

        uint seconds = (uint)Math.Min(elapsed, uint.MaxValue);
        _world?.Post(() =>
        {
            Scheduler.Tick(seconds);
            ApplyStop();
        });
    }

    private void ApplyStop()
    {
        if (Scheduler.StopRequested && Interlocked.Exchange(ref _stopFired, 1) == 0)
        {
            logger.LogInformation("Server {Kind} with exit code {Code}",
                Scheduler.Mask.HasFlag(ShutdownMask.Restart) ? "restarting" : "shutting down", Scheduler.ExitCode);
            StopAction(Scheduler.ExitCode);
        }
    }
}

/// <summary>
/// Player counts for <c>.server info</c>: the active count comes from the session registry (the
/// players in the world when none is registered) and the maximum is sampled whenever a player
/// enters the world. ArcaneCore has no login queue, so the queued counts are always 0.
/// </summary>
public sealed class ServerStats(IServiceProvider services) : IWorldFeature
{
    private int _maxActive;

    public int MaxActive => Volatile.Read(ref _maxActive);

    public void Attach(WorldRuntime world) => world.PlayerLoggedIn += _ => Sample(world);

    public int Active(WorldRuntime world) => Math.Max(services.GetService<SessionRegistry>()?.Count ?? 0, world.OnlinePlayerCount);

    private void Sample(WorldRuntime world)
    {
        int active = Active(world);
        int seen;
        do
        {
            seen = Volatile.Read(ref _maxActive);
        }
        while (active > seen && Interlocked.CompareExchange(ref _maxActive, active, seen) != seen);
    }
}
