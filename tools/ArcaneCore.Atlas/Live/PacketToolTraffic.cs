using System.Text.Json.Nodes;

namespace ArcaneCore.Atlas.Live;

/// <summary>
/// What the daemon's packet tools stream on one connection (docs/areas/map-feed.md, "Packet tools"): a tap's <c>packet</c> lines and its
/// <c>tapEnd</c>, and console lines printed after their command's result. Filled by the session's reader loop, drained by
/// <c>live.tap.read</c>. Bounded: packets beyond <see cref="MaxPackets"/> waiting to be read, or beyond <see cref="MaxBytes"/> of
/// estimated memory, are dropped here and counted, so a tap nobody reads cannot grow Atlas without limit. Thread-safe.
/// </summary>
internal sealed class PacketToolTraffic
{
    /// <summary>Packet lines kept until read.</summary>
    public const int MaxPackets = 20_000;

    /// <summary>Estimated memory of the packet lines kept until read (a 4096-byte payload is 8192 hex characters, 16 KiB as a string).</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>What a packet line costs besides its hex text (the object, its properties and strings).</summary>
    private const int LineOverheadBytes = 512;

    /// <summary>Late console lines kept until read.</summary>
    public const int MaxConsoleLines = 1_000;

    private readonly Lock _gate = new();
    private readonly Queue<(JsonObject Line, long Cost)> _packets = new();
    private readonly Queue<JsonObject> _consoleLines = new();
    private JsonObject? _tapEnd;
    private long _packetBytes;
    private long _droppedHere;
    private long _received;

    /// <summary>Take one tool line from the reader loop; false when <paramref name="op"/> is not a tool stream op.</summary>
    public bool Accept(string op, JsonObject line)
    {
        lock (_gate)
        {
            switch (op)
            {
                case "packet":
                    _received++;
                    long cost = LineOverheadBytes + (line["hex"] is JsonValue hex && hex.TryGetValue(out string? text) ? 2L * text.Length : 0L);
                    if (_packets.Count >= MaxPackets || _packetBytes + cost > MaxBytes)
                    {
                        _droppedHere++;
                    }
                    else
                    {
                        _packets.Enqueue((line, cost));
                        _packetBytes += cost;
                    }

                    return true;
                case "tapEnd":
                    _tapEnd = line;
                    return true;
                case "consoleLine":
                    if (_consoleLines.Count >= MaxConsoleLines)
                    {
                        _consoleLines.Dequeue();
                    }

                    _consoleLines.Enqueue(line);
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>A new tap starts: forget the previous tap's leftovers and end notice.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _packets.Clear();
            _packetBytes = 0;
            _tapEnd = null;
            _droppedHere = 0;
            _received = 0;
        }
    }

    /// <summary>Whether packets are waiting or the tap's end has arrived.</summary>
    public bool HasNews
    {
        get
        {
            lock (_gate)
            {
                return _packets.Count > 0 || _tapEnd is not null;
            }
        }
    }

    /// <summary>
    /// Up to <paramref name="max"/> packets, oldest first, removed; the tap's end notice once every packet before it was read; and the
    /// counters. Also hands out the late console lines.
    /// </summary>
    public JsonObject Drain(int max)
    {
        lock (_gate)
        {
            var packets = new JsonArray();
            while (packets.Count < max && _packets.TryDequeue(out (JsonObject Line, long Cost) packet))
            {
                _packetBytes -= packet.Cost;
                packets.Add(packet.Line);
            }

            var console = new JsonArray();
            while (_consoleLines.TryDequeue(out JsonObject? line))
            {
                console.Add(line["line"]?.DeepClone());
            }

            bool done = _tapEnd is not null && _packets.Count == 0;
            return new JsonObject
            {
                ["packets"] = packets,
                ["pending"] = _packets.Count,
                ["received"] = _received,
                ["droppedByAtlas"] = _droppedHere,
                ["ended"] = done ? _tapEnd!.DeepClone() : null,
                ["consoleLines"] = console,
            };
        }
    }
}
