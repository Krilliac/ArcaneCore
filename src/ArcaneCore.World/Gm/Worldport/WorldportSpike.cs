using System.Diagnostics;
using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Gm.Worldport;

/// <summary>Which experiment a <see cref="WorldportTrace"/> records.</summary>
public enum WorldportSpikeMode
{
    /// <summary>A far teleport to the player's own map: SMSG_TRANSFER_PENDING and SMSG_NEW_WORLD with the same map id, then MSG_MOVE_WORLDPORT_ACK.</summary>
    FarSameMap,

    /// <summary>The player is taken out of its map and put back at once: the login packets and create blocks again, no transfer packets.</summary>
    ReAddInPlace,
}

/// <summary>One step of a spike run: what happened, how long after the request (wall clock), and the detail.</summary>
public sealed record WorldportStep(string Name, double ElapsedMs, string Detail);

/// <summary>One run of the same-map world-port spike, step by step. Mutated on the world thread only.</summary>
public sealed class WorldportTrace
{
    private readonly List<WorldportStep> _steps = [];
    private readonly long _started = Stopwatch.GetTimestamp();

    internal WorldportTrace(int id, WorldportSpikeMode mode, Player player, uint mapId, float x, float y, float z, float orientation)
    {
        Id = id;
        Mode = mode;
        PlayerName = player.Name;
        PlayerGuid = player.Guid;
        MapId = mapId;
        X = x;
        Y = y;
        Z = z;
        Orientation = orientation;
        StartedUtc = DateTimeOffset.UtcNow;
    }

    public int Id { get; }

    public WorldportSpikeMode Mode { get; }

    public string PlayerName { get; }

    public ObjectGuid PlayerGuid { get; }

    /// <summary>The map the run starts and must end on.</summary>
    public uint MapId { get; }

    public float X { get; }

    public float Y { get; }

    public float Z { get; }

    public float Orientation { get; }

    public DateTimeOffset StartedUtc { get; }

    public IReadOnlyList<WorldportStep> Steps => _steps;

    /// <summary>Whether the player was put back into a map (on <see cref="MapId"/> or not: see <see cref="Outcome"/>).</summary>
    public bool ReAdded { get; internal set; }

    /// <summary>Null while the run is open; then <c>ok</c> or <c>failed: ...</c>.</summary>
    public string? Outcome { get; internal set; }

    public bool Finished => Outcome is not null;

    /// <summary>Whether the ack timeout was reported (once per run).</summary>
    internal bool TimeoutReported { get; set; }

    /// <summary>Wall-clock milliseconds since the request.</summary>
    public double ElapsedMs => Stopwatch.GetElapsedTime(_started).TotalMilliseconds;

    /// <summary>When the observation window after the re-add ends (wall-clock ms since the request), or null while not re-added.</summary>
    internal double? ObserveUntilMs { get; set; }

    internal WorldportStep Add(string name, string detail)
    {
        var step = new WorldportStep(name, ElapsedMs, detail);
        _steps.Add(step);
        return step;
    }

    /// <summary>One line per step (the <c>.worldport status</c> text).</summary>
    public string Format()
    {
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"#{Id} {ModeName(Mode)} {PlayerName} map {MapId} ({X:F1}, {Y:F1}, {Z:F1}) at {StartedUtc:HH:mm:ss}: {Outcome ?? "in progress"}"),
        };
        lines.AddRange(_steps.Select(s => string.Create(CultureInfo.InvariantCulture, $"  +{s.ElapsedMs:F0} ms {s.Name}: {s.Detail}")));
        return string.Join('\n', lines);
    }

    internal static string ModeName(WorldportSpikeMode mode) => mode == WorldportSpikeMode.FarSameMap ? "far" : "readd";
}

