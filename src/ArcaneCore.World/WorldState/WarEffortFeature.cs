using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Packets;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.States;
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
public sealed partial class WarEffortFeature(IServiceScopeFactory scopes, GameEventFeature events, ILogger<WarEffortFeature> logger)
    : IWorldFeature, IGameEventListener, IWorldStateProvider
{
    private WarEffortSnapshot _snapshot = WarEffortSnapshot.Disabled;
    private bool _hasStore;
    private GameEventService? _events;
    private uint _reloadMs;
    private OffThreadStoreWork? _store;
    private Func<Func<Task>, Task>? _offload;
    private WarEffortPhase? _queuedPhase;
    private bool _refreshDue;
    private bool _wake;
    private readonly HashSet<CreatureMapSystem> _bossSystems = [];
    private readonly HashSet<int> _pendingBossDeaths = [];
    private readonly List<GameObject> _gates = [];
    private WorldRuntime? _world;
    private long _announcedRingAtUnix;
    private readonly Dictionary<(Map Map, uint Guid), GameObject> _piles = [];
    private Dictionary<uint, uint> _sentCapitalStates = [];

    /// <summary>Seconds after the first ring within which the champion broadcast is still sent (vmangos sends it at the ring).</summary>
    internal const long ChampionAnnounceWindowSeconds = 60;

    /// <summary>Clock seam for tests; Unix seconds.</summary>
    internal Func<long> UtcNowUnix { get; set; } = static () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>The last champion broadcast text sent to the world, for diagnostics and tests.</summary>
    public string? LastChampionAnnouncement { get; private set; }

    public WarEffortSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Test seam for where realm-store work runs (<see cref="OffThreadStoreWork.Offload"/>). Production leaves it null: the
    /// thread pool, never the world thread. Tests whose fake stores complete synchronously pass <c>work =&gt; work()</c>.
    /// </summary>
    internal Func<Func<Task>, Task>? StoreOffload
    {
        get => _offload;
        set
        {
            _offload = value;
            if (_store is not null && value is not null) _store.Offload = value;
        }
    }

    /// <summary>The single-flight store runner (null before <see cref="Attach"/>).</summary>
    internal OffThreadStoreWork? StoreWork => _store;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        _store = new OffThreadStoreWork(world);
        if (_offload is not null) _store.Offload = _offload;
        using (IServiceScope scope = scopes.CreateScope())
        {
            if (scope.ServiceProvider.GetService<IWarEffortStateStore>() is { } store)
            {
                _hasStore = true;
                Volatile.Write(ref _snapshot, store.LoadAsync().GetAwaiter().GetResult());
            }
        }

        if (_hasStore) WorldStateHooks.For(world).WorldStates.Add(this);
        _sentCapitalStates = CapitalStateMap();
        events.ServiceCreated += Wire;
        if (events.Service is { } current) Wire(current);
        world.WorldTick += diffMs =>
        {
            try
            {
                InstallBossAis(world);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("world-event.war-effort", ex);
                AnomalyLedger.Shared.Report("world-event", "lifecycle-fault", "war-effort", $"install-boss-ais error={ex.GetType().Name}: {ex.Message}");
                logger.LogFault("war-effort.install-boss-ais", ex, "failed to install AQ boss AIs on world tick");
            }

            try
            {
                OnTick(diffMs);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("world-event.war-effort", ex);
                AnomalyLedger.Shared.Report("world-event", "lifecycle-fault", "war-effort", $"on-tick error={ex.GetType().Name}: {ex.Message}");
                logger.LogFault("war-effort.on-tick", ex, "failed to tick AQ war effort on world tick");
            }

            try
            {
                ApplyGates();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("world-event.war-effort", ex);
                AnomalyLedger.Shared.Report("world-event", "lifecycle-fault", "war-effort", $"apply-gates error={ex.GetType().Name}: {ex.Message}");
                logger.LogFault("war-effort.apply-gates", ex, "failed to apply AQ gate states on world tick");
            }

            if (_hasStore)
            {
                try
                {
                    RunScenes();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("world-event.war-effort", ex);
                    AnomalyLedger.Shared.Report("world-event", "lifecycle-fault", "war-effort", $"run-scenes error={ex.GetType().Name}: {ex.Message}");
                    logger.LogFault("war-effort.run-scenes", ex, "failed to run AQ war effort scenes on world tick");
                }
            }
        };
        world.MapUnloading += map =>
        {
            if (map.FindUpdater<CreatureMapSystem>() is { } system) _bossSystems.Remove(system);
            _gates.RemoveAll(go => go.Map is null || ReferenceEquals(go.Map, map));
            foreach ((Map Map, uint Guid) key in _piles.Keys.Where(k => ReferenceEquals(k.Map, map)).ToArray()) _piles.Remove(key);
        };
    }

    public bool? WorldScriptCondition(uint field, uint state)
        => !_hasStore ? null : Snapshot.WorldScriptCondition(field, state, DateTimeOffset.UtcNow);

    public void OnEventChanged(ushort eventId, bool active, bool resume)
    {
        if (!_hasStore || eventId != WarEffortCatalog.GatheringEvent || !active || Snapshot.Phase != WarEffortPhase.Disabled
            || _queuedPhase is not null) return;
        _queuedPhase = WarEffortPhase.Gathering;
        _wake = true;
        TryLaunch();
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
        _wake = true;
        TryLaunch();
    }

    /// <summary>After the world stopped: wait for the store job in flight, then write a queued phase start and boss deaths.</summary>
    public async Task StopAsync()
    {
        if (!_hasStore || _store is null) return;
        try
        {
            await _store.InFlight.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected cancellation during shutdown.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The job's failure would have been logged by its apply on the world thread, which no longer runs: log it here.
            // The queued work below is still written.
            FaultLedger.Shared.Record("world-event.war-effort", ex);
            AnomalyLedger.Shared.Report("world-event", "lifecycle-fault", "war-effort", $"stop-inflight error={ex.GetType().Name}: {ex.Message}");
            logger.LogFault(LogLevel.Warning, "war-effort.stop-inflight", ex, "the AQ war-effort store job in flight at shutdown failed");
        }

        WarEffortPhase? phase = _queuedPhase == _inFlightPhase ? null : _queuedPhase;
        int[] deaths = [.. _pendingBossDeaths];
        if (phase is null && deaths.Length == 0) return;
        var result = new RefreshResult();
        try
        {
            await RunStoreAsync(phase, deaths, full: false, UtcNowUnix(), result).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected cancellation during shutdown.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("world-event.war-effort", ex);
            AnomalyLedger.Shared.Report("world-event", "lifecycle-fault", "war-effort", $"stop-flush error={ex.GetType().Name}: {ex.Message}");
            logger.LogFault("war-effort.stop-flush", ex, "could not write the queued AQ war-effort state at shutdown");
        }

        if (result.BossDeathFailure is { } failure)
            logger.LogFault("war-effort.stop-boss-death", failure, "could not persist AQ Colossus death at shutdown");
    }

    private WarEffortPhase? _inFlightPhase;

    private void OnTick(uint diffMs)
    {
        if (!_hasStore) return;
        _reloadMs = _reloadMs > diffMs ? _reloadMs - diffMs : 0;
        if (_reloadMs == 0)
        {
            _refreshDue = true;
            _wake = true;
            _reloadMs = 5_000;
        }
        TryLaunch();
    }

    /// <summary>What one store job did, filled off the world thread and read by <see cref="ApplyRefresh"/> on it.</summary>
    private sealed class RefreshResult
    {
        public readonly List<int> FlushedBossDeaths = [];
        public Exception? BossDeathFailure;
        public WarEffortSnapshot? Snapshot;
    }

    /// <summary>
    /// Starts the next store job when one is wanted and none is in flight. Quest turn-ins write the counters from their own
    /// transactions, so the five-second refresh still reloads the durable state, but every store call now runs off the world
    /// thread (<see cref="RunStoreAsync"/>) and only the loaded snapshot is applied on it.
    /// </summary>
    private void TryLaunch()
    {
        if (!_wake || _store is null || _store.Busy) return;
        _wake = false;
        int[] deaths = [.. _pendingBossDeaths];
        WarEffortPhase? phase = _queuedPhase;
        bool full = _refreshDue;
        _refreshDue = false;
        if (!full && deaths.Length == 0 && phase is null) return;
        long now = UtcNowUnix();
        var result = new RefreshResult();
        _inFlightPhase = phase;
        _store.TryRun(() => RunStoreAsync(phase, deaths, full, now, result),
            () =>
            {
                if (_queuedPhase == phase) _queuedPhase = null;
                ApplyRefresh(result, full, syncEvent: full || deaths.Length > 0);
            },
            ex =>
            {
                // Keep _queuedPhase: the phase write is idempotent and the next five-second refresh relaunches it (like the pending
                // boss deaths). Clearing it lost the Gathering start, and that refresh then saw Disabled and stopped event 120.
                foreach (int done in result.FlushedBossDeaths) _pendingBossDeaths.Remove(done);
                FaultLedger.Shared.Record("world-event.war-effort", ex);
                AnomalyLedger.Shared.Report("world-event", "lifecycle-fault", "war-effort", $"refresh-store error={ex.GetType().Name}: {ex.Message}");
                logger.LogFault("war-effort.refresh-store", ex, "could not refresh AQ war-effort state");
            });
    }

    private async Task RunStoreAsync(WarEffortPhase? phase, int[] deaths, bool full, long now, RefreshResult result)
    {
        using IServiceScope scope = scopes.CreateScope();
        IWarEffortStateStore store = scope.ServiceProvider.GetRequiredService<IWarEffortStateStore>();
        if (phase is { } start) await store.SetPhaseAsync(start, 0).ConfigureAwait(false);
        try
        {
            foreach (int bossIndex in deaths)
            {
                await store.MarkBossKilledAsync(bossIndex).ConfigureAwait(false);
                result.FlushedBossDeaths.Add(bossIndex);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A storage outage must not interrupt CreatureMapSystem.OnCreatureDied before it sets corpse state.
            // Keep the idempotent flag in memory and retry on the next five-second refresh.
            FaultLedger.Shared.Record("world-event.war-effort", ex);
            FaultLedger.Shared.Record("war-effort.mark-boss-killed", ex);
            AnomalyLedger.Shared.Report("world-event", "lifecycle-fault", "war-effort", $"mark-boss-killed error={ex.GetType().Name}: {ex.Message}");
            result.BossDeathFailure = ex;
        }
        WarEffortSnapshot state = await store.LoadAsync().ConfigureAwait(false);
        result.Snapshot = state;
        if (!full) return;
        WarEffortPhase? next = state.Phase == WarEffortPhase.Transporting && state.PhaseEndsAtUnix > 0 && state.PhaseEndsAtUnix <= now
            ? WarEffortPhase.Gong
            : state.Phase == WarEffortPhase.TenHourWar && state.PhaseEndsAtUnix > 0 && state.PhaseEndsAtUnix <= now
                ? WarEffortPhase.Done
                : null;
        if (next is { } advance)
        {
            await store.SetPhaseAsync(advance, 0).ConfigureAwait(false);
            result.Snapshot = await store.LoadAsync().ConfigureAwait(false);
        }
    }

    private void ApplyRefresh(RefreshResult result, bool full, bool syncEvent)
    {
        foreach (int done in result.FlushedBossDeaths) _pendingBossDeaths.Remove(done);
        if (result.BossDeathFailure is { } failure)
            logger.LogFault("war-effort.persist-boss-death", failure, "could not persist AQ Colossus death; {Count} flag(s) pending retry", _pendingBossDeaths.Count);
        if (result.Snapshot is { } current) Volatile.Write(ref _snapshot, current);
        if (_queuedPhase is not null)
        {
            // .event start 120 was queued after this job began: its stale phase would stop the event just started.
            if (full) _refreshDue = true;
            _wake = true;
            TryLaunch();
            return;
        }

        if (syncEvent)
        {
            try
            {
                SyncPhaseEvent();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("world-event.war-effort", ex);
                AnomalyLedger.Shared.Report("world-event", "lifecycle-fault", "war-effort", $"sync-phase-event error={ex.GetType().Name}: {ex.Message}");
                logger.LogFault("war-effort.sync-phase-event", ex, "could not sync AQ war-effort phase event");
            }
        }

        if (full)
        {
            try
            {
                FindGates();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("world-event.war-effort", ex);
                AnomalyLedger.Shared.Report("world-event", "lifecycle-fault", "war-effort", $"find-gates error={ex.GetType().Name}: {ex.Message}");
                logger.LogFault("war-effort.find-gates", ex, "could not scan AQ war-effort gates");
            }

            try
            {
                AnnounceChampion();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("world-event.war-effort", ex);
                AnomalyLedger.Shared.Report("world-event", "lifecycle-fault", "war-effort", $"announce-champion error={ex.GetType().Name}: {ex.Message}");
                logger.LogFault("war-effort.announce-champion", ex, "could not announce AQ war-effort champion");
            }

            try
            {
                SyncPiles();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("world-event.war-effort", ex);
                AnomalyLedger.Shared.Report("world-event", "lifecycle-fault", "war-effort", $"sync-piles error={ex.GetType().Name}: {ex.Message}");
                logger.LogFault("war-effort.sync-piles", ex, "could not sync AQ war-effort resource piles");
            }

            try
            {
                PublishCapitalStates();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("world-event.war-effort", ex);
                AnomalyLedger.Shared.Report("world-event", "lifecycle-fault", "war-effort", $"publish-capital-states error={ex.GetType().Name}: {ex.Message}");
                logger.LogFault("war-effort.publish-capital-states", ex, "could not publish AQ war-effort capital world states");
            }
        }

        TryLaunch();
    }

    /// <summary>Synchronous reload for tests that change the fake store between ticks; the world tick never calls it.</summary>
    internal void Reload()
    {
        using IServiceScope scope = scopes.CreateScope();
        IWarEffortStateStore store = scope.ServiceProvider.GetRequiredService<IWarEffortStateStore>();
        Volatile.Write(ref _snapshot, store.LoadAsync().GetAwaiter().GetResult());
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

    /// <summary>vmangos FillInitialWorldStates for the six capitals: the war-effort counters while gathering, days left while moving.</summary>
    public void Fill(Player player, uint zoneId, List<WorldStatePair> states)
    {
        if (!_hasStore || !WarEffortPileCatalog.CapitalZones.Contains(zoneId)) return;
        foreach ((uint field, uint value) in WarEffortPileCatalog.CapitalStates(Snapshot, DateTimeOffset.FromUnixTimeSeconds(UtcNowUnix())))
            states.Add(new WorldStatePair(field, (int)Math.Min(int.MaxValue, value)));
    }

    private Dictionary<uint, uint> CapitalStateMap()
        => WarEffortPileCatalog.CapitalStates(Snapshot, DateTimeOffset.FromUnixTimeSeconds(UtcNowUnix()))
            .ToDictionary(s => s.Field, s => s.Value);

    /// <summary>AddWarEffortProgress / phase transition: SMSG_UPDATE_WORLD_STATE for each changed counter to players in a capital.</summary>
    private void PublishCapitalStates()
    {
        Dictionary<uint, uint> now = CapitalStateMap();
        Dictionary<uint, uint> before = _sentCapitalStates;
        _sentCapitalStates = now;
        if (_world is null) return;
        List<byte[]> packets = [];
        foreach ((uint field, uint value) in now)
            if (!before.TryGetValue(field, out uint old) || old != value)
                packets.Add(WorldStatePackets.BuildUpdate(field, value));
        // A state that is no longer sent (phase moved on) is cleared to 0, which hides the client's counter.
        foreach (uint field in before.Keys.Where(f => !now.ContainsKey(f)))
            packets.Add(WorldStatePackets.BuildUpdate(field, 0));
        if (packets.Count == 0) return;
        foreach (Player player in _world.OnlinePlayers.Where(p => WarEffortPileCatalog.CapitalZones.Contains(p.ZoneId)).ToArray())
            foreach (byte[] packet in packets)
                player.Session.Send(WorldOpcode.SmsgUpdateWorldState, packet);
    }

    /// <summary>
    /// Keep the classic-db 4498 resource piles of the loaded Eastern Kingdoms/Kalimdor maps at the saved state's tiers
    /// (mangos-classic ChangeWarEffortGoSpawns / vmangos HandleSupplyObjectSpawn). Runtime objects, so a restart rebuilds them.
    /// </summary>
    private void SyncPiles()
    {
        if (_world is null) return;
        HashSet<uint> wanted = WarEffortPileCatalog.Visible(Snapshot, DateTimeOffset.FromUnixTimeSeconds(UtcNowUnix()))
            .Select(p => p.Guid).ToHashSet();
        foreach (Map map in _world.Maps.Where(m => m.MapId is 0 or 1 && m.InstanceId == 0).ToArray())
        {
            if (map.FindUpdater<GameObjectMapSystem>() is not { } objects) continue;
            foreach (WarEffortPile pile in WarEffortPileCatalog.Piles)
            {
                if (pile.MapId != map.MapId) continue;
                bool exists = _piles.TryGetValue((map, pile.Guid), out GameObject? go) && go.IsSpawned;
                if (wanted.Contains(pile.Guid) && !exists)
                {
                    if (objects.Summon(pile.Entry, pile.X, pile.Y, pile.Z, pile.Orientation) is { } spawned)
                        _piles[(map, pile.Guid)] = spawned;
                }
                else if (!wanted.Contains(pile.Guid) && go is not null)
                {
                    objects.Remove(go);
                    _piles.Remove((map, pile.Guid));
                }
            }
        }
    }

    /// <summary>The live pile objects, for diagnostics and tests.</summary>
    internal IEnumerable<uint> SpawnedPileGuids(Map map)
        => _piles.Where(kv => ReferenceEquals(kv.Key.Map, map) && kv.Value.IsSpawned).Select(kv => kv.Key.Guid);

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
        LastChampionAnnouncement = BroadcastToWorld(WarEffortCatalog.ChampionBroadcastText, "$N has rung the Scarab Gong.", name ?? "champion");
    }

    /// <summary>sWorld.SendBroadcastTextToWorld: the imported broadcast text (or a fallback) as a system message to every player.</summary>
    private string BroadcastToWorld(int textId, string fallback, string? name)
    {
        string text;
        using (IServiceScope scope = scopes.CreateScope())
        {
            text = scope.ServiceProvider.GetService<CreatureWorldFeature>()?.Content.Ai.BroadcastTexts.Find((uint)textId)?.Text ?? fallback;
        }

        if (name is not null)
            text = text.Replace("$N", name, StringComparison.Ordinal).Replace("$n", name, StringComparison.Ordinal);
        LastWorldBroadcast = text;
        if (_world is null) return text;
        byte[] packet = ChatPackets.BuildSystemMessage(text);
        foreach (Player player in _world.OnlinePlayers.ToArray())
            player.Session.Send(WorldOpcode.SmsgMessagechat, packet);
        return text;
    }

    /// <summary>The last world-wide broadcast this feature sent.</summary>
    public string? LastWorldBroadcast { get; private set; }

    /// <summary>
    /// vmangos silithus.cpp npc_colossusAI. The Colossus announces itself when it appears. In combat it casts Colossal Smash (26167)
    /// 60 s after the fight starts, then alternately 10 s and 60 s after each successful cast, emoting "Colossus begins to cast Colossus
    /// Smash" at the cast and "Colossus lets loose a massive attack" 5 s later. Its evade neither heals it nor sends it home: the threat
    /// list goes and it stays where it stands. The timers are not reset by the evade (the override does not call Reset). Its death
    /// saves the Colossus flag that starts the researcher's quest event.
    /// </summary>
    internal sealed class SilithusBossAi(Creature creature, WarEffortFeature feature) : CreatureAI(creature)
    {
        public const uint ColossalSmash = 26167;
        public const uint FirstSmashMs = 60_000, ShortSmashMs = 10_000, LongSmashMs = 60_000, EmoteDelayMs = 5_000;
        internal const string CastEmote = "Colossus begins to cast Colossus Smash", AttackEmote = "Colossus lets loose a massive attack";

        private uint _smashMs = FirstSmashMs, _emoteMs;
        private bool _firstSmash = true;

        public override bool AggroesOnSight => true;
        internal uint SmashMs => _smashMs;
        internal uint EmoteMs => _emoteMs;
        internal string? LastEmote { get; private set; }

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
            _firstSmash = true; // Reset
            _smashMs = FirstSmashMs;
            _emoteMs = 0;
        }

        public override void OnUpdate(uint diffMs)
        {
            if (!Me.Combat.IsInCombat || !UpdateVictim() || Victim is null) return;
            if (_smashMs <= diffMs)
            {
                if (DoCast(Me, ColossalSmash) == CreatureCastResult.Ok) OnSmashCast();
            }
            else
                _smashMs -= diffMs;

            if (_emoteMs > 0)
            {
                if (_emoteMs <= diffMs)
                {
                    Emote(AttackEmote);
                    _emoteMs = 0;
                }
                else
                    _emoteMs -= diffMs;
            }
        }

        /// <summary>A successful Colossal Smash: the cast emote, the follow-up emote in 5 s and the next timer (10 s, then 60 s, ...).</summary>
        internal void OnSmashCast()
        {
            Emote(CastEmote);
            _smashMs = _firstSmash ? ShortSmashMs : LongSmashMs;
            _emoteMs = EmoteDelayMs;
            _firstSmash = !_firstSmash;
        }

        private void Emote(string text)
        {
            LastEmote = text;
            System?.Say(Me, new CreatureAiText(0, text, 2, 0, 0), null); // MonsterTextEmote
        }

        /// <summary>
        /// vmangos silithus.cpp npc_colossusAI::EnterEvadeMode (:531-538; Ustaag, Nostalrius): it neither heals nor walks home on evade.
        /// Every aura goes (RemoveAllAuras), the threat list is dropped and it stays put.
        /// </summary>
        public override bool OnEnterEvadeMode()
        {
            System?.RemoveAllAuras(Me);
            System?.StopCombatInPlace(Me);
            return true;
        }

        public override void OnDeath(Unit? killer) => feature.OnBossDied(Me);
    }
}
