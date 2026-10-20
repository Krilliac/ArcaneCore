using System.Buffers;
using System.Text.Json;
using System.Threading.Channels;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>One packet a tap passed: copied out of the session's buffer when it was offered.</summary>
internal readonly record struct TapPacket(long Seq, long UnixMs, bool Inbound, ushort Opcode, int Length, byte[] Bytes, bool Injected);

/// <summary>
/// A map-feed connection's watch on one session's packets (docs/areas/map-feed.md, "Packet tools"). The session offers every packet on
/// whatever thread moves it (its reader, the world thread, a sender); <see cref="Offer"/> filters, copies at most <see cref="MaxBytes"/>
/// payload bytes and tries a bounded queue, and a packet that does not fit is counted in <see cref="Dropped"/>: a slow tool loses packets,
/// the server never waits for it. The connection's pump drains the queue into <c>packet</c> lines and ends with one <c>tapEnd</c>.
/// </summary>
internal sealed class FeedPacketTap : ISessionPacketTap
{
    /// <summary>Packets that may wait for the connection's pump.</summary>
    internal const int QueueCapacity = 256;

    private readonly Channel<TapPacket> _queue;
    private readonly TapDirections _directions;
    private readonly ushort[]? _opcodes;
    private readonly long _maxPackets;
    private long _matched;
    private long _dropped;
    private string? _reason;

    public FeedPacketTap(long id, ToolTarget target, TapDirections directions, ushort[]? opcodes, int maxPackets, int maxBytes, int capacity = QueueCapacity)
    {
        Id = id;
        Target = target;
        _directions = directions;
        _opcodes = opcodes;
        _maxPackets = maxPackets;
        MaxBytes = maxBytes;
        _queue = Channel.CreateBounded<TapPacket>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>The id of the <c>tap</c> request that started it.</summary>
    public long Id { get; }

    /// <summary>The session watched.</summary>
    public ToolTarget Target { get; }

    /// <summary>The most payload bytes copied per packet.</summary>
    public int MaxBytes { get; }

    /// <summary>Packets that matched but did not fit the queue (or arrived as the tap ended).</summary>
    public long Dropped => Volatile.Read(ref _dropped);

    /// <summary>Why the tap ended (count, time, stopped, closed), or null while it runs.</summary>
    public string? Reason => Volatile.Read(ref _reason);

    /// <summary>The queue the connection's pump reads.</summary>
    public ChannelReader<TapPacket> Reader => _queue.Reader;

    /// <inheritdoc />
    public void Offer(bool inbound, WorldOpcode opcode, ReadOnlySpan<byte> payload, bool injected)
    {
        if ((_directions & (inbound ? TapDirections.In : TapDirections.Out)) == 0 || Volatile.Read(ref _reason) is not null)
        {
            return;
        }

        if (_opcodes is { } filter && Array.IndexOf(filter, (ushort)opcode) < 0)
        {
            return;
        }

        long seq = Interlocked.Increment(ref _matched);
        if (seq > _maxPackets)
        {
            return;
        }

        byte[] bytes = payload.Length == 0 || MaxBytes == 0 ? [] : payload[..Math.Min(payload.Length, MaxBytes)].ToArray();
        var packet = new TapPacket(seq, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), inbound, (ushort)opcode, payload.Length, bytes, injected);
        if (!_queue.Writer.TryWrite(packet))
        {
            Interlocked.Increment(ref _dropped);
        }

        if (seq == _maxPackets)
        {
            End("count");
        }
    }

    /// <inheritdoc />
    public void SessionClosed() => End("closed");

    /// <summary>End the tap (the first reason wins); the pump drains what is queued and reports.</summary>
    public void End(string reason)
    {
        Interlocked.CompareExchange(ref _reason, reason, null);
        _queue.Writer.TryComplete();
    }
}

