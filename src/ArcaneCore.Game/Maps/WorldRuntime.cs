using System.Collections.Concurrent;
using System.Diagnostics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.Kernel.Characters;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// The authoritative world simulation: one dedicated thread runs a fixed-rate tick that
/// executes posted commands, then updates every map (vmangos World::Update → Map::Update).
/// All in-world state is owned by that thread; other threads interact only through
/// <see cref="Post"/> / <see cref="InvokeAsync{T}"/> and the thread-safe online registry.
/// </summary>
public sealed partial class WorldRuntime : IDisposable
{
    private readonly ILogger _logger;
    private readonly ICharacterSaveQueue _saveQueue;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly ConcurrentDictionary<Action, byte> _pendingInvocations = new();
    private volatile bool _stopped;
    private readonly Dictionary<(uint MapId, uint InstanceId), Map> _maps = [];
    private readonly List<Map> _unloadRequests = [];
    private readonly ConcurrentDictionary<ObjectGuid, Player> _online = new();
    private readonly ConcurrentDictionary<string, Player> _onlineByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ManualResetEventSlim _stopSignal = new(false);
    // Manual clock only: set by Post/InvokeAsync/AdvanceClock*/session enqueue so the world thread does not sleep a whole tick
    // interval before it looks at the queue. Never set on the real clock (production tick cadence is unchanged).
    private readonly ManualResetEventSlim _wakeSignal = new(false);
    private Thread? _thread;
    private int _worldThreadId = -1;

    /// <summary>A token that is this runtime and nothing else: stamped on reports (<c>AnomalyLedger.Report</c>'s <c>origin</c>) so a test capture can tell two worlds apart without anything holding the runtime itself alive.</summary>
    public object Identity { get; } = new();

    public WorldRuntime(WorldRuntimeOptions options, ICharacterSaveQueue saveQueue, ILogger<WorldRuntime> logger)
    {
        Options = options;
        _saveQueue = saveQueue;
        _logger = logger;
        Clock = new WorldClock(_clock);
        PendingWork = new WorldPendingWork(WakeIfManual);
        Scripts.Logger = logger;
        // .reload config refuses a bad value; a start-up file is not checked anywhere else, and a bad value turns the scaling off.
        foreach (string problem in options.Respawn.Dynamic.Problems())
        {
            logger.LogWarning("{Problem}; a bad value turns World:Respawn:Dynamic off, so with Enabled=true spawns fall back to the zone-rate scaling of Creatures:Respawn:DynamicRate (or none); fix it and .reload config.", problem);
        }
    }

    /// <summary>
    /// The global script hooks (AzerothCore ScriptMgr; docs/integration/script-hooks.md). Modules register before <see cref="Start"/>;
    /// a hook nobody registered costs its dispatch site one empty-array check.
    /// </summary>
    public Scripting.ScriptHookRegistry Scripts { get; } = new();

    public WorldRuntimeOptions Options { get; }

    /// <summary>The durable characters-database authority assigned before features attach and world startup.</summary>
    public CharacterOwnershipToken? CharacterOwnership { get; set; }

    /// <summary>Optional native player display geometry, applied before <see cref="Map.AddPlayer"/>.</summary>
    public Func<uint, ArcaneCore.Game.Spells.DisplayModelGeometry?>? PlayerDisplayModelResolver { get; set; }

    /// <summary>
    /// Tick duration, allocation and overrun statistics recorded by the world loop (docs/areas/ops-perf.md).
    /// Safe to read from any thread; only <see cref="Run"/> records into it.
    /// </summary>
    public TickStats Stats { get; } = new();

    /// <summary>The world thread's tick schedule (late and skipped starts; docs/integration/playerbot-movement-and-tick-health.md).</summary>
    public WorldTickScheduler Scheduler { get; private set; } = new(50);

    /// <summary>The tick wait in effect once the world thread runs (<see cref="WorldRuntimeOptions.TickTimer"/>, or Legacy where Precise is unavailable).</summary>
    public WorldTickTimer EffectiveTickTimer { get; private set; } = WorldTickTimer.Legacy;

    /// <summary>Test seam (the cadence harness): the spun part of a precise wait, in microseconds. Set before <see cref="Start"/>.</summary>
    internal long TickSpinMarginMicros { get; set; } = WorldTickWaiter.DefaultSpinMarginMicros;

