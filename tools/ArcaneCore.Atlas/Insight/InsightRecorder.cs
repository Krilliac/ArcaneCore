using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Live;

namespace ArcaneCore.Atlas.Insight;

/// <summary>One entity of a recorded frame (columnar fields; motion names are interned in the recorder and packed into <see cref="Flags"/>).</summary>
/// <param name="Guid">Object guid.</param>
/// <param name="Kind">A <see cref="LiveKind"/> value.</param>
/// <param name="X">World X.</param>
/// <param name="Y">World Y.</param>
/// <param name="Z">World Z.</param>
/// <param name="Hp">Health percent, 0 to 100.</param>
/// <param name="Flags">Bit 0 is <see cref="InsightRecorder.FlagCombat"/>; bits 8 to 15 index the motion table (0 = none).</param>
/// <param name="D">The frame's <c>d</c> (display id) or 0 when the frame had none.</param>
internal readonly record struct RecordedEntity(ulong Guid, byte Kind, float X, float Y, float Z, byte Hp, uint Flags, uint D);

/// <summary>A frame reconstructed from the recording.</summary>
internal sealed record ReplayFrame(long Seq, long ClockMs, uint Map, uint Instance, IReadOnlyList<RecordedEntity> Entities);

/// <summary>What changed between two recorded frames (guids, ascending).</summary>
internal sealed record ReplayDiff(IReadOnlyList<ulong> Appeared, IReadOnlyList<ulong> Vanished, IReadOnlyList<ulong> Died, IReadOnlyList<ulong> Moved);

/// <summary>
/// A ring of compact live frames, bounded by bytes and by the age of the frames' own clock. Stored columnar: guids, kinds, positions and hp for
/// every entity, and the flag and <c>d</c> columns only for frames that have a non-zero value in them. Splines, routes, homes, targets and
/// the look fields other than <c>d</c> are not recorded (see <see cref="ReplayFields"/>). Any thread; one lock guards the ring.
/// </summary>
internal sealed class InsightRecorder
{
    /// <summary>Flag bit: the entity was in combat (the rev-2 <c>cb</c> field).</summary>
    public const uint FlagCombat = 1;

    private const int FrameOverheadBytes = 96;
    private const int MotionShift = 8;
    private const int MaxMotions = 254;

    private readonly Lock _gate = new();
    private readonly FrameList _frames = new();
    private readonly Dictionary<string, int> _motionIds = new(StringComparer.Ordinal);
    private readonly List<string> _motions = [];
    private long _bytes;
    private long _evicted;
    private long _droppedEntities;

    /// <summary>Creates an empty recorder bounded by <paramref name="maxBytes"/> and <paramref name="maxAge"/>.</summary>
    public InsightRecorder(long maxBytes, TimeSpan maxAge)
    {
        MaxBytes = maxBytes;
        MaxAge = maxAge;
    }

    /// <summary>The fields a replayed frame carries, in order. Everything else a live frame has (splines, routes, targets, animation state) is not recorded.</summary>
    public static IReadOnlyList<string> ReplayFields { get; } = ["guid", "kind", "x", "y", "z", "hp", "motion", "d"];

    /// <summary>The byte budget.</summary>
    public long MaxBytes { get; }

    /// <summary>The oldest frame kept is at most this much older than the newest, by the frames' own clock.</summary>
    public TimeSpan MaxAge { get; }

