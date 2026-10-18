using System.Collections.Concurrent;
using System.Diagnostics;
using ArcaneCore.Game.Entities;
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
    private Thread? _thread;
    private int _worldThreadId = -1;
    private ulong _autosaveClockMs; // world thread: the sum of tick diffs, the clock the per-character autosave timers run on
    private readonly Dictionary<ObjectGuid, ulong> _autosaveDue = []; // world thread: each online character's next autosave on _autosaveClockMs
    private ulong _autosaveNextDue; // world thread: the earliest entry of _autosaveDue at the last scan (a removal can leave it early: one extra scan)
    private bool _autosaveRescan = true; // world thread: a player came online since the last scan, whose timer is not set yet

    /// <summary>A token that is this runtime and nothing else: stamped on reports (<c>AnomalyLedger.Report</c>'s <c>origin</c>) so a test capture can tell two worlds apart without anything holding the runtime itself alive.</summary>
    public object Identity { get; } = new();

    // The world thread's position and the slowest features of the tick in progress (docs/ops/watchdog.md). Written by the
    // world thread only; the position is read by the watchdog thread, so it is published with volatile stores.
    private volatile int _phase;
    private volatile object? _currentWork;
    private long _currentWorkSince;
    private readonly string?[] _topNames = new string?[TickStats.TopFeatureCount];
    private readonly long[] _topMicros = new long[TickStats.TopFeatureCount];
    private int _topCount;
    private int _commandsRun;

    /// <summary>
    /// The handlers of <see cref="Updated"/> and <see cref="WorldTick"/> with their tick-statistics names, kept for the multicast delegate they
    /// were split from (<see cref="HandlersOf"/>): the invocation list, its cast and the name lookups were per tick. A delegate is immutable,
    /// so a subscription change makes a new one and the next tick splits it again. World thread.
    /// </summary>
    private HandlerTable _updatedHandlers = HandlerTable.None;
    private HandlerTable _worldTickHandlers = HandlerTable.None;

    // Manual clock (test seam, see UseManualClock): game time advances only through AdvanceClockAsync.
    private bool _manualClock;
    private uint _manualStepMs;
    private long _manualNowMs;
    private long _manualTick; // world thread only
    private readonly List<ManualAdvance> _manualAdvances = []; // world thread only

    public WorldRuntime(WorldRuntimeOptions options, ICharacterSaveQueue saveQueue, ILogger<WorldRuntime> logger)
    {
        Options = options;
        _saveQueue = saveQueue;
        _logger = logger;
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

    /// <summary>
    /// Milliseconds since the world started, wrapping like vmangos WorldTimer::getMSTime.
    /// This is the clock movement timestamps and create blocks carry.
    /// </summary>
    public uint NowMs => _manualClock
        ? unchecked((uint)Interlocked.Read(ref _manualNowMs))
        : unchecked((uint)_clock.ElapsedMilliseconds);

    /// <summary>Time since the world was created (does not wrap, unlike <see cref="NowMs"/>).</summary>
    public TimeSpan Uptime => _manualClock ? TimeSpan.FromMilliseconds(Interlocked.Read(ref _manualNowMs)) : _clock.Elapsed;

    /// <summary>Whether <see cref="UseManualClock"/> froze the simulation clock.</summary>
    public bool IsManualClock => _manualClock;

    /// <summary>
    /// Test seam (never configuration-bound): from now on game time — <see cref="NowMs"/>, <see cref="Uptime"/> and
    /// every tick's diff — advances only through <see cref="AdvanceClockAsync"/>, in steps of at most
    /// <paramref name="stepMs"/> (default 50, vmangos WORLD_SLEEP_CONST). Posted commands still run every
    /// <see cref="WorldRuntimeOptions.TickIntervalMs"/> of wall time (with a zero diff), so sessions and
    /// <see cref="InvokeAsync{T}"/> keep working while the simulation stands still. Call before <see cref="Start"/>.
    /// </summary>
    public void UseManualClock(uint stepMs = 50)
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("the manual clock must be chosen before the world starts");
        }

        if (stepMs == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stepMs), "the manual clock step must be positive");
        }

        _manualStepMs = stepMs;
        // Continue from the current reading, so values cached by features that attached earlier never run backwards.
        Interlocked.Exchange(ref _manualNowMs, _clock.ElapsedMilliseconds);
        _manualClock = true;
    }

    /// <summary>
    /// Manual clock only: run the simulation forward by <paramref name="milliseconds"/> of game time, in ticks of at
    /// most the configured step, back to back. Completes on the world thread after the last of those ticks.
    /// </summary>
    public Task AdvanceClockAsync(uint milliseconds) => AdvanceClockUntilAsync(milliseconds, null);

    /// <summary>
    /// Manual clock only: run the simulation forward tick by tick until <paramref name="condition"/> (evaluated on the
    /// world thread after every tick) holds — true — or <paramref name="maxMilliseconds"/> of game time have passed —
    /// false. Without a condition it simply advances and completes true. Concurrent advances share the ticks.
    /// </summary>
    public Task<bool> AdvanceClockUntilAsync(uint maxMilliseconds, Func<bool>? condition)
    {
        if (!_manualClock)
        {
            throw new InvalidOperationException("the world clock is not manual");
        }

        var advance = new ManualAdvance(maxMilliseconds, condition);
        Post(() =>
        {
            advance.AddedAtTick = _manualTick;
            _manualAdvances.Add(advance);
        });
        return advance.Done.Task;
    }

    private sealed class ManualAdvance(long remainingMs, Func<bool>? until)
    {
        public long Remaining = remainingMs;
        public long AddedAtTick = -1;
        public Func<bool>? Until { get; } = until;
        public TaskCompletionSource<bool> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The next manual tick's diff: the configured step, cut to the largest remaining advance (0: commands only).</summary>
    private uint NextManualDiff()
    {
        _manualTick++;
        long wanted = 0;
        foreach (ManualAdvance advance in _manualAdvances)
        {
            wanted = Math.Max(wanted, advance.Remaining);
        }

        return (uint)Math.Min(wanted, _manualStepMs);
    }

    /// <summary>After a manual tick: charge it to the advances that existed before it and complete the finished ones.</summary>
    private void SettleManualAdvances(uint diff)
    {
        for (int i = _manualAdvances.Count - 1; i >= 0; i--)
        {
            ManualAdvance advance = _manualAdvances[i];
            if (advance.AddedAtTick == _manualTick)
            {
                continue; // added by a command of this very tick: it starts with the next one
            }

            advance.Remaining -= diff;
            bool reached;
            try
            {
                reached = advance.Until?.Invoke() ?? false;
            }
            catch (Exception ex)
            {
                _manualAdvances.RemoveAt(i);
                advance.Done.TrySetException(ex);
                continue;
            }

            if (reached || advance.Remaining <= 0)
            {
                _manualAdvances.RemoveAt(i);
                advance.Done.TrySetResult(reached || advance.Until is null);
            }
        }
    }

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
    /// Raised on the world thread after a map and its default updaters have been registered.
    /// Features install their per-map simulation here, including maps first visited later.
    /// </summary>
    public event Action<Map>? MapCreated;

    /// <summary>
    /// Raised on the world thread when an instance map is about to be unloaded (it is empty,
    /// nobody is in transit from it, and its owner asked with <see cref="UnloadMap"/>), before
    /// its grids are unloaded and it leaves <see cref="Maps"/>. Features drop their per-map
    /// state here (vmangos <c>MapManager::DeleteInstance</c> → <c>Map::UnloadAll</c>).
    /// </summary>
    public event Action<Map>? MapUnloading;

    /// <summary>
    /// Raised once per world tick after posted commands and map updates, even when no maps
    /// exist. The argument is elapsed milliseconds. World features use this for global
    /// maintenance; a failing handler is logged and does not stop the others.
    /// </summary>
    public event Action<uint>? Updated;

    /// <summary>
    /// Chooses the map (and instance) a player enters at login and after a far teleport. Null
    /// means every map has the single instance 0 (the instances feature installs one;
    /// docs/integration/instances.md). World thread.
    /// </summary>
    public IMapResolver? MapResolver { get; set; }

    /// <summary>
    /// Raised on the world thread once a player has entered the world and its client has the
    /// full login sequence, i.e. after vmangos' <c>SendInitialPacketsAfterAddToMap</c> — where
    /// vmangos <c>HandlePlayerLogin</c> announces the member to its group and sends friend
    /// status. Raised by the login path through <see cref="NotifyLoggedIn"/>. A failing handler
    /// is logged and does not stop the others.
    /// </summary>
    public event Action<Player>? PlayerLoggedIn;

    /// <summary>
    /// Raised on the world thread when a player is about to leave the world (logout completion
    /// or disconnect), while it is still in its map and the online registry, before it is
    /// saved. A failing handler is logged and does not stop the others or the removal.
    /// </summary>
    public event Action<Player>? PlayerLoggingOut;

    /// <summary>
    /// Raised once per world tick on the world thread, after the posted commands and before the maps update,
    /// with the tick's elapsed milliseconds (vmangos <c>World::Update</c> drives <c>sGameEventMgr.Update</c> from
    /// its own timers, World.cpp:2106-2111; this is the equivalent seam for world-level services). A failing
    /// handler is logged and does not stop the others.
    /// </summary>
    public event Action<uint>? WorldTick;

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
    public void Post(Action command) => _commands.Enqueue(command);

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

    /// <summary>Put a loaded player into its map and the online registry (world thread).</summary>
    public void AddPlayer(Player player)
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
        Map map = MapResolver?.ResolveLoginMap(player) ?? GetMap(player.MapId);
        player.InitializeNativeDisplayModel(PlayerDisplayModelResolver);
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
        if (_manualClock)
        {
            Interlocked.Add(ref _manualNowMs, diffMs);
        }

        long phaseStart = Stopwatch.GetTimestamp();
        long bytesStart = GC.GetAllocatedBytesForCurrentThread();
        _topCount = 0;
        SetPosition(TickPhase.Commands, null, phaseStart);
        RunCommands();
        long commandsEnd = Stopwatch.GetTimestamp();
        long commandsBytesEnd = GC.GetAllocatedBytesForCurrentThread();
        // World-level services (game events, rest, the tick watchdog) run before the maps. Their time counts as world
        // features and is also timed per handler ("WorldTick:<Type>"); the watchdog's handler stays first and is a few stores.
        SetPosition(TickPhase.WorldTick, null, commandsEnd);
        RaiseWorldTick(diffMs);
        try
        {
            Scripts.World.OnUpdate(diffMs);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogFault("world-script", ex, "world script {Hook} failed (diff={DiffMs} ms, online={Online}, maps={Maps})",
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
                    _logger.LogFault("map-update", ex,
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
                        _logger.LogFault("map-observer", ex, "MapUpdated observer failed for map {MapId} instance {InstanceId}", map.MapId, map.InstanceId);
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
                        _logger.LogFault("map-observer", ex, "map diagnostics observer failed for map {MapId} instance {InstanceId}", map.MapId, map.InstanceId);
                    }
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<Map>.Shared.Return(maps, clearArray: true);
        }

        UnloadRequestedMaps();
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
        SetPosition(TickPhase.Idle, null, 0);
    }

    /// <summary>
    /// Where the world thread is now (any thread; the watchdog's hang log): the tick phase and the feature, command or map running in it.
    /// <see cref="TickPhase.Idle"/> between ticks.
    /// </summary>
    public TickPosition CurrentPosition => new((TickPhase)_phase, _currentWork, Volatile.Read(ref _currentWorkSince));

    private void SetPosition(TickPhase phase, object? work, long since)
    {
        _currentWork = work;
        Volatile.Write(ref _currentWorkSince, since);
        _phase = (int)phase;
    }

    /// <summary>Keep the slowest <see cref="TickStats.TopFeatureCount"/> features of the tick in progress (no allocation).</summary>
    private void NoteSlowest(string name, long micros)
    {
        int index;
        if (_topCount < _topNames.Length)
        {
            index = _topCount++;
        }
        else if (micros > _topMicros[^1])
        {
            index = _topNames.Length - 1;
        }
        else
        {
            return;
        }

        while (index > 0 && _topMicros[index - 1] < micros)
        {
            _topNames[index] = _topNames[index - 1];
            _topMicros[index] = _topMicros[index - 1];
            index--;
        }

        _topNames[index] = name;
        _topMicros[index] = micros;
    }

    public void Dispose()
    {
        Stop();
        DisposeMapScheduler();
        _stopSignal.Dispose();
    }

    private void LogIfSlow(int thresholdMs, long startTimestamp, string what, Map map, MapUpdateDiagnostics? diagnostics, GcSnapshot gcBefore)
    {
        if (thresholdMs <= 0)
        {
            return;
        }

        LogIfSlowMicros(thresholdMs, (Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000 / Stopwatch.Frequency, what, map, diagnostics, gcBefore);
    }

    private void LogIfSlowMicros(int thresholdMs, long micros, string what, Map map, MapUpdateDiagnostics? diagnostics, GcSnapshot gcBefore)
    {
        if (thresholdMs <= 0)
        {
            return;
        }

        if (micros > thresholdMs * 1000L)
        {
            string host = HostLoadText(micros) + ParallelText(map);
            if (diagnostics is { Completed: true } timing)
            {
                _logger.LogWarning(PerformanceLogOptions.PerfEventId,
                    "{What}: map {MapId} instance {InstanceId} took {DurationMs} ms (simulation {SimulationMs} ms, visibility {VisibilityMs} ms, values {ValuesMs} ms, flush {FlushMs} ms, cleanup {CleanupMs} ms; players {Players}, moved {MovedObjects}, changed {ChangedObjects}, new {NewObjects}, visibility candidates {VisibilityCandidates}); top: {Top}; {Breakdown}; {Gc}{Host}",
                    what, map.MapId, map.InstanceId, micros / 1000, timing.SimulationMicros / 1000,
                    timing.VisibilityMicros / 1000, timing.ValuesMicros / 1000, timing.FlushMicros / 1000,
                    timing.CleanupMicros / 1000, timing.Players, timing.MovedObjects, timing.ChangedObjects,
                    timing.NewObjects, timing.VisibilityCandidates, timing.FormatTop(3), timing.Describe(), GcSource().Since(gcBefore).Format(), host);
            }
            else
            {
                _logger.LogWarning(PerformanceLogOptions.PerfEventId, "{What}: map {MapId} instance {InstanceId} took {DurationMs} ms{Host}",
                    what, map.MapId, map.InstanceId, micros / 1000, host);
            }
        }
    }

    /// <summary>
    /// The host-load sampler the slow-update lines ask "was it the machine, the collector or the code" (set by the tick watchdog feature;
    /// null: the lines carry no host text). Read on the world thread only when a line is about to be written.
    /// </summary>
    public ArcaneCore.Kernel.Ops.Watchdog.HostLoadSampler? HostLoad { get; set; }

    private string HostLoadText(long windowMicros)
    {
        if (HostLoad is not { } sampler)
        {
            return string.Empty;
        }

        try
        {
            string text = sampler.Describe(windowMicros); // a fresh probe read (one GetSystemTimes, or /proc/stat on Linux): slow-map lines are rare, and a stored sample
            // usually ends before the slow update, which would report a zero-length window as "0%"
            return text.Length == 0 ? string.Empty : "; " + text;
        }
        catch (Exception ex)
        {
            return $"; host load unavailable ({ex.GetType().Name})";
        }
    }

    /// <summary>Benchmark seam (tests): every map update of the world loop, with its diagnostics (world thread).</summary>
    internal Action<Map, MapUpdateDiagnostics>? MapUpdateObserver { get; set; }

    /// <summary>Benchmark seam (tests): every tick of the world loop: its duration (µs), the world thread's allocation and the phases.</summary>
    internal Action<long, long, TickPhases>? TickObserver { get; set; }

    /// <summary>
    /// Raised on the world thread after every tick of the world loop with the tick body duration (microseconds), the
    /// bytes the world thread allocated during it and its phase split (the metrics feature, docs/ops/metrics.md). A
    /// handler must not block or allocate per call. Not raised by a direct <see cref="RunTick(uint)"/> call.
    /// </summary>
    public event Action<long, long, TickPhases>? TickCompleted;

    /// <summary>Raised on the world thread after each map's update with the map and its update time in microseconds (the metrics feature). Same rules as <see cref="TickCompleted"/>.</summary>
    public event Action<Map, long>? MapUpdated;

    /// <summary>Benchmark seam (tests): every timed world feature of every tick: its name, time (µs) and allocation.</summary>
    internal Action<string, long, long>? FeatureObserver { get; set; }

    /// <summary>
    /// Where the world loop reads the GC counters that it differences around every tick (test seam; default the runtime's
    /// <see cref="GcSnapshot.Read"/>). The delegate is cached, so the per-tick read allocates nothing.
    /// </summary>
    internal Func<GcSnapshot> GcSource { get; set; } = GcSnapshot.Read;

    /// <summary>The phase times of the last <see cref="RunTick"/> (world thread).</summary>
    public TickPhases LastTickPhases { get; private set; }

    private static long Micros(long stopwatchTicks) => stopwatchTicks * 1_000_000 / Stopwatch.Frequency;

    /// <summary>Raise <see cref="Updated"/>, timing each world feature for the tick statistics.</summary>
    private void RaiseTimed(Action<uint>? handlers, uint diffMs)
    {
        if (handlers is null)
        {
            return;
        }

        foreach ((Action<uint> handler, string name) in HandlersOf(ref _updatedHandlers, handlers, TickWorkNames.Of))
        {
            long start = Stopwatch.GetTimestamp();
            long startBytes = GC.GetAllocatedBytesForCurrentThread();
            SetPosition(TickPhase.Features, handler, start);
            try
            {
                handler(diffMs);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogFault("updated-handler", ex, "{Event} handler {Handler} failed for {Subject}", nameof(Updated), name, diffMs);
            }

            long micros = Micros(Stopwatch.GetTimestamp() - start);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;
            Stats.RecordFeature(name, micros, bytes);
            NoteSlowest(name, micros);
            if (FeatureObserver is { } featureObserver)
            {
                try
                {
                    featureObserver(name, micros, bytes);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogFault("tick-observer", ex, "FeatureObserver failed for feature {Feature}", name);
                }
            }
        }
    }

    /// <summary>Raise <see cref="WorldTick"/>, timing each handler as the feature "WorldTick:&lt;Type&gt;" in the tick statistics.</summary>
    private void RaiseWorldTick(uint diffMs)
    {
        Action<uint>? handlers = WorldTick;
        if (handlers is null)
        {
            return;
        }

        foreach ((Action<uint> handler, string name) in HandlersOf(ref _worldTickHandlers, handlers, WorldTickName))
        {
            long start = Stopwatch.GetTimestamp();
            long startBytes = GC.GetAllocatedBytesForCurrentThread();
            SetPosition(TickPhase.WorldTick, handler, start);
            try
            {
                handler(diffMs);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogFault("worldtick", ex, "{Event} handler {Handler} failed for {Subject}", nameof(WorldTick), TickWorkNames.Of(handler), diffMs);
            }

            long micros = Micros(Stopwatch.GetTimestamp() - start);
            Stats.RecordFeature(name, micros, GC.GetAllocatedBytesForCurrentThread() - startBytes);
            NoteSlowest(name, micros);
        }
    }

    /// <summary>The tick-statistics name of a <see cref="WorldTick"/> handler: "WorldTick:" and its class (the method name for a static one).</summary>
    internal static string WorldTickName(Action<uint> handler)
        => handler.Method.DeclaringType is null ? "WorldTick:" + handler.Method.Name : "WorldTick:" + TickWorkNames.Of(handler);

    /// <summary>
    /// The handlers of <paramref name="handlers"/> in invocation order with their names, split once per delegate instance and kept in
    /// <paramref name="table"/> until the event's delegate changes.
    /// </summary>
    private static (Action<uint> Handler, string Name)[] HandlersOf(ref HandlerTable table, Action<uint> handlers, Func<Action<uint>, string> name)
    {
        if (!ReferenceEquals(table.Source, handlers))
        {
            table = new HandlerTable(handlers, [.. handlers.GetInvocationList().Cast<Action<uint>>().Select(h => (h, name(h)))]);
        }

        return table.Handlers;
    }

    private sealed record HandlerTable(Action<uint>? Source, (Action<uint> Handler, string Name)[] Handlers)
    {
        public static readonly HandlerTable None = new(null, []);
    }

    private void Raise<T>(Action<T>? handlers, T subject, string name)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action<T> handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                handler(subject);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                object? description = subject is Player player ? player.Name : subject;
                _logger.LogFault("event-handler", ex, "{Event} handler {Handler} failed for {Subject}", name, TickWorkNames.Of(handler), description);
            }
        }
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
                _logger.LogFault("map-unload", ex, "unloading map {MapId} instance {InstanceId} failed", map.MapId, map.InstanceId);
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
                _logger.LogFault("world-command", ex,
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
                    _logger.LogFault("character-save-snapshot", ex,
                        "failed to snapshot player {Player} ({Guid}) on map {MapId} at ({X:F1},{Y:F1},{Z:F1})",
                        player.Name, player.Guid, player.MapId, player.X, player.Y, player.Z);
                }
            }
        }
    }

    /// <summary>
    /// The periodic save, one character at a time on its own timer (vmangos <c>Player::m_nextSave</c>, counted down from
    /// <c>PlayerSave.Interval</c> in <c>Player::Update</c>): each online character is snapshotted once per <paramref name="interval"/>.
    /// A character's first save comes between half an interval and an interval after the tick that first sees it, at a point fixed
    /// by its id (<see cref="AutosaveStagger"/>), so characters that entered the world together (a server start, a bot fleet) are
    /// spread over the interval instead of all being snapshotted in one tick (vmangos spreads the first save randomly over half an interval
    /// to one and a half; see <see cref="AutosaveStagger"/> for the difference) (a whole-world pass queues one write per online character
    /// at once: about 1000 with the live bot fleet). A character whose previous snapshot is still waiting in the queue is skipped until
    /// its next turn (the queue is behind; another snapshot would only supersede the waiting one and cost a copy).
    /// Unlike vmangos, whose <c>SaveToDB</c> resets <c>m_nextSave</c>, a settlement or other save does not restart the timer.
    /// The timers are only scanned when the earliest one is due or a player came online since the last scan, so most ticks cost
    /// nothing. Returns whether it scanned; <paramref name="queued"/> is the number of snapshots queued.
    /// </summary>
    private bool Autosave(ulong interval, out int queued)
    {
        queued = 0;
        ulong clock = _autosaveClockMs;
        if (!_autosaveRescan && clock < _autosaveNextDue)
        {
            return false;
        }

        _autosaveRescan = false;
        ulong next = ulong.MaxValue;
        uint now = NowMs;
        foreach (KeyValuePair<ObjectGuid, Player> entry in _online) // not .Values: that locks the whole dictionary and copies it
        {
            Player player = entry.Value;
            ref ulong due = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_autosaveDue, player.Guid, out bool seen);
            bool turn = false;
            if (!seen)
            {
                due = clock + interval - AutosaveStagger(player.Guid.Low, interval);
            }
            else if (due <= clock)
            {
                // The next turn is one interval after this one (not after now), so the stagger survives late ticks; a stall longer than
                // an interval skips the turns it missed instead of saving the character again on every tick until it catches up.
                do
                {
                    due += interval;
                }
                while (due <= clock);
                turn = true;
            }

            next = Math.Min(next, due);
            if (!turn)
            {
                continue;
            }

            if (player.IsQuestSettlementPending || _saveQueue.IsPending(unchecked((int)player.Guid.Low)))
            {
                continue;
            }

            try
            {
                _saveQueue.Enqueue(player.CreateSnapshot(now));
                queued++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogFault("character-save-snapshot", ex,
                    "failed to snapshot player {Player} ({Guid}) on map {MapId} at ({X:F1},{Y:F1},{Z:F1})",
                    player.Name, player.Guid, player.MapId, player.X, player.Y, player.Z);
            }
        }

        _autosaveNextDue = next;
        return true;
    }

    /// <summary>
    /// How much earlier than a full interval a character's first autosave comes: a point in [0, interval / 2) fixed by its id
    /// (Fibonacci hashing), so consecutive ids are spread evenly. vmangos staggers too, but randomly and wider: its Player constructor
    /// sets <c>m_nextSave = urand(interval / 2, interval * 3 / 2)</c>; here the window is [interval / 2, interval) and deterministic.
    /// </summary>
    internal static ulong AutosaveStagger(uint characterLow, ulong interval)
        => (unchecked(characterLow * 2654435769u) * (interval / 2)) >> 32;

    private void Run()
    {
        _worldThreadId = Environment.CurrentManagedThreadId;
        int interval = Math.Max(1, Options.TickIntervalMs);
        int tolerance = Math.Clamp(Options.TickLateToleranceMs, 0, 1000);
        Scheduler = new WorldTickScheduler(interval, tolerance);
        Stats.FrameOverrunToleranceMicros = tolerance * 1000L;
        using var waiter = new WorldTickWaiter(Options.TickTimer, _stopSignal, TickSpinMarginMicros);
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
            _logger.LogFault("world-script", ex, "world script {Hook} failed (diff={DiffMs} ms, online={Online}, maps={Maps})",
                nameof(Scripting.IWorldHooks.OnStartup), 0u, _online.Count, _maps.Count);
        }

        while (!_stopSignal.IsSet)
        {
            long startTicks = _clock.ElapsedTicks;
            long tickStart = startTicks * 1000 / Stopwatch.Frequency;
            uint diff = (uint)Math.Clamp(tickStart - last, 0, uint.MaxValue);
            last = tickStart;
            // The frame interval on the precise clock (the diff is whole milliseconds); the manual clock reports its own steps.
            long frameMicros = lastStartTicks < 0 ? 0 : (startTicks - lastStartTicks) * 1_000_000 / Stopwatch.Frequency;
            lastStartTicks = startTicks;
            if (_manualClock)
            {
                diff = NextManualDiff();
                frameMicros = diff * 1000L;
            }

            // vmangos WorldRunnable.cpp:73-74: the frame interval (sleep included), checked before the update.
            if (Options.Perf.SlowWorldUpdateMeasure == SlowWorldUpdateMeasure.FrameInterval
                && Options.Perf.SlowWorldUpdate > 0 && diff > (uint)Options.Perf.SlowWorldUpdate)
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
                    _logger.LogFault("tick-observer", ex, "TickObserver failed (duration={DurationMicros} us)", durationMicros);
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
                    _logger.LogFault("tick-observer", ex, "TickCompleted observer failed (duration={DurationMicros} us)", durationMicros);
                }
            }
            if (Options.Perf.SlowWorldUpdateMeasure == SlowWorldUpdateMeasure.TickDuration
                && Options.Perf.SlowWorldUpdate > 0 && durationMicros > Options.Perf.SlowWorldUpdate * 1000L)
            {
                _logger.LogWarning(PerformanceLogOptions.PerfEventId, "Slow world update: {DurationMs} ms (interval {IntervalMs} ms): {Phases}",
                    durationMicros / 1000, interval, LastTickPhases.Format());
            }

            if (_manualClock)
            {
                SettleManualAdvances(diff);
                if (_manualAdvances.Count > 0)
                {
                    continue; // advances run their ticks back to back
                }
            }

            int wait = Scheduler.NextWait(tickStart, _clock.ElapsedMilliseconds);
            if (wait > 0 && Scheduler.NextDueMs is { } dueMs)
            {
                waiter.WaitUntil(_clock, dueMs * Stopwatch.Frequency / 1000, wait);
            }
        }

        DisposeMapScheduler();
        _logger.LogInformation("World thread stopped");
    }
}
