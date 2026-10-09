using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// The global AQ resource counters and phase used by CONDITION_WORLD_SCRIPT. The quest reward store writes contributions
/// in the same transaction as a turn-in; this feature reloads that durable state and keeps the phase's server-side
/// game event in step. An administrator can start the gathering phase with .event start 120.
/// </summary>
public sealed class WarEffortFeature(IServiceScopeFactory scopes, GameEventFeature events, ILogger<WarEffortFeature> logger)
    : IWorldFeature, IGameEventListener
{
    private WarEffortSnapshot _snapshot = WarEffortSnapshot.Disabled;
    private bool _hasStore;
    private GameEventService? _events;
    private uint _reloadMs;

    public WarEffortSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        using (IServiceScope scope = scopes.CreateScope())
        {
            if (scope.ServiceProvider.GetService<IWarEffortStateStore>() is { } store)
            {
                _hasStore = true;
                Volatile.Write(ref _snapshot, store.LoadAsync().GetAwaiter().GetResult());
            }
        }

        events.ServiceCreated += Wire;
        if (events.Service is { } current) Wire(current);
        world.WorldTick += OnTick;
    }

    public bool? WorldScriptCondition(uint field, uint state)
        => !_hasStore ? null : Snapshot.WorldScriptCondition(field, state, DateTimeOffset.UtcNow);

    public void OnEventChanged(ushort eventId, bool active, bool resume)
    {
        if (!_hasStore || eventId != WarEffortCatalog.GatheringEvent || !active || Snapshot.Phase != WarEffortPhase.Disabled)
            return;
        SetPhase(WarEffortPhase.Gathering, 0);
    }

    private void Wire(GameEventService service)
    {
        _events = service;
        service.AddListener(this);
    }

    private void OnTick(uint diffMs)
    {
        if (!_hasStore) return;
        _reloadMs = _reloadMs > diffMs ? _reloadMs - diffMs : 0;
        if (_reloadMs == 0)
        {
            try
            {
                Reload();
                WarEffortSnapshot state = Snapshot;
                if (state.Phase == WarEffortPhase.Transporting && state.PhaseEndsAtUnix > 0
                    && state.PhaseEndsAtUnix <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    SetPhase(WarEffortPhase.Gong, 0);
                SyncPhaseEvent();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "could not refresh AQ war-effort state");
            }

            _reloadMs = 5_000;
        }
    }

    private void Reload()
    {
        using IServiceScope scope = scopes.CreateScope();
        IWarEffortStateStore store = scope.ServiceProvider.GetRequiredService<IWarEffortStateStore>();
        Volatile.Write(ref _snapshot, store.LoadAsync().GetAwaiter().GetResult());
    }

    private void SetPhase(WarEffortPhase phase, long endsAtUnix)
    {
        using IServiceScope scope = scopes.CreateScope();
        IWarEffortStateStore store = scope.ServiceProvider.GetRequiredService<IWarEffortStateStore>();
        store.SetPhaseAsync(phase, endsAtUnix).GetAwaiter().GetResult();
        Reload();
    }

    private void SyncPhaseEvent()
    {
        GameEventService? service = _events;
        if (service?.IsInitialised != true) return;
        ushort desired = Snapshot.Phase switch
        {
            WarEffortPhase.Gathering => 120,
            WarEffortPhase.Transporting => 121,
            WarEffortPhase.Gong => 122,
            WarEffortPhase.TenHourWar => 123,
            WarEffortPhase.Done => 124,
            _ => 0,
        };
        for (ushort id = 120; id <= 124; id++)
        {
            if (id != desired && service.IsActiveEvent(id)) service.StopEvent(id);
        }

        if (desired != 0 && service.IsValidEvent(desired) && !service.IsActiveEvent(desired))
            service.StartEvent(desired);
    }
}
