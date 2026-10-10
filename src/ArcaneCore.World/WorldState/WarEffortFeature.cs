using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Packets;
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
    private readonly HashSet<CreatureMapSystem> _bossSystems = [];
    private readonly HashSet<int> _pendingBossDeaths = [];
    private readonly List<GameObject> _gates = [];
    private WorldRuntime? _world;
    private long _announcedRingAtUnix;

    /// <summary>Seconds after the first ring within which the champion broadcast is still sent (vmangos sends it at the ring).</summary>
    internal const long ChampionAnnounceWindowSeconds = 60;

    /// <summary>Clock seam for tests; Unix seconds.</summary>
    internal Func<long> UtcNowUnix { get; set; } = static () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>The last champion broadcast text sent to the world, for diagnostics and tests.</summary>
    public string? LastChampionAnnouncement { get; private set; }

    public WarEffortSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
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
        world.WorldTick += diffMs =>
        {
            InstallBossAis(world);
            OnTick(diffMs);
            ApplyGates();
        };
        world.MapUnloading += map =>
        {
            if (map.FindUpdater<CreatureMapSystem>() is { } system) _bossSystems.Remove(system);
            _gates.RemoveAll(go => go.Map is null || ReferenceEquals(go.Map, map));
        };
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

    private void InstallBossAis(WorldRuntime world)
    {
        if (!_hasStore) return;
        foreach (Map map in world.Maps.Where(m => m.MapId == 1))
        {
            if (map.FindUpdater<CreatureMapSystem>() is not { } creatures || !_bossSystems.Add(creatures)) continue;
            creatures.RegisterEntryAi(WarEffortCatalog.ColossusOfAshi, creature => new SilithusBossAi(creature, this));
            creatures.RegisterEntryAi(WarEffortCatalog.ColossusOfRegal, creature => new SilithusBossAi(creature, this));
            creatures.RegisterEntryAi(WarEffortCatalog.ColossusOfZora, creature => new SilithusBossAi(creature, this));
        }
    }

    internal void OnBossDied(Creature boss)
    {
        if (!_hasStore || boss.Map?.MapId != 1 || WarEffortCatalog.BossIndex(boss.Entry) is not { } bossIndex)
            return;
        _pendingBossDeaths.Add(bossIndex);
        FlushBossDeaths();
    }

    private void FlushBossDeaths()
    {
        if (_pendingBossDeaths.Count == 0) return;
        try
        {
            using IServiceScope scope = scopes.CreateScope();
            IWarEffortStateStore store = scope.ServiceProvider.GetRequiredService<IWarEffortStateStore>();
            foreach (int bossIndex in _pendingBossDeaths.ToArray())
            {
                store.MarkBossKilledAsync(bossIndex).GetAwaiter().GetResult();
                _pendingBossDeaths.Remove(bossIndex);
            }
            Reload();
            SyncPhaseEvent();
        }
        catch (Exception ex)
        {
            // A storage outage must not interrupt CreatureMapSystem.OnCreatureDied before it sets corpse state.
            // Keep the idempotent flag in memory and retry on the next five-second refresh.
            logger.LogError(ex, "could not persist AQ Colossus death; {Count} flag(s) pending retry", _pendingBossDeaths.Count);
        }
    }

    private void OnTick(uint diffMs)
    {
        if (!_hasStore) return;
        _reloadMs = _reloadMs > diffMs ? _reloadMs - diffMs : 0;
        if (_reloadMs == 0)
        {
            try
            {
                FlushBossDeaths();
                Reload();
                WarEffortSnapshot state = Snapshot;
                long now = UtcNowUnix();
                if (state.Phase == WarEffortPhase.Transporting && state.PhaseEndsAtUnix > 0
                    && state.PhaseEndsAtUnix <= now)
                    SetPhase(WarEffortPhase.Gong, 0);
                else if (state.Phase == WarEffortPhase.TenHourWar && state.PhaseEndsAtUnix > 0
                    && state.PhaseEndsAtUnix <= now)
                    SetPhase(WarEffortPhase.Done, 0);
                SyncPhaseEvent();
                FindGates();
                AnnounceChampion();
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

        for (int bossIndex = 0; bossIndex < 3; bossIndex++)
        {
            ushort eventId = WarEffortCatalog.BossDeathEvent(bossIndex);
            bool shouldRun = Snapshot.Phase == WarEffortPhase.TenHourWar
                && (Snapshot.KilledBossMask & (1 << bossIndex)) != 0;
            if (shouldRun && service.IsValidEvent(eventId) && !service.IsActiveEvent(eventId)) service.StartEvent(eventId);
            else if (!shouldRun && service.IsActiveEvent(eventId)) service.StopEvent(eventId);
        }
    }

    /// <summary>Cache the gate pieces of the loaded Kalimdor maps (rescanned on the five-second refresh).</summary>
    private void FindGates()
    {
        if (_world is null) return;
        _gates.RemoveAll(go => !go.IsSpawned);
        foreach (Map map in _world.Maps.Where(m => m.MapId == 1))
        {
            if (map.FindUpdater<GameObjectMapSystem>() is not { } objects) continue;
            foreach (GameObject go in objects.GameObjects)
            {
                if (go.Entry is WarEffortCatalog.GateBarrier or WarEffortCatalog.GateRoots or WarEffortCatalog.GateRunes
                    && !_gates.Contains(go))
                    _gates.Add(go);
            }
        }
    }

    /// <summary>Hold each gate piece at the state the saved phase and first-ring time imply.</summary>
    private void ApplyGates()
    {
        if (!_hasStore || _gates.Count == 0) return;
        WarEffortGateState gate = Snapshot.GateAt(UtcNowUnix());
        foreach (GameObject go in _gates)
        {
            bool open = go.Entry switch
            {
                WarEffortCatalog.GateRoots => gate.Roots,
                WarEffortCatalog.GateRunes => gate.Runes,
                WarEffortCatalog.GateBarrier => gate.Barrier,
                _ => false,
            };
            GameObjectState desired = open ? GameObjectState.Active : GameObjectState.Ready;
            if (go.IsSpawned && go.State != desired) go.State = desired;
        }
    }

    /// <summary>sWorld.SendBroadcastTextToWorld(GLOBAL_TEXT_CHAMPION) once for the first ring.</summary>
    private void AnnounceChampion()
    {
        WarEffortSnapshot state = Snapshot;
        long rungAt = state.GongFirstRungAtUnix;
        if (rungAt <= 0 || rungAt == _announcedRingAtUnix) return;
        _announcedRingAtUnix = rungAt;
        if (UtcNowUnix() - rungAt > ChampionAnnounceWindowSeconds || _world is null) return;

        string? name = _world.OnlinePlayers.FirstOrDefault(p => (int)p.Guid.Low == state.GongFirstRingerId)?.Name;
        string text;
        using (IServiceScope scope = scopes.CreateScope())
        {
            text = scope.ServiceProvider.GetService<CreatureWorldFeature>()?.Content.Ai.BroadcastTexts
                .Find(WarEffortCatalog.ChampionBroadcastText)?.Text
                ?? "$N has rung the Scarab Gong.";
        }

        text = text.Replace("$N", name ?? "champion", StringComparison.Ordinal)
            .Replace("$n", name ?? "champion", StringComparison.Ordinal);
        LastChampionAnnouncement = text;
        byte[] packet = ChatPackets.BuildSystemMessage(text);
        foreach (Player player in _world.OnlinePlayers.ToArray())
            player.Session.Send(WorldOpcode.SmsgMessagechat, packet);
    }

    private sealed class SilithusBossAi(Creature creature, WarEffortFeature feature) : CreatureAI(creature)
    {
        public override bool AggroesOnSight => true;

        public override void OnRespawn()
        {
            int textId = Me.Entry switch
            {
                WarEffortCatalog.ColossusOfAshi => 11426,
                WarEffortCatalog.ColossusOfRegal => 11424,
                WarEffortCatalog.ColossusOfZora => 11425,
                _ => 0,
            };
            if (textId != 0) System?.SayText(Me, textId);
        }

        public override void OnDeath(Unit? killer) => feature.OnBossDied(Me);
    }
}