/// <summary>
/// Tier 3 spike (docs/design/hot-reload-tiers.md, section 5.5): does a 1.12.1 client accept SMSG_NEW_WORLD to the map it is already
/// on? vmangos never sends that (a same-map teleport is always a near one), and phase 3b's handoff needs the answer. Two experiments,
/// each recorded step by step with wall-clock timings in the server log (category <c>ArcaneCore.World.Gm.Worldport</c>, every line
/// starts with <see cref="LogPrefix"/>) and in <see cref="Recent"/>:
/// <list type="bullet">
/// <item><see cref="StartFar"/>: the existing far-teleport path of <see cref="TeleportService"/> with <see cref="TeleportOptions.ForceFar"/>,
/// so SMSG_TRANSFER_PENDING and SMSG_NEW_WORLD name the same map; the client's MSG_MOVE_WORLDPORT_ACK puts the player back with the
/// login packets, exactly as after a cross-map teleport.</item>
/// <item><see cref="StartReAdd"/>: the player is removed from its map and added again at once, with the login packets around the
/// add, and no transfer packet at all (the "in place" variant 3b would need if the client refuses the first).</item>
/// </list>
/// What the server can see of a client's reaction is recorded too: the worldport ack (or its absence within <see cref="AckTimeout"/>),
/// the player leaving the world (a client that crashes or disconnects), and every <c>teleport</c> or <c>session</c> anomaly reported
/// about the player while the run is open. Development only: the commands exist only with <c>World:HotCode:Enabled</c>
/// (<see cref="WorldportSpikeServiceModule"/>). Thread affinity: world thread, except the anomaly listener, which only posts.
/// </summary>
public sealed class WorldportSpike : IDisposable
{
    /// <summary>The first word of every log line the spike writes.</summary>
    public const string LogPrefix = "worldport-spike";

    /// <summary>How many finished runs <see cref="Recent"/> keeps.</summary>
    public const int RecentCapacity = 16;

    private readonly WorldRuntime _world;
    private readonly Func<TeleportFeature> _teleports;
    private readonly ILogger _logger;
    private readonly Dictionary<ObjectGuid, WorldportTrace> _active = [];
    private readonly List<WorldportTrace> _recent = [];
    private TeleportService? _subscribed;
    private int _nextId;
    private int _activeCount;
    private bool _disposed;

    /// <param name="world">The world whose players are moved.</param>
    /// <param name="teleports">The teleport feature (resolved on first use: it is attached after the command host starts).</param>
    /// <param name="logger">Where the steps are written.</param>
    /// <param name="ackTimeout">How long a far run waits for MSG_MOVE_WORLDPORT_ACK before it reports a timeout (default 60 s).</param>
    /// <param name="observeWindow">How long a run stays open after the player is back in its map, to catch a client that drops then (default 15 s).</param>
    public WorldportSpike(WorldRuntime world, Func<TeleportFeature> teleports, ILogger? logger = null, TimeSpan? ackTimeout = null, TimeSpan? observeWindow = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(teleports);
        _world = world;
        _teleports = teleports;
        _logger = logger ?? NullLogger.Instance;
        AckTimeout = ackTimeout ?? TimeSpan.FromSeconds(60);
        ObserveWindow = observeWindow ?? TimeSpan.FromSeconds(15);
    }

    public TimeSpan AckTimeout { get; }

    public TimeSpan ObserveWindow { get; }

    /// <summary>The open runs first (oldest first), then the finished ones, newest last. World thread.</summary>
    public IReadOnlyList<WorldportTrace> Recent => [.. _active.Values.OrderBy(t => t.Id), .. _recent];

    /// <summary>The open run of <paramref name="player"/>, if any. World thread.</summary>
    public WorldportTrace? ActiveFor(Player player) => _active.GetValueOrDefault(player.Guid);

