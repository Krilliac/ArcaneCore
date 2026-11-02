using ArcaneCore.Game.Maps;

namespace ArcaneCore.Gateway;

/// <summary>One worker's row of the <see cref="GatewayRouteTable"/>.</summary>
/// <param name="NodeId">The worker's node id (Gateway:Workers:N:NodeId).</param>
/// <param name="Ready">Whether its link is registered (a down worker owns nothing for routing purposes).</param>
/// <param name="FromAnnouncement">True when the maps come from the worker's own announcement; false when they come from the static <c>Gateway:Workers:N:Maps</c> fallback.</param>
/// <param name="Maps">The ownership text (<c>0,1,30-40</c>, or <c>all maps</c>), with <c> + instances</c> when instance copies of other maps also run there.</param>
public sealed record GatewayRouteEntry(string NodeId, bool Ready, bool FromAnnouncement, string Maps);

/// <summary>
/// The gateway's map-to-worker table. Each worker announces the maps it owns (<c>Cluster:Worker:OwnedMaps</c>) in its link handshake; the table keeps that
/// list per worker while the link is up, and falls back to the static <c>Gateway:Workers:N:Maps</c> list when the worker announced none (an announcement
/// that lists maps wins; an empty one defers to the config; with neither, the worker owns every map). A worker whose link is down owns nothing for routing:
/// <see cref="LinkDown"/> drops its announcement and <see cref="TryResolve"/> skips it.
/// <para>
/// New sessions open on the designated login worker (<see cref="TryGetLoginWorker"/>, <c>Gateway:LoginWorker</c>); the transfer router resolves a map
/// copy to its worker with <see cref="TryResolve"/>. Thread safety: every member locks one gate; writers are the link supervisors (one task per link),
/// readers are client connections and metrics. Nothing is allocated on <see cref="TryResolve"/> or <see cref="TryGetLoginWorker"/>.
/// </para>
/// </summary>
public sealed class GatewayRouteTable
{
    private sealed class Entry(string nodeId, MapOwnership configured)
    {
        public string NodeId { get; } = nodeId;

        public MapOwnership Configured { get; } = configured;

        public MapOwnership? Announced { get; set; }

        public bool Ready { get; set; }

        public MapOwnership Effective => Announced ?? Configured;
    }

    private readonly object _sync = new();
    private readonly Entry[] _entries;
    private readonly string? _loginWorker;

    /// <param name="workers">The configured worker entries, in configuration order (the order breaks ties).</param>
    /// <param name="loginWorker">Node id of the designated login worker, or null/blank for none (sessions then spread over the ready links).</param>
    /// <exception cref="ArgumentException"><paramref name="loginWorker"/> names no entry.</exception>
    public GatewayRouteTable(IReadOnlyList<GatewayWorkerEntry> workers, string? loginWorker = null)
    {
        ArgumentNullException.ThrowIfNull(workers);
        _entries = new Entry[workers.Count];
        for (int index = 0; index < workers.Count; index++)
        {
            GatewayWorkerEntry worker = workers[index];
            _entries[index] = new Entry(worker.NodeId, ConfiguredOwnership(worker.Maps));
        }

        if (!string.IsNullOrWhiteSpace(loginWorker))
        {
            if (FindEntry(loginWorker) is null)
            {
                throw new ArgumentException($"Gateway:LoginWorker '{loginWorker}' names no worker entry.", nameof(loginWorker));
            }

            _loginWorker = loginWorker;
        }
    }

    /// <summary>The designated login worker's node id, or null when none is configured.</summary>
    public string? ConfiguredLoginWorker => _loginWorker;

    /// <summary>
    /// Records that <paramref name="nodeId"/>'s link registered and which maps it announced (<paramref name="ownedMaps"/> is the <c>0,1,30-40</c> text of
    /// its handshake; blank or malformed means no announcement, so the static list applies). False when no entry has that node id.
    /// </summary>
    public bool LinkUp(string nodeId, string? ownedMaps, bool allowInstanceMaps)
    {
        MapOwnership? announced = null;
        if (MapOwnership.TryParse(ownedMaps, allowInstanceMaps, out MapOwnership parsed, out _) && !parsed.OwnsEverything)
        {
            announced = parsed;
        }

        lock (_sync)
        {
            Entry? entry = FindEntry(nodeId);
            if (entry is null)
            {
                return false;
            }

            entry.Announced = announced;
            entry.Ready = true;
            return true;
        }
    }

