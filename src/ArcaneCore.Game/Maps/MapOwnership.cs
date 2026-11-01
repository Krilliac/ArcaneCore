using System.Globalization;
using System.Text;

namespace ArcaneCore.Game.Maps;

/// <summary>Outcome of asking a <see cref="WorldRuntime"/> for a map this process may refuse (<see cref="WorldRuntime.TryGetMap"/>).</summary>
public enum MapAccessResult
{
    /// <summary>The map is owned here and was returned (created on first use).</summary>
    Granted = 0,

    /// <summary>
    /// Another worker owns the map (Cluster:Worker:OwnedMaps). A login turns this into a redirect and a teleport into a far
    /// transfer once those exist; until then the caller refuses cleanly.
    /// </summary>
    NotOwned = 1,
}

/// <summary>Thrown by <see cref="WorldRuntime.GetMap(uint, uint)"/> for a map this worker does not own; callers that can refuse use <see cref="WorldRuntime.TryGetMap"/>.</summary>
public sealed class MapNotOwnedException(uint mapId, uint instanceId)
    : InvalidOperationException($"map {mapId} (instance {instanceId}) is not owned by this worker (Cluster:Worker:OwnedMaps)")
{
    public uint MapId { get; } = mapId;

    public uint InstanceId { get; } = instanceId;
}

/// <summary>
/// The set of maps one world process owns (<c>Cluster:Worker:OwnedMaps</c>), immutable. An empty set owns every map, which is the combined
/// (single process) behaviour. A non-empty set owns the listed shared maps (instance 0); instance copies of any map are owned only while
/// <see cref="AllowInstanceMaps"/> is set. Thread-safe: it never changes after construction.
/// </summary>
public sealed class MapOwnership
{
    /// <summary>A map id range, both ends inclusive.</summary>
    public readonly record struct Range(uint First, uint Last)
    {
        public bool Contains(uint mapId) => mapId >= First && mapId <= Last;

        public bool Overlaps(Range other) => First <= other.Last && other.First <= Last;

        public override string ToString() => First == Last ? First.ToString(CultureInfo.InvariantCulture) : $"{First}-{Last}";
    }

    /// <summary>Owns everything: the combined behaviour.</summary>
    public static MapOwnership All { get; } = new([], allowInstanceMaps: true);

    private readonly Range[] _ranges;

    public MapOwnership(IEnumerable<Range> ranges, bool allowInstanceMaps)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        _ranges = [.. ranges.OrderBy(r => r.First).ThenBy(r => r.Last)];
        AllowInstanceMaps = allowInstanceMaps;
    }

    /// <summary>True when no map is listed, so every map is owned.</summary>
    public bool OwnsEverything => _ranges.Length == 0;

    /// <summary>Whether instance copies (instance id other than 0) of maps outside the list are owned (only meaningful for a non-empty list).</summary>
    public bool AllowInstanceMaps { get; }

    public IReadOnlyList<Range> Ranges => _ranges;

    /// <summary>Whether the list names this map id (an empty list names none; use <see cref="Owns"/> for ownership).</summary>
    public bool ListsMap(uint mapId)
    {
        foreach (Range range in _ranges)
        {
            if (range.Contains(mapId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether this process owns the given map copy: everything when the list is empty; else a listed shared map, or any instance copy while <see cref="AllowInstanceMaps"/>.</summary>
    public bool Owns(uint mapId, uint instanceId)
        => OwnsEverything || ListsMap(mapId) || (instanceId != 0 && AllowInstanceMaps);

    /// <summary>The list as the config text it parses from (<c>0,1,30-40</c>); empty for an empty list.</summary>
    public string ToListText() => string.Join(',', _ranges.Select(r => r.ToString()));

    /// <summary>
    /// Parses <c>0,1,30-40</c> (separators: comma, semicolon, whitespace; ranges inclusive). Null or blank is the empty list. Returns false with
    /// <paramref name="error"/> for a token that is not a map id or range, or a range written backwards. Overlapping entries inside one list are accepted
    /// (they describe one owner).
    /// </summary>
    public static bool TryParseRanges(string? text, out IReadOnlyList<Range> ranges, out string? error)
    {
        var result = new List<Range>();
        ranges = result;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        foreach (string raw in text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int dash = raw.IndexOf('-', StringComparison.Ordinal);
            bool ok;
            uint first;
            uint last;
            if (dash < 0)
            {
                ok = uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out first);
                last = first;
            }
            else
            {
                bool firstOk = uint.TryParse(raw.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out first);
                bool lastOk = uint.TryParse(raw.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out last);
                ok = firstOk && lastOk;
            }

            if (!ok)
            {
                error = $"'{raw}' is not a map id or a range such as 30-40";
                result.Clear();
                return false;
            }

            if (last < first)
            {
                error = $"range '{raw}' runs backwards";
                result.Clear();
                return false;
            }

            result.Add(new Range(first, last));
        }

        return true;
    }

    /// <summary>Parses a list (see <see cref="TryParseRanges"/>) into an ownership value.</summary>
    public static bool TryParse(string? text, bool allowInstanceMaps, out MapOwnership ownership, out string? error)
    {
        if (!TryParseRanges(text, out IReadOnlyList<Range> ranges, out error))
        {
            ownership = All;
            return false;
        }

        ownership = ranges.Count == 0 ? All : new MapOwnership(ranges, allowInstanceMaps);
        return true;
    }

    /// <summary>The first overlap between two range lists, or null.</summary>
    public static (Range A, Range B)? FindOverlap(IEnumerable<Range> a, IEnumerable<Range> b)
    {
        Range[] right = [.. b];
        foreach (Range left in a)
        {
            foreach (Range other in right)
            {
                if (left.Overlaps(other))
                {
                    return (left, other);
                }
            }
        }

        return null;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(OwnsEverything ? "all maps" : ToListText());
        if (!OwnsEverything && AllowInstanceMaps)
        {
            sb.Append(" + instances");
        }

        return sb.ToString();
    }
}