    /// <summary>
    /// Start a far teleport of <paramref name="player"/> to its own map at the given position. Returns the run, or null with
    /// <paramref name="refusal"/> when the player cannot take part (no map, a teleport or run already in progress, aboard a ship, on a
    /// battleground, no client) or <see cref="TeleportService.TeleportTo(Player, uint, float, float, float, float, TeleportOptions)"/>
    /// refused it (the run is then recorded as failed). World thread.
    /// </summary>
    public WorldportTrace? StartFar(Player player, float x, float y, float z, float orientation, string invoker, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(player);
        TeleportFeature feature = EnsureSubscribed();
        if (Refuse(player, feature, out refusal) is not { } map)
        {
            return null;
        }

        WorldportTrace trace = Open(WorldportSpikeMode.FarSameMap, player, map.MapId, x, y, z, orientation);
        Record(trace, "requested", string.Create(CultureInfo.InvariantCulture,
            $"by {invoker}; from ({player.X:F1}, {player.Y:F1}, {player.Z:F1}) to ({x:F1}, {y:F1}, {z:F1}, o {orientation:F2}) on map {map.MapId}"));
        if (!feature.Teleports.TeleportTo(player, map.MapId, x, y, z, orientation, TeleportOptions.ForceFar))
        {
            Finish(trace, "failed: the teleport service refused the destination (invalid coordinates or entry refused)");
            refusal = trace.Outcome;
            return trace;
        }

        Record(trace, "far-scheduled", "the transfer runs after this map's update (vmangos ScheduleFarTeleport)");
        return trace;
    }

    /// <summary>
    /// Take <paramref name="player"/> out of its map and put it back where it stands at the start of the next tick, with the login
    /// packets before and after the add and no transfer packet. Returns the run, or null with <paramref name="refusal"/>. World thread.
    /// </summary>
    public WorldportTrace? StartReAdd(Player player, string invoker, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(player);
        TeleportFeature feature = EnsureSubscribed();
        if (Refuse(player, feature, out refusal) is not { } map)
        {
            return null;
        }

        WorldportTrace trace = Open(WorldportSpikeMode.ReAddInPlace, player, map.MapId, player.X, player.Y, player.Z, player.Orientation);
        Record(trace, "requested", string.Create(CultureInfo.InvariantCulture,
            $"by {invoker}; re-add in place at ({player.X:F1}, {player.Y:F1}, {player.Z:F1}) on map {map.MapId}, at the start of the next tick"));
        _world.Post(() => ReAdd(player, map, trace));
        return trace;
    }

    private Map? Refuse(Player player, TeleportFeature feature, out string? refusal)
    {
        refusal = null;
        if (player.Map is not { } map)
        {
            refusal = $"{player.Name} is not in a map.";
        }
        else if (_active.ContainsKey(player.Guid))
        {
            refusal = $"{player.Name} already has a spike run in progress.";
        }
        else if (feature.Teleports.IsBeingTeleported(player))
        {
            refusal = $"{player.Name} is being teleported.";
        }
        else if (player.Transport is not null)
        {
            refusal = $"{player.Name} is aboard a ship; the spike runs on solid ground only.";
        }
        else if (feature.Maps.Registry.Find(map.MapId) is { IsBattleground: true })
        {
            refusal = $"{player.Name} is on a battleground map; the spike does not run there.";
        }
        else if (!player.Session.WantsUpdateObjects)
        {
            refusal = $"{player.Name} has no client to answer (a lean bot).";
        }
        else
        {
            return map;
        }

        return null;
    }

