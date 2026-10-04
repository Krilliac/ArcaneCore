using ArcaneCore.Game.Honor;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Honor;

/// <summary>
/// Schedules the weekly honor calculation (<see cref="HonorMaintenanceRunner"/>). It first catches up at attach (before the world
/// thread starts, so nobody is online), then, only in <see cref="HonorMaintenanceMode.Live"/> mode (opt-in), checks once a minute whether a week
/// has ended (vmangos World.cpp:2121-2127 checks the same way, but only flags the work and needs a restart, HonorMgr.cpp:617-633;
/// running in-process is a deliberate, opt-in deviation: the default <c>World:Honor:MaintenanceMode = Startup</c> is the vmangos timing).
/// A failed run is logged and retried on the next tick; the transaction is atomic so nothing is applied twice.
/// </summary>
public sealed class HonorMaintenanceFeature(IServiceProvider services, IServiceScopeFactory scopes, ILoggerFactory loggers) : IWorldFeature, IAsyncDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    private readonly ILogger _logger = loggers.CreateLogger<HonorMaintenanceFeature>();
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;
    private HonorFeature? _honor;
    private HonorMaintenanceRunner? _runner;
    private TimeProvider _time = TimeProvider.System;
    private WorldRuntime? _world;

    /// <summary>The runner (after <see cref="Attach"/>, when honor is enabled).</summary>
    public HonorMaintenanceRunner? Runner => _runner;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _honor = services.GetService<HonorFeature>();
        if (_honor is null || !_honor.Options.Enabled)
        {
            return;
        }

        _world = world;
        _time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        _runner = new HonorMaintenanceRunner(_honor, scopes, loggers.CreateLogger<HonorMaintenanceRunner>());
        try
        {
            RunAsync(live: false).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Honor maintenance catch-up failed at startup; it is retried while the world runs");
        }

        if (_honor.Options.MaintenanceMode == HonorMaintenanceMode.Live)
        {
            _loop = Task.Run(() => LoopAsync(_stop.Token));
        }
    }

    /// <summary>Run the weeks that are due now. <paramref name="live"/> says whether the world thread is running.</summary>
    public Task<int> RunAsync(bool live, CancellationToken cancellationToken = default)
    {
        HonorFeature honor = _honor ?? throw new InvalidOperationException("the honor maintenance feature is not attached to an enabled honor feature");
        uint gameDay = HonorMaintenancePlanner.GameDay(_time.GetUtcNow().ToUnixTimeSeconds(), honor.Options.TimeZoneOffsetHours * 3600);
        return _runner!.RunDueAsync(gameDay, live ? _world : null, _time, cancellationToken);
    }

    public async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
    }

    // The token source owns no timer or handle, so it is not disposed: the host stops features and then disposes the container,
    // and a second StopAsync must stay harmless.
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TickInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await RunAsync(live: true, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Honor maintenance failed; nothing was applied, retrying on the next tick");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }
}
