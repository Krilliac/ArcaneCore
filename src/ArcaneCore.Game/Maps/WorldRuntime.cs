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
    private readonly Dictionary<uint, Map> _maps = [];
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
    public void SavePlayer(Player player) => _saveQueue.Enqueue(player.CreateSnapshot(NowMs));

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
    /// The map with the given id, created on first use with its default per-map systems
    /// (<see cref="DefaultMapUpdaters"/>; world thread).
    /// </summary>
    public Map GetMap(uint mapId)
    {
        if (!_maps.TryGetValue(mapId, out Map? map))
        {
            map = new Map(mapId, this, _logger);
            DefaultMapUpdaters.AttachTo(map, this);
            _maps[mapId] = map;
            Raise(MapCreated, map, nameof(MapCreated));
        }

        return map;
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
        GetMap(player.MapId).AddPlayer(player);
    }

    /// <summary>Take a player out of the world and queue its state for saving (world thread).</summary>
    public void RemovePlayer(Player player)
    {
        if (!_online.ContainsKey(player.Guid))
        {
            return;
        }

        Raise(PlayerLoggingOut, player, nameof(PlayerLoggingOut));
        _online.TryRemove(player.Guid, out _);
        _onlineByName.TryRemove(new KeyValuePair<string, Player>(player.Name, player));
        player.Map?.RemovePlayer(player);
        _saveQueue.Enqueue(player.CreateSnapshot(NowMs));
    }

    /// <summary>
    /// Complete a logout (world thread): save and remove the player, then return its client to
    /// the character screen (vmangos WorldSession::LogoutPlayer → SMSG_LOGOUT_COMPLETE).
    /// </summary>
    public void LogoutPlayer(Player player)
    {
        if (!_online.ContainsKey(player.Guid))
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

        foreach (Map map in _maps.Values)
        {
            try
            {
                map.Update(diffMs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "map {MapId} update failed", map.MapId);
            }
        }

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
            _saveQueue.Enqueue(player.CreateSnapshot(now));
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

            RunTick(diff);

            long elapsed = _clock.ElapsedMilliseconds - tickStart;
            if (elapsed < interval)
            {
                _stopSignal.Wait((int)(interval - elapsed));
            }
        }

        _logger.LogInformation("World thread stopped");
    }
}
