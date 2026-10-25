using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ArcaneCore.Atlas.Live;

namespace ArcaneCore.Atlas.Host.Public;

/// <summary>What the world's <c>links</c> answer says (design 0.6, "Entities linked to a real player"), as the projector uses it.</summary>
/// <param name="Supported">False when the world does not answer <c>links</c> (an older daemon) or the last ask failed.</param>
/// <param name="FrameSeq">The sequence number of the frame that was current when the answer was asked for; the answer is stale when frames have moved on by more than 2.</param>
/// <param name="Linked">Guids linked to a real player by owner, charmer, creator, summoner or group.</param>
/// <param name="SummonEntries">Creature entries the world has seen owned by something; kept as the template set when <c>links</c> can no longer be asked.</param>
/// <param name="OwnedObjectEntries">Game object entries the world has seen with an owner; the same, for objects.</param>
internal sealed record LinksSnapshot(bool Supported, long FrameSeq, IReadOnlySet<ulong> Linked, IReadOnlySet<uint> SummonEntries, IReadOnlySet<uint> OwnedObjectEntries)
{
    /// <summary>No answer: the projector fails closed.</summary>
    public static LinksSnapshot Unsupported { get; } = new(false, 0, new HashSet<ulong>(), new HashSet<uint>(), new HashSet<uint>());
}

/// <summary>What a guest sees an entity as. Players and bots are both <see cref="Character"/>.</summary>
internal enum GuestKind
{
    Character,
    Creature,
    GameObject,
}

/// <summary>One entity a guest may see. <see cref="Guid"/> is server-side only: it never reaches a serializer (the opaque id is derived per session).</summary>
internal readonly record struct GuestEntity(ulong Guid, GuestKind Kind, uint Entry, string? Name, double X, double Y, double Z, double Orientation, double? Hp, string? Motion);

/// <summary>A coarse cell of real players: the cell's centre and how many players were in it when the delayed snapshot was taken.</summary>
internal readonly record struct GuestCell(double X, double Y, int Count);

/// <summary>The result of projecting one frame for guests: the entities and cells every guest of the map may see (before per-session opaque ids).</summary>
internal sealed record GuestProjection(long Seq, long ClockMs, uint Map, uint Instance, IReadOnlyList<GuestEntity> Entities, IReadOnlyList<GuestCell> Cells, int CellYards, bool Reduced);

/// <summary>
/// The guest data policy of design 0.6, applied to one map's frames. One instance per map (it keeps the delay history and the release times of linked
/// entities). <see cref="Project"/> runs once per frame and is shared by every guest; <see cref="WriteFrame"/> then writes one session's bytes with
/// that session's opaque ids. The browser never receives anything this class removed.
/// <para>
/// Rules (<c>ShowPlayers=false</c>): players are not emitted and are counted into coarse delayed cells (cells under <c>PlayerMinCellCount</c> omitted);
/// every entity linked to a player is removed (owner chains and groups from <c>links</c>; <c>tg</c> and motion targets from the frame itself, transitively
/// to depth 3) and stays removed for <c>PlayerDelaySeconds</c> after its last link; <c>chase</c> and <c>follow</c> collapse to <c>moving</c> unless the
/// target is an emitted non-player entity. With the world's <c>links</c> missing or older than two frames the projector fails closed: every bot, every following
/// creature, every creature or object whose entry the world once reported as owned, and (with no such list yet) every game object is dropped. With
/// <c>ShowPlayers=true</c> players are emitted (named only with <c>ShowPlayerNames</c>). In every case <c>tg</c>, <c>home</c>, splines and routes are never
/// copied. Thread-safety: <see cref="Project"/> is called by one feed reader at a time; the returned projection is immutable.
/// </para>
/// </summary>
internal sealed class GuestFrameProjector(PublicGuestOptions options, TimeProvider time)
{
    /// <summary>Links are followed this many owner/target steps.</summary>
    public const int MaxLinkDepth = 3;

    /// <summary>A links answer older than this many frames is stale.</summary>
    public const int MaxLinksAgeFrames = 2;

    /// <summary>The label of a real player shown without a name.</summary>
    public const string PlayerLabel = "Player";

    private readonly Dictionary<ulong, long> _hiddenUntil = [];
    private readonly List<(long Stamp, Dictionary<(int X, int Y), int> Counts)> _history = [];
    private readonly HashSet<uint> _knownSummonEntries = [];
    private readonly HashSet<uint> _knownOwnedObjectEntries = [];
    private long _lastHistoryStamp = long.MinValue;