    public int OnlinePlayerCount => _online.Count;

    /// <summary>Approximate number of commands waiting for a world-thread tick.</summary>
    public int PendingCommandCount => _commands.Count;

    /// <summary>
    /// True when the caller is the world thread (or no world thread is running), or a map thread of the parallel maps phase while it
    /// updates one of this world's maps (<see cref="TickOwner"/>): simulation code may run there and must keep its synchronous,
    /// on-thread path (an <see cref="InvokeAsync{T}"/> from a map thread would wait for a world thread that waits for it). Whether the
    /// caller may change one particular map is <see cref="IsOwner"/>; world-wide state during the parallel phase is behind <see cref="EnterSerial"/>.
    /// </summary>
    public bool IsWorldThread => _worldThreadId == -1 || Environment.CurrentManagedThreadId == _worldThreadId
        || ReferenceEquals(TickOwner.CurrentWorld, this);

    /// <summary>True only on the world thread itself (or when no world thread runs): never on a map thread.</summary>
    public bool IsMainWorldThread => _worldThreadId == -1 || Environment.CurrentManagedThreadId == _worldThreadId;

    /// <summary>Whether a character is currently in the world. Thread-safe.</summary>
    public bool IsOnline(ObjectGuid guid) => _online.ContainsKey(guid);

    /// <summary>The online player with this name (case-insensitive), or null (world thread).</summary>
    public Player? FindOnlinePlayer(string name) => _onlineByName.GetValueOrDefault(name);

    /// <summary>The online player with this GUID, or null (world thread).</summary>
    public Player? FindOnlinePlayer(ObjectGuid guid) => _online.GetValueOrDefault(guid);

    /// <summary>Every player in the world (world thread).</summary>
    public IEnumerable<Player> OnlinePlayers => _online.Values;

    /// <summary>Existing maps (world thread; a snapshot while maps update in parallel); features can install per-map systems at attachment.</summary>
    public IEnumerable<Map> Maps
    {
        get
        {
            if (!_parallelPhase)
            {
                return _maps.Values;
            }

            lock (_maps)
            {
                return [.. _maps.Values];
            }
        }
    }

    /// <summary>
    /// Chooses the map (and instance) a player enters at login and after a far teleport. Null
    /// means every map has the single instance 0 (the instances feature installs one;
    /// docs/integration/instances.md). World thread.
    /// </summary>
    public IMapResolver? MapResolver { get; set; }

    /// <summary>Announce that <paramref name="player"/> finished entering the world (world thread).</summary>
    public void NotifyLoggedIn(Player player)
    {
        Raise(PlayerLoggedIn, player, nameof(PlayerLoggedIn));
        Scripts.Player.OnLogin(player);
    }

    /// <summary>Queue one player's current state for saving (world thread).</summary>
    public void SavePlayer(Player player)
    {
        if (!player.IsQuestSettlementPending && _online.TryGetValue(player.Guid, out Player? current) && ReferenceEquals(current, player))
        {
            _saveQueue.Enqueue(player.CreateSnapshot(NowMs));
        }
    }

    /// <summary>Send a packet to every player in the world (world thread).</summary>
    public void BroadcastToAll(WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        foreach (Player player in _online.Values)
        {
            player.Session.Send(opcode, payload);
        }
    }