    private void ReAdd(Player player, Map map, WorldportTrace trace)
    {
        if (trace.Finished)
        {
            return;
        }

        if (!ReferenceEquals(_world.FindOnlinePlayer(player.Guid), player) || !ReferenceEquals(player.Map, map) || _teleports().Teleports.IsBeingTeleported(player))
        {
            Finish(trace, "failed: the player left, changed map or started a teleport before the re-add ran");
            return;
        }

        bool removed = false;
        try
        {
            map.RemovePlayer(player);
            removed = true;
            Record(trace, "removed", "taken out of the map; observers got SMSG_DESTROY_OBJECT, the client got nothing");
            if (player.Session is WorldSession session)
            {
                LoginSequence.SendInitialPacketsBeforeAddToMap(session, player, LoginPackets.BuildTutorialFlags(session.Settings.Tutorials));
            }

            map.AddPlayer(player);
            if (player.Session is WorldSession after)
            {
                LoginSequence.SendInitialPacketsAfterAddToMap(after, player);
            }

            Arrived(trace, player, "re-added in place with the login packets and the self create block; the visible set is sent again");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "{Prefix} #{Id} {Player}: the re-add threw", LogPrefix, trace.Id, trace.PlayerName);
            if (removed && player.Map is null)
            {
                try
                {
                    map.AddPlayer(player);
                }
                catch (Exception again) when (again is not OutOfMemoryException)
                {
                    _logger.LogError(again, "{Prefix} #{Id} {Player}: could not put the player back; kicking the session", LogPrefix, trace.Id, trace.PlayerName);
                    player.Session.Kick();
                }
            }

            Finish(trace, "failed: the re-add threw " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private TeleportFeature EnsureSubscribed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TeleportFeature feature = _teleports();
        if (_subscribed is null)
        {
            _subscribed = feature.Teleports;
            _subscribed.FarStageReached += OnFarStage;
            _subscribed.TeleportCompleted += OnTeleportCompleted;
            _world.PlayerLoggingOut += OnLoggingOut;
            _world.WorldTick += OnTick;
            AnomalyLedger.Shared.Reported += OnAnomaly;
        }

        return feature;
    }

    private WorldportTrace Open(WorldportSpikeMode mode, Player player, uint mapId, float x, float y, float z, float orientation)
    {
        var trace = new WorldportTrace(++_nextId, mode, player, mapId, x, y, z, orientation);
        _active[player.Guid] = trace;
        Interlocked.Increment(ref _activeCount);
        return trace;
    }

    private void OnFarStage(Player player, TeleportStage stage)
    {
        if (!_active.TryGetValue(player.Guid, out WorldportTrace? trace) || trace.Mode != WorldportSpikeMode.FarSameMap)
        {
            return;
        }

        if (stage == TeleportStage.Far)
        {
            Record(trace, "transfer-sent", string.Create(CultureInfo.InvariantCulture,
                $"SMSG_TRANSFER_PENDING (map {trace.MapId}) then SMSG_NEW_WORLD (map {trace.MapId}, {trace.X:F1}, {trace.Y:F1}, {trace.Z:F1}, {trace.Orientation:F2}); the player is in no map, waiting for MSG_MOVE_WORLDPORT_ACK"));
        }
        else if (stage == TeleportStage.Arriving)
        {
            Record(trace, "worldport-ack", "MSG_MOVE_WORLDPORT_ACK received; the player enters the map at the start of the next tick");
        }
    }

    private void OnTeleportCompleted(Player player)
    {
        if (_active.TryGetValue(player.Guid, out WorldportTrace? trace) && trace.Mode == WorldportSpikeMode.FarSameMap && !trace.ReAdded)
        {
            Arrived(trace, player, "re-added with the login packets (SendInitialPacketsBeforeAddToMap, the map add, SendInitialPacketsAfterAddToMap)");
        }
    }

    private void Arrived(WorldportTrace trace, Player player, string what)
    {
        trace.ReAdded = true;
        uint? mapId = player.Map?.MapId;
        string where = string.Create(CultureInfo.InvariantCulture,
            $"{what}; now on map {(mapId is { } m ? m.ToString(CultureInfo.InvariantCulture) : "none")} at ({player.X:F1}, {player.Y:F1}, {player.Z:F1})");
        Record(trace, "re-added", where);
        if (mapId != trace.MapId)
        {
            Finish(trace, string.Create(CultureInfo.InvariantCulture, $"failed: the player ended on map {(mapId is { } n ? n.ToString(CultureInfo.InvariantCulture) : "none")}, not {trace.MapId}"));
            return;
        }

        trace.ObserveUntilMs = trace.ElapsedMs + ObserveWindow.TotalMilliseconds;
    }

    private void OnLoggingOut(Player player)
    {
        if (_active.TryGetValue(player.Guid, out WorldportTrace? trace))
        {
            Record(trace, "client-left", "the player left the world (logout, disconnect or a client crash) while the run was open");
            Finish(trace, trace.ReAdded ? "failed: the client left the world after the re-add" : "failed: the client left the world before the re-add");
        }
    }

    private void OnTick(uint diffMs)
    {
        if (_active.Count == 0)
        {
            return;
        }

        foreach (WorldportTrace trace in _active.Values.ToArray())
        {
            double elapsed = trace.ElapsedMs;
            if (trace.ObserveUntilMs is { } until)
            {
                if (elapsed >= until)
                {
                    Record(trace, "observed", string.Create(CultureInfo.InvariantCulture,
                        $"still in the world {ObserveWindow.TotalSeconds:F0} s after the re-add, no client-left event"));
                    Finish(trace, "ok");
                }

                continue;
            }

            if (!trace.TimeoutReported && elapsed >= AckTimeout.TotalMilliseconds)
            {
                trace.TimeoutReported = true;
                Player? player = _world.FindOnlinePlayer(trace.PlayerGuid);
                TeleportStage? stage = player is null ? null : _teleports().Teleports.StageOf(player);
                Record(trace, "ack-timeout", string.Create(CultureInfo.InvariantCulture,
                    $"not back in the world after {AckTimeout.TotalSeconds:F0} s (teleport stage {(stage?.ToString() ?? "none")}); the client never acknowledged or the entry failed. The run stays open for a late ack."));
            }

            if (elapsed >= AckTimeout.TotalMilliseconds * 10)
            {
                Finish(trace, "failed: never re-added (abandoned)");
            }
        }
    }

    private void OnAnomaly(AnomalyNotice notice)
    {
        if (Volatile.Read(ref _activeCount) == 0 || notice.Subsystem is not (ServerAnomalies.Teleport or ServerAnomalies.Session))
        {
            return;
        }

        _world.Post(() =>
        {
            foreach (WorldportTrace trace in _active.Values)
            {
                if (string.Equals(notice.Subject, SubjectOf(trace), StringComparison.Ordinal))
                {
                    Record(trace, "anomaly", $"{notice.Subsystem}/{notice.Code} ({notice.Severity}): {notice.Detail}");
                }
            }
        });
    }

    private static string SubjectOf(WorldportTrace trace)
        => ServerAnomalies.SubjectOf(trace.PlayerName, trace.PlayerGuid);

    private void Record(WorldportTrace trace, string name, string detail)
    {
        WorldportStep step = trace.Add(name, detail);
        _logger.LogInformation(
            "{Prefix} #{Id} {Mode} {Player} +{ElapsedMs:F0} ms {Step}: {Detail}",
            LogPrefix, trace.Id, WorldportTrace.ModeName(trace.Mode), trace.PlayerName, step.ElapsedMs, step.Name, step.Detail);
    }

    private void Finish(WorldportTrace trace, string outcome)
    {
        if (trace.Finished)
        {
            return;
        }

        trace.Outcome = outcome;
        if (_active.TryGetValue(trace.PlayerGuid, out WorldportTrace? open) && ReferenceEquals(open, trace))
        {
            _active.Remove(trace.PlayerGuid);
            Interlocked.Decrement(ref _activeCount);
        }

        _recent.Add(trace);
        if (_recent.Count > RecentCapacity)
        {
            _recent.RemoveAt(0);
        }

        LogLevel level = outcome == "ok" ? LogLevel.Information : LogLevel.Warning;
        _logger.Log(level, "{Prefix} #{Id} {Mode} {Player} finished after {ElapsedMs:F0} ms: {Outcome}",
            LogPrefix, trace.Id, WorldportTrace.ModeName(trace.Mode), trace.PlayerName, trace.ElapsedMs, outcome);
    }

    /// <summary>Stop listening. World thread (or after the world stopped).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_subscribed is not null)
        {
            _subscribed.FarStageReached -= OnFarStage;
            _subscribed.TeleportCompleted -= OnTeleportCompleted;
            _world.PlayerLoggingOut -= OnLoggingOut;
            _world.WorldTick -= OnTick;
            AnomalyLedger.Shared.Reported -= OnAnomaly;
            _subscribed = null;
        }
    }
}