    /// <summary>Projects <paramref name="frame"/> for guests using the latest <paramref name="links"/> answer.</summary>
    public GuestProjection Project(LiveFrame frame, LinksSnapshot links)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(links);
        long now = time.GetTimestamp();
        long delay = (long)(options.PlayerDelaySeconds * (double)time.TimestampFrequency);
        bool showPlayers = options.ShowPlayers;
        bool linksUsable = links.Supported && frame.Seq - links.FrameSeq <= MaxLinksAgeFrames;
        if (links.Supported)
        {
            _knownSummonEntries.UnionWith(links.SummonEntries);
            _knownOwnedObjectEntries.UnionWith(links.OwnedObjectEntries);
        }

        var hidden = new HashSet<ulong>();
        var players = new HashSet<ulong>();
        foreach (LiveEntity entity in frame.Entities)
        {
            if (entity.Kind == LiveKind.Player)
            {
                players.Add(entity.Id);
            }
        }

        if (!showPlayers)
        {
            var roots = new HashSet<ulong>();
            foreach (LiveEntity entity in frame.Entities)
            {
                if (entity.Kind == LiveKind.Player)
                {
                    hidden.Add(entity.Id);
                    roots.Add(entity.Id);
                }
            }

            var linked = new HashSet<ulong>();
            if (linksUsable)
            {
                foreach (ulong guid in links.Linked)
                {
                    linked.Add(guid);
                    roots.Add(guid);
                }
            }

            // Links the frame itself shows: a target (tg is the motion target, else the target) that is a player or a linked entity.
            for (int depth = 0; depth < MaxLinkDepth; depth++)
            {
                bool grew = false;
                foreach (LiveEntity entity in frame.Entities)
                {
                    if (entity.Kind != LiveKind.Player && entity.Target is { } target && roots.Contains(target) && roots.Add(entity.Id))
                    {
                        linked.Add(entity.Id);
                        grew = true;
                    }
                }

                if (!grew)
                {
                    break;
                }
            }

            long releaseAt = now + delay;
            foreach (ulong guid in linked)
            {
                _hiddenUntil[guid] = releaseAt;
            }

            hidden.UnionWith(linked);
            foreach (KeyValuePair<ulong, long> held in _hiddenUntil)
            {
                if (held.Value > now)
                {
                    hidden.Add(held.Key);
                }
            }

            foreach (ulong expired in _hiddenUntil.Where(p => p.Value <= now).Select(p => p.Key).ToList())
            {
                _hiddenUntil.Remove(expired);
            }
        }

        bool reduced = !showPlayers && !linksUsable;
        var emit = new List<LiveEntity>(frame.Entities.Count);
        var emitted = new HashSet<ulong>();
        foreach (LiveEntity entity in frame.Entities)
        {
            if (hidden.Contains(entity.Id) || (reduced && FailClosedDrops(entity)))
            {
                continue;
            }

            emit.Add(entity);
            emitted.Add(entity.Id);
        }

        var entities = new List<GuestEntity>(emit.Count);
        foreach (LiveEntity entity in emit)
        {
            entities.Add(ToGuest(entity, emitted, players, showPlayers));
        }