    /// <summary>Start the world thread.</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("world already started");
        }

        if (Clock.Mode == WorldClockMode.Stepped)
        {
            throw new InvalidOperationException("the stepped clock is driven by RunTick; it has no world thread (use the manual clock for a started world)");
        }

        _stopped = false;
        Scripts.Freeze();
        _thread = new Thread(Run) { IsBackground = true, Name = "world" };
        _thread.Start();
    }

    /// <summary>
    /// True once <see cref="Stop"/> has retired the world thread: from then on every <see cref="InvokeAsync{T}"/> is cancelled
    /// rather than run, so a caller that sees a cancelled invocation while this is set knows the world is gone, not that its own
    /// token fired.
    /// </summary>
    public bool IsStopped => _stopped;

    /// <summary>
    /// Stop the world thread, then save every online character. After this returns the world
    /// thread is gone, so this method may touch world state itself.
    /// </summary>
    public void Stop()
    {
        if (_thread is null)
        {
            return;
        }

        _stopSignal.Set();
        _thread.Join();
        _thread = null;
        _worldThreadId = -1;

        // Commands posted before shutdown (e.g. a disconnect's save) still run.
        RunCommands(drainAll: true);

        // Nothing drains the queue any more: an InvokeAsync still waiting (or posted from now on) is cancelled instead of left hanging.
        _stopped = true;
        CancelPendingInvocations();
        SaveAll();
        foreach (ManualAdvance advance in _manualAdvances)
        {
            advance.Done.TrySetResult(false);
        }

        _manualAdvances.Clear();
    }

    /// <summary>Queue work for the start of the next tick. Thread-safe.</summary>
    public void Post(Action command)
    {
        _commands.Enqueue(command);
        WakeIfManual();
    }

    /// <summary>
    /// Manual clock only: end the world thread's idle wait now, so work that was just queued (a command, a session packet) runs on
    /// the next tick instead of after the rest of the tick interval. A no-op on the real clock. Thread-safe, allocation-free.
    /// </summary>
    public void WakeIfManual()
    {
        if (_manualClock)
        {
            _wakeSignal.Set();
        }
    }

    /// <summary>
    /// Run <paramref name="func"/> on the world thread and return its result. The task is cancelled when <paramref name="cancellationToken"/>
    /// is (a command not yet run is then skipped) and when the world is stopped before the command ran, so a caller never waits on a
    /// world thread that is gone.
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action cancel = () => completion.TrySetCanceled();
        _pendingInvocations[cancel] = 0;
        CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            completion.TrySetCanceled(cancellationToken);
            _pendingInvocations.TryRemove(cancel, out _);
        });
        completion.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        Post(() =>
        {
            _pendingInvocations.TryRemove(cancel, out _);
            if (completion.Task.IsCompleted)
            {
                return; // cancelled while queued
            }

            try
            {
                completion.TrySetResult(func());
            }
            catch (Exception ex)
            {
                FaultLedger.Shared.Record("world.invoke", ex);
                completion.TrySetException(ex);
            }
        });

        // Stop sets the flag before it cancels, so either it sees this entry or this sees the flag.
        if (_stopped)
        {
            CancelPendingInvocations();
        }

        return completion.Task;
    }

    private void CancelPendingInvocations()
    {
        foreach (Action cancel in _pendingInvocations.Keys)
        {
            if (_pendingInvocations.TryRemove(cancel, out _))
            {
                cancel();
            }
        }
    }

    /// <summary>
    /// The map with the given id (instance 0: continents and other shared maps), created on
    /// first use with its default per-map systems (<see cref="DefaultMapUpdaters"/>; world thread).
    /// </summary>
    public Map GetMap(uint mapId) => GetMap(mapId, 0);

    /// <summary>
    /// One instance of a map, created on first use with its default per-map systems (vmangos
    /// <c>MapManager::CreateMap</c> / <c>CreateDungeonMap</c>, keyed by <c>MapID(id, instance)</c>;
    /// world thread). Instance 0 is the shared copy.
    /// </summary>
    public Map GetMap(uint mapId, uint instanceId)
    {
        if (_parallelPhase)
        {
            return GetMapInParallelPhase(mapId, instanceId);
        }

        if (!_maps.TryGetValue((mapId, instanceId), out Map? map))
        {
            map = new Map(mapId, instanceId, this, _logger);
            DefaultMapUpdaters.AttachTo(map, this);
            _maps[(mapId, instanceId)] = map;
            Raise(MapCreated, map, nameof(MapCreated));
        }

        return map;
    }

    /// <summary>
    /// <see cref="GetMap(uint, uint)"/> while maps update in parallel (W2.4): the lookup is locked, and a map that must be made is made
    /// inside the serial gate (its default updaters and the <see cref="MapCreated"/> handlers touch world features). The new map is not
    /// in this tick's map list, so it is first updated next tick, as in the serial loop (which updates a snapshot of the maps too).
    /// </summary>
    private Map GetMapInParallelPhase(uint mapId, uint instanceId)
    {
        lock (_maps)
        {
            if (_maps.TryGetValue((mapId, instanceId), out Map? existing))
            {
                return existing;
            }
        }

        using SerialScope gate = EnterSerial();
        Map? map;
        lock (_maps)
        {
            if (_maps.TryGetValue((mapId, instanceId), out map))
            {
                return map;
            }
        }

        map = new Map(mapId, instanceId, this, _logger);
        DefaultMapUpdaters.AttachTo(map, this);
        lock (_maps)
        {
            _maps[(mapId, instanceId)] = map;
        }

        Raise(MapCreated, map, nameof(MapCreated));
        return map;
    }

    /// <summary>An existing map instance, or null (vmangos <c>MapManager::FindMap</c>; world thread).</summary>
    public Map? FindMap(uint mapId, uint instanceId = 0)
    {
        if (!_parallelPhase)
        {
            return _maps.GetValueOrDefault((mapId, instanceId));
        }

        lock (_maps)
        {
            return _maps.GetValueOrDefault((mapId, instanceId));
        }
    }

    /// <summary>
    /// Ask for an instance map to be unloaded after the current map pass (world thread). It is
    /// unloaded only if it is still registered, is not instance 0, has no players and nobody is
    /// in transit from it. Requests wait for transit to finish; occupied or replaced maps drop
    /// their request and the owner asks again later.
    /// </summary>
    public void UnloadMap(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        lock (_unloadRequests) // a map thread may ask during the parallel maps phase; the requests are served after it
        {
            if (map.InstanceId != 0 && !_unloadRequests.Contains(map))
            {
                _unloadRequests.Add(map);
            }
        }
    }

    /// <summary>
    /// The map a logging-in player enters (world thread). A dungeon login without its instance moves
    /// the player to the entrance here (vmangos Player::LoadFromDB), so call this before anything
    /// that reads the player's map or position, then hand the result to <see cref="AddPlayer"/>.
    /// </summary>
    public Map ResolveLoginMap(Player player) => MapResolver?.ResolveLoginMap(player) ?? GetMap(player.MapId);

    /// <summary>
    /// Put a loaded player into its map and the online registry (world thread).
    /// <paramref name="resolvedMap"/> is the result of <see cref="ResolveLoginMap"/> when the caller already resolved it.
    /// </summary>
    public void AddPlayer(Player player, Map? resolvedMap = null)
    {
        if (player.CharacterOwnership is null && CharacterOwnership is { } ownership)
            player.CharacterOwnership = ownership;
        if (!_online.TryAdd(player.Guid, player))
        {
            throw new InvalidOperationException($"{player.Name} ({player.Guid}) is already online");
        }

        _onlineByName[player.Name] = player;
        _autosaveRescan = true; // its autosave timer starts at the next autosave scan
        player.StartPlayedTime(NowMs);
        Map map = resolvedMap ?? ResolveLoginMap(player);
        player.InitializeNativeDisplayModel(PlayerDisplayModelResolver);
        MapResolver?.OnLoginMapAdd(player, map);
        map.AddPlayer(player);
    }

    /// <summary>Take a player out of the world and queue its state for saving (world thread).</summary>
    public void RemovePlayer(Player player)
    {
        if (!_online.TryGetValue(player.Guid, out Player? current) || !ReferenceEquals(current, player))
        {
            return;
        }

        Raise(PlayerLoggingOut, player, nameof(PlayerLoggingOut));
        Scripts.Player.OnLogout(player);
        player.Map?.RemovePlayer(player);
        if (!player.IsQuestSettlementPending)
        {
            _saveQueue.Enqueue(player.CreateSnapshot(NowMs));
        }
        // Publish offline only after the final snapshot is queued: login's save barrier
        // must never overtake this old session's last write.
        _onlineByName.TryRemove(new KeyValuePair<string, Player>(player.Name, player));
        _online.TryRemove(new KeyValuePair<ObjectGuid, Player>(player.Guid, player));
        _autosaveDue.Remove(player.Guid); // a later login starts a new timer
    }

    /// <summary>
    /// Complete a logout (world thread): save and remove the player, then return its client to
    /// the character screen (vmangos WorldSession::LogoutPlayer → SMSG_LOGOUT_COMPLETE).
    /// </summary>
    public void LogoutPlayer(Player player)
    {
        if (!_online.TryGetValue(player.Guid, out Player? current) || !ReferenceEquals(current, player))
        {
            return;
        }

        RemovePlayer(player);
        player.Session.OnLoggedOut();
    }

    /// <summary>One tick: posted commands, then every map (world thread; tests call it directly).</summary>
    public void RunTick(uint diffMs) => RunTick(diffMs, null);

    /// <summary>Benchmark seam: observe completed map phases without enabling warning logs.</summary>
    internal void RunTick(uint diffMs, Action<Map, MapUpdateDiagnostics>? observeMap)
    {
        // First: every reading of this tick (NowMs, Uptime, UtcNow) is fixed from here on.
        Clock.BeginTick(diffMs);

        // Before anything of this tick runs: the previous tick's phases, duration and slowest features are complete and still readable.
        RaiseTickStarting(diffMs);
        long phaseStart = Stopwatch.GetTimestamp();
        long bytesStart = GC.GetAllocatedBytesForCurrentThread();
        _topCount = 0;
        SetPosition(TickPhase.Commands, null, phaseStart);
        RunCommands(drainAll: Clock.IsDeterministic);
        long commandsEnd = Stopwatch.GetTimestamp();
        long commandsBytesEnd = GC.GetAllocatedBytesForCurrentThread();
        // World-level services (game events, rest) run before the maps. Their time counts as world
        // features and is also timed per handler ("WorldTick:<Type>"). The tick watchdog is not one of them: it runs on TickStarting.
        SetPosition(TickPhase.WorldTick, null, commandsEnd);
        RaiseWorldTick(diffMs);
        try
        {
            Scripts.World.OnUpdate(diffMs);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("world.script", ex);
            _logger.LogFault("world.script", ex, "world script {Hook} failed (diff={DiffMs} ms, online={Online}, maps={Maps})",
                nameof(Scripting.IWorldHooks.OnUpdate), diffMs, _online.Count, _maps.Count);
        }
        long worldTickEnd = Stopwatch.GetTimestamp();
        long worldTickBytesEnd = GC.GetAllocatedBytesForCurrentThread();

        SetPosition(TickPhase.Maps, null, worldTickEnd);

        // A snapshot: a map system may create another map (an instance) during its update.
        int mapCount = _maps.Count;
        Map[] maps = System.Buffers.ArrayPool<Map>.Shared.Rent(Math.Max(mapCount, 1));
        _maps.Values.CopyTo(maps, 0);
        long parallelMapBytes = 0;
        if (Options.Parallel.EffectiveMapUpdateThreads > 1)
        {
            EnsureMapScheduler();
        }

        try
        {
            if (_mapScheduler is { } scheduler && mapCount > 1)
            {
                parallelMapBytes = RunMapsParallel(scheduler, maps, mapCount, diffMs, observeMap);
                mapCount = 0; // the serial loop below has nothing left to do
            }

            for (int mapIndex = 0; mapIndex < mapCount; mapIndex++)
            {
                Map map = maps[mapIndex];
                long mapStart = Stopwatch.GetTimestamp();
                SetPosition(TickPhase.Maps, map, mapStart);
                MapUpdateDiagnostics? diagnostics = observeMap is not null || Options.Perf.SlowMapUpdate > 0 && _logger.IsEnabled(LogLevel.Warning)
                    ? map.ReusableDiagnostics()
                    : null;
                // The collector's counters around this map's update, for the slow line's "gc" share (two cached-delegate reads of four counters).
                GcSnapshot gcBeforeMap = diagnostics is not null ? GcSource() : default;
                try
                {
                    map.Update(diffMs, diagnostics);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("world.map-update", ex);
                    _logger.LogFault("map.update", ex,
                        "map {MapId} instance {InstanceId} update failed (diff={DiffMs} ms, players={Players}, objects={Objects})",
                        map.MapId, map.InstanceId, diffMs, map.PlayerCount, map.ObjectCount);
                }

                if (MapUpdated is { } mapUpdated)
                {
                    try
                    {
                        mapUpdated(map, Micros(Stopwatch.GetTimestamp() - mapStart));
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        FaultLedger.Shared.Record("world.map-observer", ex);
                        _logger.LogFault("map.observer", ex, "MapUpdated observer failed for map {MapId} instance {InstanceId}", map.MapId, map.InstanceId);
                    }
                }

                map.LastUpdateMicros = Micros(Stopwatch.GetTimestamp() - mapStart);
                LogIfSlow(Options.Perf.SlowMapUpdate, mapStart, "Slow map update", map, diagnostics, gcBeforeMap);
                if (diagnostics is not null && observeMap is not null)
                {
                    try
                    {
                        observeMap(map, diagnostics);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        FaultLedger.Shared.Record("world.map-observer", ex);
                        _logger.LogFault("map.observer", ex, "map diagnostics observer failed for map {MapId} instance {InstanceId}", map.MapId, map.InstanceId);
                    }
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<Map>.Shared.Return(maps, clearArray: true);
        }

        UnloadRequestedMaps();

        // vmangos World::Update ends with sTerrainMgr.Update (World.cpp:2147-2149): the terrain clean-up of every map id, once per tick and
        // after the maps, since one map id's terrain serves all of its instances.
        try
        {
            WorldMaps.Of(this).Terrain.Update(diffMs);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("world.terrain-update", ex);
            _logger.LogFault("map.terrain", ex, "terrain cleanup failed (diff={DiffMs} ms, maps={Maps})", diffMs, _maps.Count);
        }

        long mapsEnd = Stopwatch.GetTimestamp();
        long mapsBytesEnd = GC.GetAllocatedBytesForCurrentThread();
        SetPosition(TickPhase.Features, null, mapsEnd);
        RaiseTimed(Updated, diffMs);
        long featuresEnd = Stopwatch.GetTimestamp();
        long featuresBytesEnd = GC.GetAllocatedBytesForCurrentThread();

        long autosaveMicros = 0;
        int autosaveSnapshots = 0;
        _autosaveClockMs += diffMs;
        if (Options.AutosaveIntervalMs > 0)
        {
            SetPosition(TickPhase.Autosave, null, featuresEnd);
            // A tick that scanned the timers counts as an autosave tick (TickStats) whether or not anyone was due: the scan is the cost.
            if (Autosave((ulong)Options.AutosaveIntervalMs, out autosaveSnapshots))
            {
                autosaveMicros = Math.Max(1, Micros(Stopwatch.GetTimestamp() - featuresEnd));
            }
        }

        // Assigned after the autosave so the split accounts for every part of the tick body.
        LastTickPhases = new TickPhases(Micros(commandsEnd - phaseStart), Micros(mapsEnd - worldTickEnd),
            Micros((worldTickEnd - commandsEnd) + (featuresEnd - mapsEnd)))
        {
            CommandsBytes = commandsBytesEnd - bytesStart,
            MapsBytes = mapsBytesEnd - worldTickBytesEnd + parallelMapBytes,
            FeaturesBytes = (worldTickBytesEnd - commandsBytesEnd) + (featuresBytesEnd - mapsBytesEnd),
            WorldTickMicros = Micros(worldTickEnd - commandsEnd),
            AutosaveMicros = autosaveMicros,
            AutosaveSnapshots = autosaveSnapshots,
            CommandsRun = _commandsRun,
        };
        LastTickDurationMicros = Micros(Stopwatch.GetTimestamp() - phaseStart);
        SetPosition(TickPhase.Idle, null, 0);
        _perfLog?.FlushIfDue();
    }

    public void Dispose()
    {
        Stop();
        DisposeMapScheduler();
        _stopSignal.Dispose();
        // A session may still enqueue (and wake) after shutdown: ManualResetEventSlim.Set does not check disposal (it only flips the state
        // bit and, under the lock, sets an event object that Dispose already nulled), so that late Set is harmless. Wait and Reset do throw
        // once disposed, but only the world thread calls them and it is gone by now.
        _wakeSignal.Dispose();
    }

    private void UnloadRequestedMaps()
    {
        if (_unloadRequests.Count == 0)
        {
            return;
        }

        Map[] pending = [.. _unloadRequests];
        _unloadRequests.Clear();
        foreach (Map map in pending)
        {
            if (!_maps.TryGetValue((map.MapId, map.InstanceId), out Map? current) || !ReferenceEquals(current, map)
                || map.PlayerCount > 0)
            {
                continue;
            }

            if (map.TransitCount > 0)
            {
                // A reset's unload timer has already expired. Keep this exact source map's
                // request until its final worldport acknowledgment (or disconnect) drains transit.
                _unloadRequests.Add(map);
                continue;
            }

            Raise(MapUnloading, map, nameof(MapUnloading));
            _maps.Remove((map.MapId, map.InstanceId));
            try
            {
                map.UnloadAll();
            }
            catch (Exception ex)
            {
                FaultLedger.Shared.Record("world.map-unload", ex);
                _logger.LogFault("map.unload", ex, "unloading map {MapId} instance {InstanceId} failed", map.MapId, map.InstanceId);
            }
        }
    }

    private void RunCommands(bool drainAll = false)
    {
        int admitted = 0;
        int run = 0;
        long started = Stopwatch.GetTimestamp();
        int maxCommands = Math.Max(1, Options.MaxCommandsPerTick);
        long budgetTicks = Options.CommandTimeBudgetMs > 0
            ? Options.CommandTimeBudgetMs * Stopwatch.Frequency / 1000L
            : 0;
        while (_commands.TryDequeue(out Action? command))
        {
            run++;
            SetPosition(TickPhase.Commands, command, Stopwatch.GetTimestamp());
            try
            {
                command();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("world.command", ex);
                _logger.LogFault("world.command", ex,
                    "world command {Command}.{Method} (#{Run}, pending={Pending}) failed",
                    TickWorkNames.Of(command), command.Method.Name, run, _commands.Count);
            }

            if (drainAll)
            {
                continue;
            }

            admitted++;
            if (admitted >= maxCommands || budgetTicks > 0
                && Stopwatch.GetTimestamp() - started >= budgetTicks)
            {
                break;
            }
        }

        _commandsRun = run;
    }

    /// <summary>Queue every online character for saving (world thread, or after <see cref="Stop"/>).</summary>
    public void SaveAll()
    {
        uint now = NowMs;
        foreach (Player player in _online.Values)
        {
            if (!player.IsQuestSettlementPending)
            {
                try
                {
                    _saveQueue.Enqueue(player.CreateSnapshot(now));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("world.character-save", ex);
                    _logger.LogFault("character.save-snapshot", ex,
                        "failed to snapshot player {Player} ({Guid}) on map {MapId} at ({X:F1},{Y:F1},{Z:F1})",
                        player.Name, player.Guid, player.MapId, player.X, player.Y, player.Z);
                }
            }
        }
    }

    private void Run()
    {
        _worldThreadId = Environment.CurrentManagedThreadId;
        int interval = Math.Max(1, Options.TickIntervalMs);
        int tolerance = Math.Clamp(Options.TickLateToleranceMs, 0, 1000);
        Scheduler = new WorldTickScheduler(interval, tolerance);
        Stats.FrameOverrunToleranceMicros = tolerance * 1000L;
        using var waiter = new WorldTickWaiter(Options.TickTimer, _stopSignal, TickSpinMarginMicros, _manualClock ? _wakeSignal : null);
        EffectiveTickTimer = waiter.Mode;
        long last = _clock.ElapsedMilliseconds;
        long lastStartTicks = -1;
        _logger.LogInformation("World thread started ({Interval} ms tick, {Timer} timer, late after {Tolerance} ms)", interval, waiter.Mode, tolerance);
        try
        {
            Scripts.World.OnStartup();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("world.script", ex);
            _logger.LogFault("world.script", ex, "world script {Hook} failed (diff={DiffMs} ms, online={Online}, maps={Maps})",
                nameof(Scripting.IWorldHooks.OnStartup), 0u, _online.Count, _maps.Count);
        }

        while (!_stopSignal.IsSet)
        {
            if (_manualClock)
            {
                // Before the tick drains the queue: anything queued after this reset sets the signal again and ends the next wait.
                _wakeSignal.Reset();
            }

            long startTicks = _clock.ElapsedTicks;
            long tickStart = startTicks * 1000 / Stopwatch.Frequency;
            uint diff = (uint)Math.Clamp(tickStart - last, 0, uint.MaxValue);
            last = tickStart;
            // The frame interval on the precise clock (the diff is whole milliseconds); the manual clock reports its own steps.
            long frameMicros = lastStartTicks < 0 ? 0 : (startTicks - lastStartTicks) * 1_000_000 / Stopwatch.Frequency;
            lastStartTicks = startTicks;
            if (_manualClock)
            {
                diff = HoldForPendingWork(NextManualDiff());
                frameMicros = diff * 1000L;
            }

            PerfLogAggregator? perfLog = PerfLog;
            perfLog?.CountFrame();

            // vmangos WorldRunnable.cpp:73-74: the frame interval (sleep included), checked before the update.
            if (Options.Perf.SlowWorldUpdateMeasure == SlowWorldUpdateMeasure.FrameInterval
                && Options.Perf.SlowWorldUpdate > 0 && diff > (uint)Options.Perf.SlowWorldUpdate
                && (perfLog is null || perfLog.RecordWorldFrame(diff * 1000L)))
            {
                _logger.LogWarning(PerformanceLogOptions.PerfEventId, "Slow world update: {DurationMs}ms", diff);
            }

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            Func<GcSnapshot> gcSource = GcSource; // one read; the seam is replaced only before the loop runs
            GcSnapshot gcBefore = gcSource();
            long stampBefore = Stopwatch.GetTimestamp();
            RunTick(diff, MapUpdateObserver);
            long durationMicros = (Stopwatch.GetTimestamp() - stampBefore) * 1_000_000 / Stopwatch.Frequency;
            GcDelta gcDelta = gcSource().Since(gcBefore);
            long tickBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            // The GC split belongs to the phases the slow-tick record, the watchdog's hang line and the slow-update line all print.
            LastTickPhases = LastTickPhases with { GcCaptured = true, Gc = gcDelta };
            Stats.Record(durationMicros, tickBytes, interval * 1000L, frameMicros, LastTickPhases);
            if (durationMicros > interval * 1000L)
            {
                Stats.RecordSlowTick(LastTickPhases, _topNames, _topMicros, _topCount, durationMicros);
            }

            if (TickObserver is { } tickObserver)
            {
                try
                {
                    tickObserver(durationMicros, tickBytes, LastTickPhases);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("world.tick-observer", ex);
                    _logger.LogFault("world.tick-observer", ex, "TickObserver failed (duration={DurationMicros} us)", durationMicros);
                }
            }

            if (TickCompleted is { } tickCompleted)
            {
                try
                {
                    tickCompleted(durationMicros, tickBytes, LastTickPhases);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("world.tick-observer", ex);
                    _logger.LogFault("world.tick-observer", ex, "TickCompleted observer failed (duration={DurationMicros} us)", durationMicros);
                }
            }
            if (Options.Perf.SlowWorldUpdateMeasure == SlowWorldUpdateMeasure.TickDuration
                && Options.Perf.SlowWorldUpdate > 0 && durationMicros > Options.Perf.SlowWorldUpdate * 1000L
                && (perfLog is null || perfLog.RecordWorldFrame(durationMicros)))
            {
                _logger.LogWarning(PerformanceLogOptions.PerfEventId, "Slow world update: {DurationMs} ms (interval {IntervalMs} ms): {Phases}",
                    durationMicros / 1000, interval, LastTickPhases.Format());
            }

            perfLog?.FlushIfDue();

            if (_manualClock)
            {
                SettleManualAdvances(diff);
                if (_manualAdvances.Count > 0 && !_manualHeld)
                {
                    continue; // advances run their ticks back to back
                }
            }

            if (_manualClock)
            {
                // Idle wait of one interval, cut short by a wake. The drift-compensated cadence is not used: early wakes would
                // push its due time ahead and stretch the periodic zero-diff tick.
                waiter.WaitUntil(_clock, _clock.ElapsedTicks + interval * Stopwatch.Frequency / 1000, interval);
                continue;
            }

            int wait = Scheduler.NextWait(tickStart, _clock.ElapsedMilliseconds);
            if (wait > 0 && Scheduler.NextDueMs is { } dueMs)
            {
                waiter.WaitUntil(_clock, dueMs * Stopwatch.Frequency / 1000, wait);
            }
        }

        DisposeMapScheduler();
        _perfLog?.FlushNow(); // the slow updates of the interval in progress are not lost with the process
        _logger.LogInformation("World thread stopped");
    }
}