    /// <summary>Bytes the recorded columns occupy.</summary>
    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                return _bytes;
            }
        }
    }

    /// <summary>Frames in the ring.</summary>
    public int FrameCount
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count;
            }
        }
    }

    /// <summary>Sequence of the oldest frame in the ring, 0 when empty.</summary>
    public long OldestSeq
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count == 0 ? 0 : _frames[0].Seq;
            }
        }
    }

    /// <summary>Sequence of the newest frame in the ring, 0 when empty.</summary>
    public long NewestSeq
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count == 0 ? 0 : _frames[^1].Seq;
            }
        }
    }

    /// <summary>Clock of the newest frame, 0 when empty.</summary>
    public long NewestClockMs
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count == 0 ? 0 : _frames[^1].ClockMs;
            }
        }
    }

    /// <summary>Frames removed from the ring because of the byte or age bound (or a sequence restart).</summary>
    public long EvictedFrames
    {
        get
        {
            lock (_gate)
            {
                return _evicted;
            }
        }
    }

    /// <summary>Entities of accepted JSON frames that were malformed and not recorded.</summary>
    public long DroppedEntities => Interlocked.Read(ref _droppedEntities);

    /// <summary>Records one frame. A sequence that does not advance means the feed restarted: the older frames are dropped, since their sequence numbers would collide.</summary>
    public void Add(long seq, long clockMs, uint map, uint instance, ReadOnlySpan<RecordedEntity> entities)
    {
        int count = entities.Length;
        var guid = new ulong[count];
        var kind = new byte[count];
        var x = new float[count];
        var y = new float[count];
        var z = new float[count];
        var hp = new byte[count];
        uint[]? flags = null;
        uint[]? display = null;
        for (int i = 0; i < count; i++)
        {
            RecordedEntity e = entities[i];
            guid[i] = e.Guid;
            kind[i] = e.Kind;
            x[i] = e.X;
            y[i] = e.Y;
            z[i] = e.Z;
            hp[i] = e.Hp;
            if (e.Flags != 0)
            {
                flags ??= new uint[count];
                flags[i] = e.Flags;
            }

            if (e.D != 0)
            {
                display ??= new uint[count];
                display[i] = e.D;
            }
        }

        long bytes = FrameOverheadBytes + (count * (8L + 1 + 4 + 4 + 4 + 1)) + (flags is null ? 0 : count * 4L) + (display is null ? 0 : count * 4L);
        var stored = new Stored(seq, clockMs, map, instance, guid, kind, x, y, z, hp, flags, display, bytes);
        lock (_gate)
        {
            if (_frames.Count > 0 && seq <= _frames[^1].Seq)
            {
                _evicted += _frames.Count;
                _frames.Clear();
                _bytes = 0;
            }

            _frames.Add(stored);
            _bytes += bytes;
            long oldestAllowed = clockMs - (long)MaxAge.TotalMilliseconds;
            while (_frames.Count > 0 && (_bytes > MaxBytes || _frames[0].ClockMs < oldestAllowed))
            {
                _bytes -= _frames[0].Bytes;
                _frames.RemoveFirst();
                _evicted++;
            }
        }
    }

    /// <summary>Records a <c>live.frame</c> payload as published by the live feed. False, with the reason, when the frame has no usable header.</summary>
    public bool AddJson(JsonNode? frame, out string? problem)
    {
        problem = null;
        if (frame is not JsonObject o)
        {
            problem = "the frame is not a JSON object";
            return false;
        }

        if (!TryLong(o["seq"], out long seq))
        {
            problem = "the frame has no integer seq";
            return false;
        }

        if (!TryLong(o["clockMs"], out long clock) || !TryLong(o["map"], out long map) || !TryLong(o["instance"], out long instance) || map < 0 || instance < 0)
        {
            problem = "the frame has no clockMs, map or instance";
            return false;
        }

        if (o["entities"] is not JsonArray list)
        {
            problem = "the frame has no entities list";
            return false;
        }

        var parsed = new List<RecordedEntity>(list.Count);
        foreach (JsonNode? node in list)
        {
            if (TryEntity(node, out RecordedEntity entity))
            {
                parsed.Add(entity);
            }
            else
            {
                Interlocked.Increment(ref _droppedEntities);
            }
        }

        Add(seq, clock, (uint)map, (uint)instance, CollectionsMarshal.AsSpan(parsed));
        return true;
    }

    /// <summary>The frame with exactly this sequence, or null when it is not in the ring.</summary>
    public ReplayFrame? Exact(long seq)
    {
        lock (_gate)
        {
            int index = IndexOfSeq(seq);
            return index < 0 ? null : ToFrame(_frames[index]);
        }
    }

    /// <summary>The first frame after <paramref name="seq"/>, by sequence; false when there is none.</summary>
    public bool TryNext(long seq, out long nextSeq, out long clockMs)
    {
        lock (_gate)
        {
            foreach (Stored f in _frames)
            {
                if (f.Seq > seq)
                {
                    nextSeq = f.Seq;
                    clockMs = f.ClockMs;
                    return true;
                }
            }
        }

        nextSeq = 0;
        clockMs = 0;
        return false;
    }

    /// <summary>The newest frame, or null when empty.</summary>
    public ReplayFrame? Latest()
    {
        lock (_gate)
        {
            return _frames.Count == 0 ? null : ToFrame(_frames[^1]);
        }
    }

    /// <summary>
    /// The state at <paramref name="clockMs"/>: the entities of the frame at or before it, with positions interpolated towards the next
    /// frame; hp, flags and <c>d</c> are the earlier frame's. Null outside the recorded span.
    /// </summary>
    public ReplayFrame? AtClock(long clockMs)
    {
        lock (_gate)
        {
            if (_frames.Count == 0 || clockMs < _frames[0].ClockMs || clockMs > _frames[^1].ClockMs)
            {
                return null;
            }

            int lo = 0;
            int hi = _frames.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (_frames[mid].ClockMs <= clockMs)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            Stored a = _frames[lo];
            ReplayFrame early = ToFrame(a);
            if (a.ClockMs == clockMs || lo + 1 >= _frames.Count)
            {
                return early with { ClockMs = clockMs };
            }

            Stored b = _frames[lo + 1];
            if (b.Map != a.Map || b.Instance != a.Instance || b.ClockMs <= a.ClockMs)
            {
                return early with { ClockMs = clockMs };
            }

            float t = (float)(clockMs - a.ClockMs) / (b.ClockMs - a.ClockMs);
            var later = new Dictionary<ulong, int>(b.Guid.Length);
            for (int i = 0; i < b.Guid.Length; i++)
            {
                later[b.Guid[i]] = i;
            }

            var entities = new RecordedEntity[early.Entities.Count];
            for (int i = 0; i < entities.Length; i++)
            {
                RecordedEntity e = early.Entities[i];
                if (later.TryGetValue(e.Guid, out int j))
                {
                    e = e with
                    {
                        X = e.X + ((b.X[j] - e.X) * t),
                        Y = e.Y + ((b.Y[j] - e.Y) * t),
                        Z = e.Z + ((b.Z[j] - e.Z) * t),
                    };
                }

                entities[i] = e;
            }

            return early with { ClockMs = clockMs, Entities = entities };
        }
    }

    /// <summary>What changed between the frames with sequences <paramref name="a"/> and <paramref name="b"/>; <c>moved</c> is a straight-line distance strictly greater than <paramref name="movedYards"/>.</summary>
    /// <exception cref="ArgumentException">A sequence is not in the ring.</exception>
    public ReplayDiff Diff(long a, long b, double movedYards)
    {
        ReplayFrame first = Exact(a) ?? throw new ArgumentException($"frame {a} is not in the recording.", nameof(a));
        ReplayFrame second = Exact(b) ?? throw new ArgumentException($"frame {b} is not in the recording.", nameof(b));
        Dictionary<ulong, RecordedEntity> before = first.Entities.ToDictionary(e => e.Guid);
        Dictionary<ulong, RecordedEntity> after = second.Entities.ToDictionary(e => e.Guid);
        var appeared = new List<ulong>();
        var died = new List<ulong>();
        var moved = new List<ulong>();
        foreach ((ulong guid, RecordedEntity now) in after)
        {
            if (!before.TryGetValue(guid, out RecordedEntity was))
            {
                appeared.Add(guid);
                continue;
            }

            if (was.Hp > 0 && now.Hp == 0)
            {
                died.Add(guid);
            }

            double dx = now.X - was.X;
            double dy = now.Y - was.Y;
            double dz = now.Z - was.Z;
            if (Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz)) > movedYards)
            {
                moved.Add(guid);
            }
        }

        List<ulong> vanished = [.. before.Keys.Where(g => !after.ContainsKey(g))];
        appeared.Sort();
        vanished.Sort();
        died.Sort();
        moved.Sort();
        return new ReplayDiff(appeared, vanished, died, moved);
    }

    /// <summary>
    /// Entities whose hp fell to zero between two consecutive frames of <paramref name="map"/> that both lie within <paramref name="windowMs"/>
    /// of that map's newest frame, as they were in the frame where they were first dead.
    /// </summary>
    public IReadOnlyList<RecordedEntity> Deaths(long windowMs, uint map)
    {
        var deaths = new List<RecordedEntity>();
        lock (_gate)
        {
            long newest = long.MinValue;
            foreach (Stored f in _frames)
            {
                if (f.Map == map)
                {
                    newest = f.ClockMs;
                }
            }

            Stored? previous = null;
            foreach (Stored f in _frames)
            {
                if (f.Map != map || f.ClockMs < newest - windowMs)
                {
                    continue;
                }

                if (previous is { } p)
                {
                    var was = new Dictionary<ulong, byte>(p.Guid.Length);
                    for (int i = 0; i < p.Guid.Length; i++)
                    {
                        was[p.Guid[i]] = p.Hp[i];
                    }

                    for (int i = 0; i < f.Guid.Length; i++)
                    {
                        if (f.Hp[i] == 0 && was.TryGetValue(f.Guid[i], out byte hp) && hp > 0)
                        {
                            deaths.Add(ToEntity(f, i));
                        }
                    }
                }

                previous = f;
            }
        }

        return deaths;
    }

    /// <summary>The motion name packed into <paramref name="flags"/>, or null when the frame had none.</summary>
    public string? MotionName(uint flags)
    {
        int id = (int)((flags >> MotionShift) & 0xFF);
        lock (_gate)
        {
            return id == 0 || id > _motions.Count ? null : _motions[id - 1];
        }
    }

    private int IndexOfSeq(long seq)
    {
        int lo = 0;
        int hi = _frames.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            long at = _frames[mid].Seq;
            if (at == seq)
            {
                return mid;
            }

            if (at < seq)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return -1;
    }

    private static RecordedEntity ToEntity(Stored f, int i)
        => new(f.Guid[i], f.Kind[i], f.X[i], f.Y[i], f.Z[i], f.Hp[i], f.Flags?[i] ?? 0, f.D?[i] ?? 0);

    private static ReplayFrame ToFrame(Stored f)
    {
        var entities = new RecordedEntity[f.Guid.Length];
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = ToEntity(f, i);
        }

        return new ReplayFrame(f.Seq, f.ClockMs, f.Map, f.Instance, entities);
    }

    private bool TryEntity(JsonNode? node, out RecordedEntity entity)
    {
        entity = default;
        if (node is not JsonObject o
            || o["g"] is not JsonValue g || !g.TryGetValue(out string? guidText) || !LiveGuid.TryParse(guidText, out ulong guid) || guid == 0
            || o["k"] is not JsonValue k || !k.TryGetValue(out string? kindText) || !LiveKinds.TryParse(kindText, out LiveKind kind)
            || !TryFloat(o["x"], out float x) || !TryFloat(o["y"], out float y) || !TryFloat(o["z"], out float z))
        {
            return false;
        }

        byte hp = 100;
        if (o["hp"] is { } hpNode)
        {
            if (!TryFloat(hpNode, out float percent))
            {
                return false;
            }

            hp = (byte)Math.Clamp((int)Math.Round(percent, MidpointRounding.AwayFromZero), 0, 100);
        }

        uint flags = 0;
        if (o["cb"] is JsonValue cb && cb.TryGetValue(out bool combat) && combat)
        {
            flags |= FlagCombat;
        }

        if (o["mt"] is JsonValue mt && mt.TryGetValue(out string? motion) && !string.IsNullOrEmpty(motion))
        {
            flags |= (uint)Intern(motion) << MotionShift;
        }

        uint display = 0;
        if (o["d"] is { } dNode)
        {
            if (!TryLong(dNode, out long d) || d < 0 || d > uint.MaxValue)
            {
                return false;
            }

            display = (uint)d;
        }

        entity = new RecordedEntity(guid, (byte)kind, x, y, z, hp, flags, display);
        return true;
    }

    private int Intern(string motion)
    {
        lock (_gate)
        {
            if (_motionIds.TryGetValue(motion, out int id))
            {
                return id;
            }

            if (_motions.Count >= MaxMotions)
            {
                return MaxMotions + 1;
            }

            _motions.Add(motion);
            id = _motions.Count;
            _motionIds[motion] = id;
            return id;
        }
    }

    private static bool TryLong(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue v)
        {
            return false;
        }

        if (v.TryGetValue(out long l))
        {
            value = l;
            return true;
        }

        if (v.TryGetValue(out double d) && Math.Floor(d) == d && Math.Abs(d) < 9e15)
        {
            value = (long)d;
            return true;
        }

        return false;
    }

    private static bool TryFloat(JsonNode? node, out float value)
    {
        value = 0;
        if (node is JsonValue v && v.TryGetValue(out double d) && double.IsFinite(d))
        {
            value = (float)d;
            return float.IsFinite(value);
        }

        return false;
    }

    /// <summary>
    /// The frames oldest first. Evicting the oldest advances a head index instead of shifting the list, and the dead prefix is dropped once it
    /// is both large and at least half the storage, so eviction is amortised O(1). Guarded by the recorder's gate like the rest of its state.
    /// </summary>
    private sealed class FrameList
    {
        private const int CompactAt = 1024;

        private readonly List<Stored?> _items = [];
        private int _head;

        public int Count => _items.Count - _head;

        public Stored this[int index] => _items[_head + index]!;

        public void Add(Stored frame) => _items.Add(frame);

        public void Clear()
        {
            _items.Clear();
            _head = 0;
        }

        public void RemoveFirst()
        {
            _items[_head] = null;
            _head++;
            if (_head == _items.Count)
            {
                Clear();
            }
            else if (_head >= CompactAt && _head * 2 >= _items.Count)
            {
                _items.RemoveRange(0, _head);
                _head = 0;
            }
        }

        public IEnumerator<Stored> GetEnumerator()
        {
            for (int i = _head; i < _items.Count; i++)
            {
                yield return _items[i]!;
            }
        }
    }

    private sealed record Stored(
        long Seq,
        long ClockMs,
        uint Map,
        uint Instance,
        ulong[] Guid,
        byte[] Kind,
        float[] X,
        float[] Y,
        float[] Z,
        byte[] Hp,
        uint[]? Flags,
        uint[]? D,
        long Bytes);
}