    /// <summary>Records that <paramref name="nodeId"/>'s link is gone: the announcement is dropped and the worker no longer resolves. False when no entry has that node id.</summary>
    public bool LinkDown(string nodeId)
    {
        lock (_sync)
        {
            Entry? entry = FindEntry(nodeId);
            if (entry is null)
            {
                return false;
            }

            entry.Announced = null;
            entry.Ready = false;
            return true;
        }
    }

    /// <summary>
    /// The ready worker that owns the map copy <c>(mapId, instanceId)</c>: a worker that lists the map wins, otherwise the first (configuration order) ready
    /// worker that owns it by owning everything or by running instance copies. False when no ready worker owns it.
    /// </summary>
    public bool TryResolve(uint mapId, uint instanceId, out string nodeId) => Resolve(mapId, instanceId, requireReady: true, out nodeId);

    /// <summary>
    /// Like <see cref="TryResolve"/>, but over every configured worker whether or not its link is up: the worker that will own the map copy once its link registers
    /// (a down worker's announcement is dropped, so its static <c>Gateway:Workers:N:Maps</c> list applies). False when no configured worker could own it at all.
    /// </summary>
    public bool TryResolveConfigured(uint mapId, uint instanceId, out string nodeId) => Resolve(mapId, instanceId, requireReady: false, out nodeId);

    private bool Resolve(uint mapId, uint instanceId, bool requireReady, out string nodeId)
    {
        lock (_sync)
        {
            foreach (Entry entry in _entries)
            {
                if ((entry.Ready || !requireReady) && entry.Effective.ListsMap(mapId))
                {
                    nodeId = entry.NodeId;
                    return true;
                }
            }

            foreach (Entry entry in _entries)
            {
                if ((entry.Ready || !requireReady) && entry.Effective.Owns(mapId, instanceId))
                {
                    nodeId = entry.NodeId;
                    return true;
                }
            }
        }

        nodeId = string.Empty;
        return false;
    }

    /// <summary>The designated login worker while its link is up. False when none is configured or it is down (the caller then falls back to any ready link).</summary>
    public bool TryGetLoginWorker(out string nodeId)
    {
        if (_loginWorker is { } login)
        {
            lock (_sync)
            {
                if (FindEntry(login) is { Ready: true })
                {
                    nodeId = login;
                    return true;
                }
            }
        }

        nodeId = string.Empty;
        return false;
    }

    /// <summary>A point-in-time copy of every row, in configuration order.</summary>
    public IReadOnlyList<GatewayRouteEntry> Snapshot()
    {
        lock (_sync)
        {
            var rows = new GatewayRouteEntry[_entries.Length];
            for (int index = 0; index < _entries.Length; index++)
            {
                Entry entry = _entries[index];
                rows[index] = new GatewayRouteEntry(entry.NodeId, entry.Ready, entry.Announced is not null, entry.Effective.ToString());
            }

            return rows;
        }
    }

    private Entry? FindEntry(string nodeId)
    {
        foreach (Entry entry in _entries)
        {
            if (string.Equals(entry.NodeId, nodeId, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    private static MapOwnership ConfiguredOwnership(int[]? maps)
    {
        if (maps is null || maps.Length == 0)
        {
            return MapOwnership.All;
        }

        var ranges = new List<MapOwnership.Range>(maps.Length);
        foreach (int map in maps)
        {
            if (map >= 0)
            {
                ranges.Add(new MapOwnership.Range((uint)map, (uint)map));
            }
        }

        return ranges.Count == 0 ? MapOwnership.All : new MapOwnership(ranges, allowInstanceMaps: true);
    }
}
