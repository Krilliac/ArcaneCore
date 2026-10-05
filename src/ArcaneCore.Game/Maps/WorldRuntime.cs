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
    private readonly Dictionary<(uint MapId, uint InstanceId), Map> _maps = [];
    private readonly List<Map> _unloadRequests = [];
    private readonly ConcurrentDictionary<ObjectGuid, Player> _online = new();
    private readonly ConcurrentDictionary<string, Player> _onlineByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ManualResetEventSlim _stopSignal = new(false);
    private Thread? _thread;
    private int _worldThreadId = -1;
    private uint _sinceAutosaveMs;

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

    /// <summary>
    /// Milliseconds since the world started, wrapping like vmangos WorldTimer::getMSTime.
    /// This is the clock movement timestamps and create blocks carry.
    /// </summary>
    public uint NowMs => unchecked((uint)_clock.ElapsedMilliseconds);

    /// <summary>Time since the world was created (does not wrap, unlike <see cref="NowMs"/>).</summary>
    public TimeSpan Uptime => _clock.Elapsed;

    public int OnlinePlayerCount => _online.Count;

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

        _thread = new Thread(Run) { IsBackground = true, Name = "world" };
        _thread.Start();
    }

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
        RunCommands();
        SaveAll();
    }

    /// <summary>Queue work for the start of the next tick. Thread-safe.</summary>
    public void Post(Action command) => _commands.Enqueue(command);

    /// <summary>Run <paramref name="func"/> on the world thread and return its result.</summary>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                completion.SetResult(func());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
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
        RunCommands();

        // A snapshot: a map system may create another map (an instance) during its update.
        foreach (Map map in _maps.Values.ToArray())
        {
            long mapStart = Stopwatch.GetTimestamp();
            try
            {
                map.Update(diffMs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "map {MapId} update failed", map.MapId);
            }

            LogIfSlow(Options.Perf.SlowMapUpdate, mapStart, "Slow map update", map);
        }

        UnloadRequestedMaps();
        Raise(Updated, diffMs, nameof(Updated));

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

    private void LogIfSlow(int thresholdMs, long startTimestamp, string what, Map map)
    {
        if (thresholdMs <= 0)
        {
            return;
        }

        long micros = (Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000 / Stopwatch.Frequency;
        if (micros > thresholdMs * 1000L)
        {
            _logger.LogWarning(PerformanceLogOptions.PerfEventId, "{What}: map {MapId} instance {InstanceId} took {DurationMs} ms",
                what, map.MapId, map.InstanceId, micros / 1000);
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

    private void RunCommands()
    {
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
        long last = _clock.ElapsedMilliseconds;
        _logger.LogInformation("World thread started ({Interval} ms tick)", interval);

        while (!_stopSignal.IsSet)
        {
            long tickStart = _clock.ElapsedMilliseconds;
            uint diff = (uint)Math.Clamp(tickStart - last, 0, uint.MaxValue);
            last = tickStart;

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
            Stats.Record(durationMicros, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore, interval * 1000L);
            if (Options.Perf.SlowWorldUpdateMeasure == SlowWorldUpdateMeasure.TickDuration
                && Options.Perf.SlowWorldUpdate > 0 && durationMicros > Options.Perf.SlowWorldUpdate * 1000L)
            {
                _logger.LogWarning(PerformanceLogOptions.PerfEventId, "Slow world update: {DurationMs} ms (interval {IntervalMs} ms)",
                    durationMicros / 1000, interval);
            }

            long elapsed = _clock.ElapsedMilliseconds - tickStart;
            if (elapsed < interval)
            {
                _stopSignal.Wait((int)(interval - elapsed));
            }
        }

        _logger.LogInformation("World thread stopped");
    }
}
