using ArcaneCore.Atlas.Live;

namespace ArcaneCore.Atlas.Host.Public;

/// <summary>What the public host knows about one map's feed right now.</summary>
/// <param name="Map">The map id.</param>
/// <param name="Connected">Whether a pooled connection is subscribed and delivering frames.</param>
/// <param name="Busy">Whether the map could not get a pooled connection (the pool is at capacity); guests are told "busy".</param>
/// <param name="Reduced">Whether the guest data is reduced because the world's <c>links</c> answer is missing or stale (design 0.6, fail closed).</param>
/// <param name="Projection">The latest guest projection, or null before the first frame.</param>
/// <param name="ReceivedAt">When the projection was made (<see cref="TimeProvider.GetTimestamp"/>), 0 before the first frame.</param>
/// <param name="StaffProjection">The latest staff projection (design 0.7: players shown with their names, nothing hidden for links) for accounts at Moderator or above, or null before the first frame.</param>
internal sealed record PublicMapStatus(uint Map, bool Connected, bool Busy, bool Reduced, GuestProjection? Projection, long ReceivedAt, GuestProjection? StaffProjection = null)
{
    /// <summary>The projection this session may see: the staff one for a Moderator or above, else the guest one.</summary>
    public GuestProjection? For(PublicSession session) => session.IsStaff ? StaffProjection : Projection;
}

/// <summary>
/// The latest guest-safe state of each public map: frames from the pooled feed connections go in through <see cref="Ingest"/>, are projected once by the
/// map's <see cref="GuestFrameProjector"/> with the latest <c>links</c> answer, and the hub reads <see cref="Status"/> for every socket. Raw frames
/// are never stored here: only the projection. Thread-safe; <see cref="Ingest"/> for one map is called by one feed reader at a time.
/// </summary>
internal sealed class PublicFrameStore(PublicOptions options, TimeProvider time)
{
    private sealed class MapState(GuestFrameProjector projector, GuestFrameProjector staffProjector)
    {
        public readonly GuestFrameProjector Projector = projector;
        public readonly GuestFrameProjector StaffProjector = staffProjector;
        public LinksSnapshot Links = LinksSnapshot.Unsupported;
        public GuestProjection? Projection;
        public GuestProjection? StaffProjection;
        public long ReceivedAt;
        public bool Connected;
        public bool Busy;
    }

    private readonly Dictionary<uint, MapState> _maps = [];
    private readonly Lock _gate = new();

    /// <summary>Raised after a map's projection or state changed (called on the feed reader thread; must not block).</summary>
    public event Action<uint>? Updated;

    /// <summary>Projects a frame from the feed and keeps the result.</summary>
    public void Ingest(LiveFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        MapState state = State(frame.Map);
        GuestProjection projection;
        lock (state)
        {
            projection = state.Projector.Project(frame, state.Links);
            state.Projection = projection;
            state.StaffProjection = state.StaffProjector.Project(frame, state.Links);
            state.ReceivedAt = time.GetTimestamp();
            state.Connected = true;
            state.Busy = false;
        }

        Updated?.Invoke(frame.Map);
    }

    /// <summary>Stores the world's latest <c>links</c> answer for a map (or <see cref="LinksSnapshot.Unsupported"/> when it could not be had).</summary>
    public void SetLinks(uint map, LinksSnapshot links)
    {
        MapState state = State(map);
        lock (state)
        {
            state.Links = links;
        }
    }

    /// <summary>The links answer a map's next frame will be projected with.</summary>
    public LinksSnapshot Links(uint map)
    {
        MapState state = State(map);
        lock (state)
        {
            return state.Links;
        }
    }

    /// <summary>Records that a map's feed connection went away or came back.</summary>
    public void SetConnected(uint map, bool connected)
    {
        MapState state = State(map);
        lock (state)
        {
            state.Connected = connected;
            if (!connected)
            {
                state.Projection = null;
                state.StaffProjection = null;
                state.Links = LinksSnapshot.Unsupported;
            }
        }

        Updated?.Invoke(map);
    }

    /// <summary>Records that a map could not get a feed connection (the pool is at capacity) or can again.</summary>
    public void SetBusy(uint map, bool busy)
    {
        MapState state = State(map);
        lock (state)
        {
            state.Busy = busy;
        }

        Updated?.Invoke(map);
    }

    /// <summary>The state of a map as the hub and the commands see it.</summary>
    public PublicMapStatus Status(uint map)
    {
        MapState state = State(map);
        lock (state)
        {
            return new PublicMapStatus(map, state.Connected, state.Busy, state.Projection?.Reduced ?? false, state.Projection, state.ReceivedAt, state.StaffProjection);
        }
    }

    private MapState State(uint map)
    {
        lock (_gate)
        {
            if (!_maps.TryGetValue(map, out MapState? state))
            {
                state = new MapState(new GuestFrameProjector(options.Guest, time), new GuestFrameProjector(StaffPolicy(options.Guest), time));
                _maps[map] = state;
            }

            return state;
        }
    }

    /// <summary>
    /// The policy of the staff projection (design 0.7): the guest settings with players shown by name, so nothing is hidden or delayed. Ids are still
    /// opaque per session and <c>tg</c>, <c>home</c> and routes are still never written.
    /// </summary>
    private static PublicGuestOptions StaffPolicy(PublicGuestOptions guest)
        => new()
        {
            MaxSessions = guest.MaxSessions,
            MaxSessionsPerIp = guest.MaxSessionsPerIp,
            Maps = guest.Maps,
            FeedHz = guest.FeedHz,
            MaxBytesPerSecond = guest.MaxBytesPerSecond,
            ShowPlayers = true,
            ShowPlayerNames = true,
            PlayerCellYards = guest.PlayerCellYards,
            PlayerDelaySeconds = guest.PlayerDelaySeconds,
            PlayerMinCellCount = guest.PlayerMinCellCount,
            WorldCallsPerSecond = guest.WorldCallsPerSecond,
        };
}