        IReadOnlyList<GuestCell> cells = showPlayers ? [] : Cells(frame, now, delay);
        return new GuestProjection(frame.Seq, frame.ClockMs, frame.Map, frame.Instance, entities, cells, options.PlayerCellYards, reduced);
    }

    /// <summary>
    /// Writes one session's frame event: <c>{"op":"event","topic":"live.frame","data":{...}}</c>. Entity ids are <c>HMAC-SHA256(opaqueKey, guid)</c>
    /// truncated to 8 bytes, hex; the same entity has the same id for that session only.
    /// </summary>
    public static void WriteFrame(IBufferWriter<byte> output, GuestProjection projection, ReadOnlySpan<byte> opaqueKey)
    {
        using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString("op"u8, "event"u8);
        writer.WriteString("topic"u8, "live.frame"u8);
        writer.WriteStartObject("data"u8);
        writer.WriteNumber("seq"u8, projection.Seq);
        writer.WriteNumber("clockMs"u8, projection.ClockMs);
        writer.WriteNumber("map"u8, projection.Map);
        writer.WriteNumber("instance"u8, projection.Instance);
        writer.WriteBoolean("reduced"u8, projection.Reduced);
        writer.WriteStartArray("entities"u8);
        Span<char> id = stackalloc char[16];
        foreach (GuestEntity entity in projection.Entities)
        {
            writer.WriteStartObject();
            OpaqueId(opaqueKey, entity.Guid, id);
            writer.WriteString("g"u8, id);
            writer.WriteString("k"u8, KindName(entity.Kind));
            if (entity.Kind != GuestKind.Character && entity.Entry != 0)
            {
                writer.WriteNumber("e"u8, entity.Entry);
            }

            if (!string.IsNullOrEmpty(entity.Name))
            {
                writer.WriteString("n"u8, entity.Name);
            }

            writer.WriteNumber("x"u8, Round(entity.X));
            writer.WriteNumber("y"u8, Round(entity.Y));
            writer.WriteNumber("z"u8, Round(entity.Z));
            writer.WriteNumber("o"u8, Math.Round(entity.Orientation, 2));
            if (entity.Hp is { } hp)
            {
                writer.WriteNumber("hp"u8, Math.Round(hp, 1));
            }

            if (entity.Motion is not null)
            {
                writer.WriteString("mt"u8, entity.Motion);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("playerCells"u8);
        foreach (GuestCell cell in projection.Cells)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x"u8, Round(cell.X));
            writer.WriteNumber("y"u8, Round(cell.Y));
            writer.WriteNumber("n"u8, cell.Count);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber("cellYards"u8, projection.CellYards);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>The opaque id of <paramref name="guid"/> for a session key, as 16 lower-case hex characters.</summary>
    public static string OpaqueId(ReadOnlySpan<byte> opaqueKey, ulong guid)
    {
        Span<char> text = stackalloc char[16];
        OpaqueId(opaqueKey, guid, text);
        return new string(text);
    }

    /// <summary>Writes the opaque id of <paramref name="guid"/> into <paramref name="destination"/> (16 characters).</summary>
    public static void OpaqueId(ReadOnlySpan<byte> opaqueKey, ulong guid, Span<char> destination)
    {
        Span<byte> message = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(message, guid);
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(opaqueKey, message, mac);
        for (int i = 0; i < 8; i++)
        {
            mac[i].TryFormat(destination[(i * 2)..], out _, "x2", CultureInfo.InvariantCulture);
        }
    }

    private static string KindName(GuestKind kind) => kind switch
    {
        GuestKind.Character => "character",
        GuestKind.Creature => "creature",
        _ => "gameobject",
    };

    private static double Round(double value) => Math.Round(value, 2);

    private bool FailClosedDrops(LiveEntity entity) => entity.Kind switch
    {
        LiveKind.Bot => true,
        LiveKind.Creature => string.Equals(entity.Motion, "follow", StringComparison.Ordinal) || _knownSummonEntries.Contains(entity.Entry),
        LiveKind.GameObject => _knownOwnedObjectEntries.Count == 0 || _knownOwnedObjectEntries.Contains(entity.Entry),
        _ => false,
    };

    private GuestEntity ToGuest(LiveEntity entity, HashSet<ulong> emitted, HashSet<ulong> players, bool showPlayers)
    {
        GuestKind kind = entity.Kind switch
        {
            LiveKind.Player or LiveKind.Bot => GuestKind.Character,
            LiveKind.GameObject => GuestKind.GameObject,
            _ => GuestKind.Creature,
        };
        string? name = entity.Kind == LiveKind.Player
            ? (options.ShowPlayerNames && showPlayers ? entity.Name : PlayerLabel)
            : entity.Name;
        string? motion = entity.Motion switch
        {
            null or "" => null,
            "idle" => "idle",
            "chase" or "follow" => entity.Target is { } target && emitted.Contains(target) && !players.Contains(target) ? entity.Motion : "moving",
            _ => "moving",
        };
        return new GuestEntity(entity.Id, kind, kind == GuestKind.Character ? 0 : entity.Entry, name, entity.Position.X, entity.Position.Y, entity.Position.Z, entity.Orientation, entity.HealthPercent, motion);
    }

    private IReadOnlyList<GuestCell> Cells(LiveFrame frame, long now, long delay)
    {
        // At most one snapshot a second is kept: the history only needs to be as fine as the delay is long.
        if (now - _lastHistoryStamp >= time.TimestampFrequency || _history.Count == 0)
        {
            var counts = new Dictionary<(int, int), int>();
            double yards = options.PlayerCellYards;
            foreach (LiveEntity entity in frame.Entities)
            {
                if (entity.Kind == LiveKind.Player)
                {
                    var cell = ((int)Math.Floor(entity.Position.X / yards), (int)Math.Floor(entity.Position.Y / yards));
                    counts[cell] = counts.GetValueOrDefault(cell) + 1;
                }
            }

            _history.Add((now, counts));
            _lastHistoryStamp = now;
        }

        // The newest snapshot that is at least the delay old; everything older than it is no longer needed.
        int chosen = -1;
        for (int i = 0; i < _history.Count; i++)
        {
            if (now - _history[i].Stamp >= delay)
            {
                chosen = i;
            }
        }

        if (chosen < 0)
        {
            return [];
        }

        _history.RemoveRange(0, chosen);
        var cells = new List<GuestCell>();
        double size = options.PlayerCellYards;
        foreach (KeyValuePair<(int X, int Y), int> pair in _history[0].Counts.OrderBy(p => p.Key.X).ThenBy(p => p.Key.Y))
        {
            if (pair.Value >= options.PlayerMinCellCount)
            {
                cells.Add(new GuestCell((pair.Key.X + 0.5) * size, (pair.Key.Y + 0.5) * size, pair.Value));
            }
        }

        return cells;
    }
}
