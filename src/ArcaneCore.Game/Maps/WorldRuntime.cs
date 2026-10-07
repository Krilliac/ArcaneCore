using System.Collections.Concurrent;
using System.Diagnostics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// The authoritative world simulation: one dedicated thread runs a fixed-rate tick that
/// executes posted commands, then updates every map (vmangos World::Update → Map::Update).
/// All in-world state is owned by that thread; other threads interact only through
/// <see cref="Post"/> / <see cref="InvokeAsync{T}"/> and the thread-safe online registry.
/// </summary>
public sealed class WorldRuntime : IDisposable
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
    private uint _sinceAutosaveMs;

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
    }

    public WorldRuntimeOptions Options { get; }

    /// <summary>Optional native player display geometry, applied before <see cref="Map.AddPlayer"/>.</summary>
    public Func<uint, ArcaneCore.Game.Spells.DisplayModelGeometry?>? PlayerDisplayModelResolver { get; set; }

    /// <summary>
    /// Tick duration, allocation and overrun statistics recorded by the world loop (docs/areas/ops-perf.md).
    /// Safe to read from any thread; only <see cref="Run"/> records into it.
    /// </summary>
    public TickStats Stats { get; } = new();

    /// <summary>The world thread's tick schedule (late and skipped starts; docs/integration/playerbot-movement-and-tick-health.md).</summary>
    public WorldTickScheduler Scheduler { get; private set; } = new(50);

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

    /// <summary>True when the caller is the world thread (or no world thread is running).</summary>
    public bool IsWorldThread => _worldThreadId == -1 || Environment.CurrentManagedThreadId == _worldThreadId;

    /// <summary>Whether a character is currently in the world. Thread-safe.</summary>
    public bool IsOnline(ObjectGuid guid) => _online.ContainsKey(guid);

    /// <summary>The online player with this name (case-insensitive), or null (world thread).</summary>
    public Player? FindOnlinePlayer(string name) => _onlineByName.GetValueOrDefault(name);

    /// <summary>The online player with this GUID, or null (world thread).</summary>
    public Player? FindOnlinePlayer(ObjectGuid guid) => _online.GetValueOrDefault(guid);

    /// <summary>Every player in the world (world thread).</summary>
    public IEnumerable<Player> OnlinePlayers => _online.Values;

    /// <summary>Existing maps (world thread); features can install per-map systems at attachment.</summary>
    public IEnumerable<Map> Maps => _maps.Values;

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
    public void NotifyLoggedIn(Player player) => Raise(PlayerLoggedIn, player, nameof(PlayerLoggedIn));

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
        if (!_maps.TryGetValue((mapId, instanceId), out Map? map))
        {
            map = new Map(mapId, instanceId, this, _logger);
            DefaultMapUpdaters.AttachTo(map, this);
            _maps[(mapId, instanceId)] = map;
            Raise(MapCreated, map, nameof(MapCreated));
        }

        return map;
    }

    /// <summary>An existing map instance, or null (vmangos <c>MapManager::FindMap</c>; world thread).</summary>
    public Map? FindMap(uint mapId, uint instanceId = 0) => _maps.GetValueOrDefault((mapId, instanceId));

    /// <summary>
    /// Ask for an instance map to be unloaded after the current map pass (world thread). It is
    /// unloaded only if it is still registered, is not instance 0, has no players and nobody is
    /// in transit from it. Requests wait for transit to finish; occupied or replaced maps drop
    /// their request and the owner asks again later.
    /// </summary>
    public void UnloadMap(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.InstanceId != 0 && !_unloadRequests.Contains(map))
        {
            _unloadRequests.Add(map);
        }
    }

    /// <summary>Put a loaded player into its map and the online registry (world thread).</summary>
    public void AddPlayer(Player player)
    {
        if (!_online.TryAdd(player.Guid, player))
        {
            throw new InvalidOperationException($"{player.Name} ({player.Guid}) is already online");
        }

        _onlineByName[player.Name] = player;
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
        player.Map?.RemovePlayer(player);
        if (!player.IsQuestSettlementPending)
        {
            _saveQueue.Enqueue(player.CreateSnapshot(NowMs));
        }
        // Publish offline only after the final snapshot is queued: login's save barrier
        // must never overtake this old session's last write.
        _onlineByName.TryRemove(new KeyValuePair<string, Player>(player.Name, player));
        _online.TryRemove(new KeyValuePair<ObjectGuid, Player>(player.Guid, player));
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
    public void RunTick(uint diffMs)
    {
        if (_manualClock)
        {
            Interlocked.Add(ref _manualNowMs, diffMs);
        }

        long phaseStart = Stopwatch.GetTimestamp();
        RunCommands();
        long commandsEnd = Stopwatch.GetTimestamp();
        // World-level services (game events, rest, the tick watchdog) run before the maps. Their time counts as world
        // features, but untimed per handler: the watchdog's handler must stay a few stores at the start of the tick.
        Raise(WorldTick, diffMs, nameof(WorldTick));
        long worldTickEnd = Stopwatch.GetTimestamp();

        // A snapshot: a map system may create another map (an instance) during its update.
        foreach (Map map in _maps.Values.ToArray())
        {
            long mapStart = Stopwatch.GetTimestamp();
            MapUpdateDiagnostics? diagnostics = Options.Perf.SlowMapUpdate > 0 && _logger.IsEnabled(LogLevel.Warning)
                ? new MapUpdateDiagnostics()
                : null;
            try
            {
                map.Update(diffMs, diagnostics);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "map {MapId} update failed", map.MapId);
            }

            LogIfSlow(Options.Perf.SlowMapUpdate, mapStart, "Slow map update", map, diagnostics);
        }

        UnloadRequestedMaps();
        long mapsEnd = Stopwatch.GetTimestamp();
        RaiseTimed(Updated, diffMs);
        long featuresEnd = Stopwatch.GetTimestamp();
        LastTickPhases = new TickPhases(Micros(commandsEnd - phaseStart), Micros(mapsEnd - worldTickEnd),
            Micros((worldTickEnd - commandsEnd) + (featuresEnd - mapsEnd)));

        if (Options.AutosaveIntervalMs > 0)
        {
            _sinceAutosaveMs += diffMs;
            if (_sinceAutosaveMs >= Options.AutosaveIntervalMs)
            {
                _sinceAutosaveMs = 0;
                SaveAll();
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _stopSignal.Dispose();
    }

    private void LogIfSlow(int thresholdMs, long startTimestamp, string what, Map map, MapUpdateDiagnostics? diagnostics)
    {
        if (thresholdMs <= 0)
        {
            return;
        }

        long micros = (Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000 / Stopwatch.Frequency;
        if (micros > thresholdMs * 1000L)
        {
            if (diagnostics is { Completed: true } timing)
            {
                _logger.LogWarning(PerformanceLogOptions.PerfEventId,
                    "{What}: map {MapId} instance {InstanceId} took {DurationMs} ms (simulation {SimulationMs} ms, visibility {VisibilityMs} ms, values {ValuesMs} ms, flush {FlushMs} ms, cleanup {CleanupMs} ms; players {Players}, moved {MovedObjects}, changed {ChangedObjects}, new {NewObjects})",
                    what, map.MapId, map.InstanceId, micros / 1000, timing.SimulationMicros / 1000,
                    timing.VisibilityMicros / 1000, timing.ValuesMicros / 1000, timing.FlushMicros / 1000,
                    timing.CleanupMicros / 1000, timing.Players, timing.MovedObjects, timing.ChangedObjects,
                    timing.NewObjects);
            }
            else
            {
                _logger.LogWarning(PerformanceLogOptions.PerfEventId, "{What}: map {MapId} instance {InstanceId} took {DurationMs} ms",
                    what, map.MapId, map.InstanceId, micros / 1000);
            }
        }
    }

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

        foreach (Action<uint> handler in handlers.GetInvocationList().Cast<Action<uint>>())
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                handler(diffMs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Event} handler failed for {Subject}", nameof(Updated), diffMs);
            }

            Stats.RecordFeature(handler.Method.DeclaringType?.Name ?? handler.Method.Name, Micros(Stopwatch.GetTimestamp() - start));
        }
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
            catch (Exception ex)
            {
                object? description = subject is Player player ? player.Name : subject;
                _logger.LogError(ex, "{Event} handler failed for {Subject}", name, description);
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
                _logger.LogError(ex, "unloading map {MapId} instance {InstanceId} failed", map.MapId, map.InstanceId);
            }
        }
    }

    private void RunCommands(bool drainAll = false)
    {
        int admitted = 0;
        long started = Stopwatch.GetTimestamp();
        int maxCommands = Math.Max(1, Options.MaxCommandsPerTick);
        long budgetTicks = Options.CommandTimeBudgetMs > 0
            ? Options.CommandTimeBudgetMs * Stopwatch.Frequency / 1000L
            : 0;
        while (_commands.TryDequeue(out Action? command))
        {
            try
            {
                command();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "world command failed");
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
    }

    /// <summary>Queue every online character for saving (world thread, or after <see cref="Stop"/>).</summary>
    public void SaveAll()
    {
        uint now = NowMs;
        foreach (Player player in _online.Values)
        {
            if (!player.IsQuestSettlementPending)
            {
                _saveQueue.Enqueue(player.CreateSnapshot(now));
            }
        }
    }

    private void Run()
    {
        _worldThreadId = Environment.CurrentManagedThreadId;
        int interval = Math.Max(1, Options.TickIntervalMs);
        Scheduler = new WorldTickScheduler(interval);
        long last = _clock.ElapsedMilliseconds;
        _logger.LogInformation("World thread started ({Interval} ms tick)", interval);

        while (!_stopSignal.IsSet)
        {
            long tickStart = _clock.ElapsedMilliseconds;
            uint diff = (uint)Math.Clamp(tickStart - last, 0, uint.MaxValue);
            last = tickStart;
            if (_manualClock)
            {
                diff = NextManualDiff();
            }

            // vmangos WorldRunnable.cpp:73-74: the frame interval (sleep included), checked before the update.
            if (Options.Perf.SlowWorldUpdateMeasure == SlowWorldUpdateMeasure.FrameInterval
                && Options.Perf.SlowWorldUpdate > 0 && diff > (uint)Options.Perf.SlowWorldUpdate)
            {
                _logger.LogWarning(PerformanceLogOptions.PerfEventId, "Slow world update: {DurationMs}ms", diff);
            }

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long stampBefore = Stopwatch.GetTimestamp();
            RunTick(diff);
            long durationMicros = (Stopwatch.GetTimestamp() - stampBefore) * 1_000_000 / Stopwatch.Frequency;
            Stats.Record(durationMicros, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
                interval * 1000L, diff * 1000L, LastTickPhases);
            if (Options.Perf.SlowWorldUpdateMeasure == SlowWorldUpdateMeasure.TickDuration
                && Options.Perf.SlowWorldUpdate > 0 && durationMicros > Options.Perf.SlowWorldUpdate * 1000L)
            {
                _logger.LogWarning(PerformanceLogOptions.PerfEventId, "Slow world update: {DurationMs} ms (interval {IntervalMs} ms)",
                    durationMicros / 1000, interval);
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
            if (wait > 0)
            {
                _stopSignal.Wait(wait);
            }
        }

        _logger.LogInformation("World thread stopped");
    }
}