/// <summary>The packet tools' lines (docs/areas/map-feed.md, "Packet tools"). Each returns a finished line, newline included. Any thread.</summary>
internal static class PacketToolsJson
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    /// <summary>A refused tool request: <c>{"op":"result","id","ok":false,"kind","code","message"}</c>.</summary>
    public static byte[] Refused(long id, string kind, string code, string message)
        => Line(writer =>
        {
            Head(writer, id, ok: false, kind);
            writer.WriteString("code"u8, code);
            writer.WriteString("message"u8, message);
        });

    /// <summary>A console answer: the reply lines without the console prefix, whether the command settled, and its failure if any.</summary>
    public static byte[] Console(long id, IReadOnlyList<string> lines, bool settled, string? failure)
        => Line(writer =>
        {
            Head(writer, id, ok: true, "console");
            writer.WriteStartArray("lines"u8);
            foreach (string line in lines)
            {
                writer.WriteStringValue(line);
            }

            writer.WriteEndArray();
            writer.WriteBoolean("settled"u8, settled);
            if (failure is not null)
            {
                writer.WriteString("failed"u8, failure);
            }
        });

    /// <summary>A line a console command printed after its <c>result</c>.</summary>
    public static byte[] ConsoleLine(long id, string line)
        => Line(writer =>
        {
            writer.WriteString("op"u8, "consoleLine"u8);
            writer.WriteNumber("id"u8, id);
            writer.WriteString("line"u8, line);
        });

    /// <summary>An injection's answer: what the dispatcher did with it.</summary>
    public static byte[] Injected(long id, ToolTarget target, uint opcode, int length, InjectVerdict verdict, bool worldHandler)
        => Line(writer =>
        {
            Head(writer, id, ok: true, "inject");
            WriteTarget(writer, target);
            WriteOpcode(writer, opcode, length);
            writer.WriteString("handler"u8, worldHandler ? "world"u8 : "session"u8);
            writer.WriteString("verdict"u8, verdict switch
            {
                InjectVerdict.Queued => "queued",
                InjectVerdict.Handled => "handled",
                _ => "disconnected",
            });
        });

    /// <summary>A send's answer: the packet is queued to the client.</summary>
    public static byte[] Sent(long id, ToolTarget target, uint opcode, int length)
        => Line(writer =>
        {
            Head(writer, id, ok: true, "send");
            WriteTarget(writer, target);
            WriteOpcode(writer, opcode, length);
        });

    /// <summary>A tap's start: <c>tap</c> is the id the <c>packet</c> and <c>tapEnd</c> lines carry.</summary>
    public static byte[] TapStarted(long id, FeedPacketTap tap, int maxPackets, int seconds)
        => Line(writer =>
        {
            Head(writer, id, ok: true, "tap");
            writer.WriteNumber("tap"u8, tap.Id);
            WriteTarget(writer, tap.Target);
            writer.WriteNumber("maxPackets"u8, maxPackets);
            writer.WriteNumber("seconds"u8, seconds);
            writer.WriteNumber("bytes"u8, tap.MaxBytes);
        });

    /// <summary>An <c>untap</c>'s answer.</summary>
    public static byte[] Untapped(long id, long tapId)
        => Line(writer =>
        {
            Head(writer, id, ok: true, "untap");
            writer.WriteNumber("tap"u8, tapId);
        });

    /// <summary>One tapped packet.</summary>
    public static byte[] Packet(long tapId, in TapPacket packet)
    {
        TapPacket p = packet;
        return Line(writer =>
        {
            writer.WriteString("op"u8, "packet"u8);
            writer.WriteNumber("tap"u8, tapId);
            writer.WriteNumber("seq"u8, p.Seq);
            writer.WriteNumber("t"u8, p.UnixMs);
            writer.WriteString("dir"u8, p.Inbound ? "in"u8 : "out"u8);
            writer.WriteNumber("opcode"u8, p.Opcode);
            writer.WriteString("name"u8, PacketToolOpcodes.Name(p.Opcode));
            writer.WriteNumber("len"u8, p.Length);
            writer.WriteString("hex"u8, Convert.ToHexString(p.Bytes));
            if (p.Bytes.Length < p.Length)
            {
                writer.WriteBoolean("cut"u8, true);
            }

            if (p.Injected)
            {
                writer.WriteBoolean("inj"u8, true);
            }
        });
    }

    /// <summary>A tap's end: why, how many packets were sent, how many were lost.</summary>
    public static byte[] TapEnd(long tapId, string reason, long packets, long dropped)
        => Line(writer =>
        {
            writer.WriteString("op"u8, "tapEnd"u8);
            writer.WriteNumber("tap"u8, tapId);
            writer.WriteString("reason"u8, reason);
            writer.WriteNumber("packets"u8, packets);
            writer.WriteNumber("dropped"u8, dropped);
        });

    private static void Head(Utf8JsonWriter writer, long id, bool ok, string kind)
    {
        writer.WriteString("op"u8, "result"u8);
        writer.WriteNumber("id"u8, id);
        writer.WriteBoolean("ok"u8, ok);
        writer.WriteString("kind"u8, kind);
    }

    private static void WriteTarget(Utf8JsonWriter writer, ToolTarget target)
    {
        writer.WriteStartObject("target"u8);
        writer.WriteString("name"u8, target.Name);
        writer.WriteString("guid"u8, target.Guid.ToString("X16", System.Globalization.CultureInfo.InvariantCulture));
        writer.WriteBoolean("bot"u8, target.Bot);
        writer.WriteEndObject();
    }

    private static void WriteOpcode(Utf8JsonWriter writer, uint opcode, int length)
    {
        writer.WriteNumber("opcode"u8, opcode);
        writer.WriteString("name"u8, PacketToolOpcodes.Name(opcode));
        writer.WriteNumber("length"u8, length);
    }

    private static byte[] Line(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return MapFeedJson.Finish(buffer);
    }
}
