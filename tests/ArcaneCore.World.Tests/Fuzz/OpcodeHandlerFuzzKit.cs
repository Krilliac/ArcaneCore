using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Tests.Fuzz;

/// <summary>Run parameters of the opcode handler fuzz, from the environment (ARCANECORE_HANDLER_FUZZ_*).</summary>
internal sealed record HandlerFuzzSettings(int RandomIterations, int BaseSeed, string? OpcodeFilter)
{
    internal const int DefaultRandomIterations = 8;
    internal const int DefaultBaseSeed = 0x5EED_F00D;

    public static HandlerFuzzSettings FromEnvironment()
    {
        int iterations = int.TryParse(Environment.GetEnvironmentVariable("ARCANECORE_HANDLER_FUZZ_ITERATIONS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0
            ? i : DefaultRandomIterations;
        int seed = int.TryParse(Environment.GetEnvironmentVariable("ARCANECORE_HANDLER_FUZZ_SEED"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int s)
            ? s : DefaultBaseSeed;
        string? filter = Environment.GetEnvironmentVariable("ARCANECORE_HANDLER_FUZZ_OPCODE");
        return new HandlerFuzzSettings(iterations, seed, string.IsNullOrWhiteSpace(filter) ? null : filter.Trim());
    }

    /// <summary>True when <paramref name="opcode"/> is selected: no filter, or the filter names it (wire name as printed, the enum name, or the number).</summary>
    public bool Selects(WorldOpcode opcode)
    {
        if (OpcodeFilter is null)
        {
            return true;
        }

        string wire = WorldOpcodeNames.GetName(opcode);
        return string.Equals(OpcodeFilter, wire, StringComparison.OrdinalIgnoreCase)
            || string.Equals(OpcodeFilter, opcode.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(OpcodeFilter, ((ushort)opcode).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}

/// <summary>One field of a handler's payload layout, learnt from the reads the handler itself makes.</summary>
internal readonly record struct HandlerFuzzField(PacketReadKind Kind, int Offset, int Width);

/// <summary>One payload to feed a handler, with the human description of how it was made.</summary>
internal sealed record HandlerFuzzCase(string Description, byte[] Payload);

/// <summary>
/// The case stream of one opcode: it learns the payload layout from the handler's own reads (<see cref="PacketReadTrace"/>), then
/// yields the deterministic mutations. Single-threaded: the runner calls <see cref="TryNext"/>, runs the handler, then
/// <see cref="Report"/> with what happened.
/// </summary>
internal sealed class HandlerFuzzCaseSource
{
    private const int MaxLearnSteps = 64;
    private const int MaxProbeLength = 64;
    private const int MaxFieldBytes = 4096;

    private enum Phase { Learn, Probe, Mutate, Done }

    private enum Issued { None, Learn, Probe, Mutation }

    private readonly Random _random;
    private readonly int _randomIterations;
    private readonly IReadOnlyList<(string Name, ulong Value)> _guids;
    private readonly Queue<HandlerFuzzCase> _queue = new();
    private Phase _phase = Phase.Learn;
    private Issued _issued = Issued.None;
    private byte[] _payload = [];
    private int _learnSteps;
    private int _probeLength;
    private int _zeroRunTo;
    private int _issuedLength;
    private readonly SortedSet<int> _accepted = [];
    private HandlerFuzzField[] _fields = [];

    public HandlerFuzzCaseSource(WorldOpcode opcode, int baseSeed, int randomIterations, IReadOnlyList<(string Name, ulong Value)> guids)
    {
        Opcode = opcode;
        _random = new Random(unchecked(baseSeed ^ ((int)opcode * unchecked((int)0x9E3779B1))));
        _randomIterations = randomIterations;
        _guids = guids;
    }

    public WorldOpcode Opcode { get; }

    /// <summary>Cases handed out so far (the per-opcode case index of the next case is this value).</summary>
    public int CaseCount { get; private set; }

    /// <summary>True once the layout was learnt from reads; false for a handler that reads its payload directly (byte-level shapes).</summary>
    public bool Traced { get; private set; }

    public bool Finished => _phase == Phase.Done && _queue.Count == 0;

    public IReadOnlyList<HandlerFuzzField> Fields => _fields;

    public int LearnedLength => _payload.Length;

    public bool TryNext(out HandlerFuzzCase fuzzCase)
    {
        if (_queue.Count > 0)
        {
            _issued = Issued.Mutation;
            fuzzCase = _queue.Dequeue();
            CaseCount++;
            return true;
        }

        switch (_phase)
        {
            case Phase.Learn:
                _issued = Issued.Learn;
                _issuedLength = _payload.Length;
                fuzzCase = new HandlerFuzzCase($"learn: layout probe, {_payload.Length} byte(s)", _payload);
                CaseCount++;
                return true;
            case Phase.Probe:
                _issued = Issued.Probe;
                _issuedLength = _probeLength;
                fuzzCase = new HandlerFuzzCase($"learn: {_probeLength} zero byte(s)", new byte[_probeLength]);
                CaseCount++;
                return true;
            default:
                _issued = Issued.None;
                fuzzCase = null!;
                return false;
        }
    }

    /// <summary>What the handler did with the case just handed out.</summary>
    public void Report(List<PacketReadEvent> events, Exception? exception)
    {
        Issued issued = _issued;
        _issued = Issued.None;
        bool broke = exception is not null && !HandlerFuzzKit.IsControlledMalformed(exception);
        if (exception is null && issued is Issued.Learn or Issued.Probe)
        {
            _accepted.Add(_issuedLength); // a payload length the handler took without objecting
        }

        if (issued == Issued.Learn)
        {
            if (events.Count == 0 && !broke && _payload.Length == 0)
            {
                _zeroRunTo = 0;
                _phase = Phase.Probe;
                _probeLength = 1;
                return;
            }

            LearnStep(events, broke);
        }
        else if (issued == Issued.Probe)
        {
            _zeroRunTo = _probeLength;
            if (events.Count > 0 || broke)
            {
                _payload = new byte[_probeLength];
                _phase = Phase.Learn;
                return;
            }

            if (++_probeLength > MaxProbeLength)
            {
                BuildByteLevel();
            }
        }
    }

    private void LearnStep(List<PacketReadEvent> events, bool broke)
    {
        int failedIndex = events.FindIndex(e => !e.Ok);
        if (!broke && failedIndex >= 0 && _learnSteps < MaxLearnSteps)
        {
            PacketReadEvent failed = events[failedIndex];
            bool lengthFailure = failed.Width >= 0 && failed.Offset + failed.Width > _payload.Length;
            if (lengthFailure && failed.Width <= MaxFieldBytes)
            {
                int missing = Math.Max(1, failed.Offset + failed.Width - _payload.Length);
                _payload = [.. _payload, .. new byte[missing]];
                _learnSteps++;
                return; // stay in the Learn phase with the longer payload
            }
        }

        _fields = [.. events.Where(e => e.Ok).Select(e => new HandlerFuzzField(e.Kind, e.Offset, e.Width))];
        Traced = _fields.Length > 0;
        if (Traced)
        {
            BuildMutations();
        }
        else
        {
            BuildByteLevel();
        }
    }

    // --- mutation generation ---------------------------------------------------------------

    private void Enqueue(string description, byte[] payload) => _queue.Enqueue(new HandlerFuzzCase(description, payload));

    private void BuildByteLevel()
    {
        Traced = false;
        _fields = [];
        // Zero payloads of every length 0..64: the layout probe already ran the lengths up to _zeroRunTo.
        for (int length = _zeroRunTo + 1; length <= MaxProbeLength; length++)
        {
            Enqueue($"byte-level: {length} zero byte(s)", new byte[length]);
        }

        // The value splats go on a zero buffer of a length the handler accepted (most of these handlers demand an exact length, so a
        // fixed 32-byte buffer would be refused up front); a handler that takes every length, or none, gets the 32-byte buffer. A handler
        // whose body is empty by contract (only length 0 accepted) has nothing to splat.
        int[] accepted = [.. _accepted.Where(l => l > 0)];
        int[] lengths = _accepted.Count == 0 || accepted.Length > 8 ? [32] : [.. accepted.Take(3)];
        foreach (int length in lengths)
        {
            BuildSplats(new byte[length]);
        }

        _payload = lengths.Length > 0 && (_accepted.Count == 0 || accepted.Length > 0) ? new byte[lengths[0]] : [];
        BuildOversized(_payload);
        BuildRandom(_payload);
        _phase = Phase.Done;
    }

    private void BuildSplats(byte[] zeros)
    {
        ulong[] guidValues = [.. _guids.Select(g => g.Value).Distinct()];
        for (int offset = 0; offset < zeros.Length; offset++)
        {
            foreach (byte v in HandlerFuzzKit.ByteValues)
            {
                Enqueue($"byte-level: u8 {v:X2} at {offset} of {zeros.Length}", HandlerFuzzKit.Splice(zeros, offset, 1, [v]));
            }

            if (offset + 2 <= zeros.Length)
            {
                foreach (ushort v in HandlerFuzzKit.UInt16Values)
                {
                    Enqueue($"byte-level: u16 {v:X4} at {offset} of {zeros.Length}", HandlerFuzzKit.Splice(zeros, offset, 2, HandlerFuzzKit.Le(v, 2)));
                }
            }

            if (offset + 4 <= zeros.Length)
            {
                foreach (uint v in HandlerFuzzKit.UInt32Values)
                {
                    Enqueue($"byte-level: u32 {v:X8} at {offset} of {zeros.Length}", HandlerFuzzKit.Splice(zeros, offset, 4, HandlerFuzzKit.Le(v, 4)));
                }
            }

            if (offset + 8 <= zeros.Length)
            {
                foreach (ulong v in guidValues)
                {
                    Enqueue($"byte-level: u64 {v:X16} at {offset} of {zeros.Length}", HandlerFuzzKit.Splice(zeros, offset, 8, HandlerFuzzKit.Le(v, 8)));
                }
            }
        }
    }

    private void BuildOversized(byte[] p)
    {
        byte[] one = [.. p, (byte)_random.Next(256)];
        Enqueue("oversized: payload + 1 byte", one);
        Enqueue("oversized: payload + 64 random bytes", [.. p, .. RandomBytes(64)]);
        if (p.Length < 0x2000)
        {
            Enqueue("oversized: payload padded with random bytes to 0x2000", [.. p, .. RandomBytes(0x2000 - p.Length)]);
        }
    }

    private void BuildMutations()
    {
        byte[] p = _payload;
        HandlerFuzzField[] fields = _fields;

        Enqueue("empty payload", []);

        // Truncation at every field boundary and one byte into each multi-byte field.
        var cuts = new SortedSet<int>();
        foreach (HandlerFuzzField f in fields)
        {
            cuts.Add(f.Offset);
            if (f.Width > 1)
            {
                cuts.Add(f.Offset + 1);
            }
        }

        cuts.Add(p.Length - 1);
        foreach (int cut in cuts)
        {
            if (cut > 0 && cut < p.Length)
            {
                Enqueue($"truncated to {cut} of {p.Length} byte(s)", p[..cut]);
            }
        }

        BuildOversized(p);

        for (int i = 0; i < fields.Length; i++)
        {
            BuildFieldExtremes(p, fields[i], i);
        }

        BuildRandom(p);
        _phase = Phase.Done;
    }

    private void BuildFieldExtremes(byte[] p, HandlerFuzzField f, int index)
    {
        string label = $"field {index} {f.Kind}@{f.Offset}";
        switch (f.Kind)
        {
            case PacketReadKind.Byte:
            case PacketReadKind.ByteCount:
                foreach (byte v in HandlerFuzzKit.ByteValues)
                {
                    Enqueue($"{label} = {v:X2}", HandlerFuzzKit.Splice(p, f.Offset, f.Width, [v]));
                }

                if (f.Kind == PacketReadKind.ByteCount)
                {
                    Enqueue($"{label} = 1 with 64 random bytes of body", [.. HandlerFuzzKit.Splice(p, f.Offset, f.Width, [1]), .. RandomBytes(64)]);
                    Enqueue($"{label} = 3 with 64 random bytes of body", [.. HandlerFuzzKit.Splice(p, f.Offset, f.Width, [3]), .. RandomBytes(64)]);
                }

                break;
            case PacketReadKind.UInt16:
                foreach (ushort v in HandlerFuzzKit.UInt16Values)
                {
                    Enqueue($"{label} = {v:X4}", HandlerFuzzKit.Splice(p, f.Offset, f.Width, HandlerFuzzKit.Le(v, 2)));
                }

                break;
            case PacketReadKind.UInt32:
            case PacketReadKind.Count:
                foreach (uint v in HandlerFuzzKit.UInt32Values)
                {
                    Enqueue($"{label} = {v:X8}", HandlerFuzzKit.Splice(p, f.Offset, f.Width, HandlerFuzzKit.Le(v, 4)));
                }

                if (f.Kind == PacketReadKind.Count)
                {
                    Enqueue($"{label} = 1 with 64 random bytes of body", [.. HandlerFuzzKit.Splice(p, f.Offset, f.Width, HandlerFuzzKit.Le(1u, 4)), .. RandomBytes(64)]);
                    Enqueue($"{label} = 3 with 64 random bytes of body", [.. HandlerFuzzKit.Splice(p, f.Offset, f.Width, HandlerFuzzKit.Le(3u, 4)), .. RandomBytes(64)]);
                }

                break;
            case PacketReadKind.UInt64:
                foreach ((string name, ulong v) in _guids)
                {
                    Enqueue($"{label} = {name} guid", HandlerFuzzKit.Splice(p, f.Offset, f.Width, HandlerFuzzKit.Le(v, 8)));
                }

                break;
            case PacketReadKind.PackedGuid:
                foreach ((string name, ulong v) in _guids)
                {
                    Enqueue($"{label} = {name} guid (packed)", HandlerFuzzKit.Splice(p, f.Offset, f.Width, HandlerFuzzKit.Packed(v)));
                }

                break;
            case PacketReadKind.Single:
            case PacketReadKind.FiniteSingle:
                foreach (float v in HandlerFuzzKit.SingleValues)
                {
                    Enqueue($"{label} = {v.ToString("R", CultureInfo.InvariantCulture)}", HandlerFuzzKit.Splice(p, f.Offset, f.Width, BitConverter.GetBytes(v)));
                }

                break;
            case PacketReadKind.CString:
                foreach ((string text, byte[] bytes) in HandlerFuzzKit.StringValues)
                {
                    Enqueue($"{label} = {text}", HandlerFuzzKit.Splice(p, f.Offset, f.Width, bytes));
                }

                Enqueue($"{label} = missing terminator", [.. p[..f.Offset], .. Enumerable.Repeat((byte)'A', Math.Max(1, p.Length - f.Offset))]);
                break;
            case PacketReadKind.Bytes:
            case PacketReadKind.Skip:
                if (f.Width > 0)
                {
                    Enqueue($"{label} = {f.Width} x FF", HandlerFuzzKit.Splice(p, f.Offset, f.Width, Enumerable.Repeat((byte)0xFF, f.Width).ToArray()));
                }

                break;
            case PacketReadKind.ToEnd:
                break;
        }
    }

    private void BuildRandom(byte[] p)
    {
        for (int n = 0; n < _randomIterations; n++)
        {
            int op = _random.Next(6);
            byte[] data = (byte[])p.Clone();
            switch (op)
            {
                case 0 when data.Length > 0:
                {
                    int flips = _random.Next(1, 9);
                    var where = new List<string>();
                    for (int k = 0; k < flips; k++)
                    {
                        int bit = _random.Next(data.Length * 8);
                        data[bit / 8] ^= (byte)(1 << (bit % 8));
                        where.Add(bit.ToString(CultureInfo.InvariantCulture));
                    }

                    Enqueue($"random {n}: flip {flips} bit(s) [{string.Join(",", where)}]", data);
                    break;
                }

                case 1 when data.Length > 0:
                {
                    int at = _random.Next(data.Length);
                    byte v = (byte)_random.Next(256);
                    data[at] = v;
                    Enqueue($"random {n}: byte {at} = {v:X2}", data);
                    break;
                }

                case 2:
                {
                    int at = _random.Next(data.Length + 1);
                    byte v = (byte)_random.Next(256);
                    Enqueue($"random {n}: insert {v:X2} at {at}", [.. data[..at], v, .. data[at..]]);
                    break;
                }

                case 3 when data.Length > 0:
                {
                    int at = _random.Next(data.Length);
                    Enqueue($"random {n}: delete byte {at}", [.. data[..at], .. data[(at + 1)..]]);
                    break;
                }

                case 4 when _fields.Length > 0:
                {
                    int index = _random.Next(_fields.Length);
                    HandlerFuzzField f = _fields[index];
                    int width = f.Kind is PacketReadKind.CString or PacketReadKind.ToEnd ? _random.Next(1, 17) : Math.Max(f.Width, 0);
                    Enqueue($"random {n}: field {index} {f.Kind}@{f.Offset} replaced by {width} random byte(s)", HandlerFuzzKit.Splice(data, f.Offset, f.Width, RandomBytes(width)));
                    break;
                }

                default:
                {
                    int length = _random.Next(257);
                    Enqueue($"random {n}: fully random buffer of {length} byte(s)", RandomBytes(length));
                    break;
                }
            }
        }
    }

    private byte[] RandomBytes(int length)
    {
        byte[] b = new byte[length];
        _random.NextBytes(b);
        return b;
    }
}

/// <summary>A broken invariant of the fuzzed player or session (not an exception).</summary>
internal readonly record struct HandlerFuzzBreak(string Name, string Detail);

/// <summary>Money and the multiset of (item entry, stack count) over every player-owned slot, taken before a handler runs.</summary>
internal sealed record HandlerFuzzInventory(uint Money, Dictionary<(uint Entry, uint Count), int> Stacks)
{
    public static HandlerFuzzInventory Take(Player player)
    {
        var stacks = new Dictionary<(uint Entry, uint Count), int>();
        foreach (Item item in player.Inventory.AllItems)
        {
            var key = (item.Entry, item.Count);
            stacks[key] = stacks.GetValueOrDefault(key) + 1;
        }

        return new HandlerFuzzInventory(player.Money, stacks);
    }

    /// <summary>Null when the multisets are equal, else the difference.</summary>
    public string? StackDiff(HandlerFuzzInventory after)
    {
        var parts = new List<string>();
        foreach (((uint entry, uint count), int n) in Stacks)
        {
            int now = after.Stacks.GetValueOrDefault((entry, count));
            if (now != n)
            {
                parts.Add($"item {entry} x{count}: {n} -> {now}");
            }
        }

        foreach (((uint entry, uint count), int n) in after.Stacks)
        {
            if (!Stacks.ContainsKey((entry, count)))
            {
                parts.Add($"item {entry} x{count}: 0 -> {n}");
            }
        }

        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}

/// <summary>One finding of the run (an exception that is not the controlled outcome, or a broken invariant), deduplicated by key.</summary>
internal sealed class HandlerFuzzFinding
{
    public required WorldOpcode Opcode { get; init; }

    public required string Kind { get; init; }

    public required string Frame { get; init; }

    public required string Detail { get; init; }

    public required string Mutation { get; init; }

    public required int CaseIndex { get; init; }

    public required byte[] Payload { get; init; }

    public int Occurrences { get; set; } = 1;

    public string Key => $"{(ushort)Opcode}|{Kind}|{Frame}";
}

/// <summary>Per-opcode counters printed in the coverage summary.</summary>
internal sealed class HandlerFuzzStats(WorldOpcode opcode, string kind)
{
    public WorldOpcode Opcode { get; } = opcode;

    public string Kind { get; set; } = kind; // world | session | stripped

    public int Cases { get; set; }

    public bool Traced { get; set; }

    public int Fields { get; set; }

    public int Kicks { get; set; }

    public int Recoveries { get; set; }

    public bool RecoveryCapped { get; set; }

    public int TransfersCompleted { get; set; }

    public int TransfersStuck { get; set; }

    public double Millis { get; set; }

    public string? FirstKick { get; set; }
}

/// <summary>Static helpers of the opcode handler fuzz: encodings, the allowed-outcome rule, invariants, formatting.</summary>
internal static class HandlerFuzzKit
{
    internal const uint FixtureCreatureEntry = 910100;
    internal const uint FixtureGameObjectEntry = 910200;
    internal const uint FixtureSpawn = 910300;

    internal static readonly byte[] ByteValues = [0, 1, 0x7F, 0x80, 0xFF];

    internal static readonly ushort[] UInt16Values = [0, 1, 0x7FFF, 0x8000, 0xFFFF];

    internal static readonly uint[] UInt32Values = [0, 1, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFE, 0xFFFFFFFF, 1_000_000];

    internal static readonly float[] SingleValues =
    [
        float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue, -float.MaxValue, 1e30f, -1e30f, float.Epsilon, -0f,
    ];

    internal static readonly (string Text, byte[] Bytes)[] StringValues =
    [
        ("empty string", [0]),
        ("300 x 'A'", [.. Enumerable.Repeat((byte)'A', 300), 0]),
        ("0x2000 x 'A'", [.. Enumerable.Repeat((byte)'A', 0x2000), 0]),
        ("format/link string", [.. "%s%s%n%x|Hitem:1:0:0:0|h[x]|h|r|c"u8, 0]),
        ("invalid UTF-8", [0xFF, 0xFE, 0xC0, 0]),
    ];

    /// <summary>
    /// The only outcome the production sessions treat as a client fault: the PacketReader's own
    /// <see cref="ArgumentOutOfRangeException"/> carrying <see cref="MalformedPacket.ParamName"/>. <see cref="MalformedPacket.Is"/> accepts any
    /// ArgumentOutOfRangeException, which would hide a server bug (a List indexer in a handler) as a client fault; this is stricter.
    /// </summary>
    internal static bool IsControlledMalformed(Exception exception)
        => exception is ArgumentOutOfRangeException { ParamName: MalformedPacket.ParamName };

    internal static byte[] Splice(byte[] source, int offset, int width, byte[] replacement)
    {
        int start = Math.Clamp(offset, 0, source.Length);
        int end = Math.Clamp(offset + Math.Max(width, 0), start, source.Length);
        return [.. source[..start], .. replacement, .. source[end..]];
    }

    internal static byte[] Le(ulong value, int width)
    {
        byte[] b = new byte[width];
        for (int i = 0; i < width; i++)
        {
            b[i] = (byte)(value >> (8 * i));
        }

        return b;
    }

    internal static byte[] Packed(ulong value)
    {
        byte mask = 0;
        var bytes = new List<byte>(9) { 0 };
        for (int i = 0; i < 8; i++)
        {
            byte b = (byte)(value >> (8 * i));
            if (b != 0)
            {
                mask |= (byte)(1 << i);
                bytes.Add(b);
            }
        }

        bytes[0] = mask;
        return [.. bytes];
    }

    /// <summary>A C# byte array literal of the payload (truncated past 256 bytes, with the real length noted).</summary>
    internal static string PayloadLiteral(byte[] payload)
    {
        const int Max = 256;
        var sb = new StringBuilder("new byte[] { ");
        for (int i = 0; i < Math.Min(payload.Length, Max); i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            sb.Append("0x").Append(payload[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        sb.Append(" }");
        if (payload.Length > Max)
        {
            sb.Append(CultureInfo.InvariantCulture, $" /* truncated: {Max} of {payload.Length} bytes */");
        }

        return sb.ToString();
    }

    // --- exception reporting ---------------------------------------------------------------

    private static readonly Regex SrcFrame = new(@"at (?<method>.+?) in (?<path>.*[\\/]src[\\/].+?):line (?<line>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The first stack frame inside src/ ("src/Area/File.cs:123"), else the first frame text, else "-".</summary>
    internal static string FirstSourceFrame(Exception exception)
    {
        string? firstAny = null;
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            foreach (string raw in (e.StackTrace ?? string.Empty).Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                firstAny ??= line;
                Match m = SrcFrame.Match(line);
                if (m.Success)
                {
                    string path = m.Groups["path"].Value.Replace('\\', '/');
                    int at = path.LastIndexOf("/src/", StringComparison.Ordinal);
                    return $"{(at >= 0 ? path[(at + 1)..] : path)}:{m.Groups["line"].Value}";
                }
            }
        }

        return firstAny ?? "-";
    }

    internal static string TopFrames(Exception exception, int count)
    {
        string[] lines = (exception.StackTrace ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string text = $"{exception.GetType().FullName}: {exception.Message}";
        if (exception is ArgumentOutOfRangeException a)
        {
            text += $" [ParamName={a.ParamName ?? "<null>"}]";
        }

        for (int i = 0; i < Math.Min(count, lines.Length); i++)
        {
            text += "\n        " + lines[i];
        }

        return text;
    }

    // --- session state ---------------------------------------------------------------------

    private static readonly FieldInfo? KickSource = typeof(WorldSession).GetField("_kick", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>True once the session was kicked (its kick token is cancelled), or a kick reason was recorded.</summary>
    internal static bool IsKicked(WorldSession session)
        => session.KickReason is not null
            || (KickSource?.GetValue(session) is CancellationTokenSource source && source.IsCancellationRequested)
            || session.State == SessionState.Closed;

    // --- invariants ------------------------------------------------------------------------

    /// <summary>
    /// The player invariants checked after every invocation. <paramref name="mapChecks"/> is false when the player is known to be out of
    /// a map (kicked, left the world, transfer that could not complete): only the position and health values are then checked.
    /// </summary>
    internal static HandlerFuzzInventory CheckPlayer(Player player, HandlerFuzzInventory before, bool mayChange, bool mapChecks, List<HandlerFuzzBreak> into)
    {
        float x = player.X, y = player.Y, z = player.Z, o = player.Orientation;
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || !float.IsFinite(o))
        {
            into.Add(new HandlerFuzzBreak("position-not-finite", $"X={x} Y={y} Z={z} O={o}"));
        }
        else if (MathF.Abs(x) > 17_100f || MathF.Abs(y) > 17_100f || MathF.Abs(z) > 100_000f)
        {
            into.Add(new HandlerFuzzBreak("position-out-of-range", $"X={x} Y={y} Z={z}"));
        }

        if (mapChecks)
        {
            if (player.Map is null)
            {
                into.Add(new HandlerFuzzBreak("map-null", $"player.Map is null (MapId={player.MapId})"));
            }
            else if (player.Map.MapId != player.MapId)
            {
                into.Add(new HandlerFuzzBreak("map-id-mismatch", $"player.Map.MapId={player.Map.MapId} but player.MapId={player.MapId}"));
            }
        }

        uint health = player.Health, max = player.MaxHealth;
        DeathState death = player.Combat.DeathState;
        bool ghost = (player.Flags & PlayerFlags.Ghost) != 0;
        if (max == 0)
        {
            into.Add(new HandlerFuzzBreak("max-health-zero", "MaxHealth == 0"));
        }

        if (health > max)
        {
            into.Add(new HandlerFuzzBreak("health-above-max", $"Health={health} MaxHealth={max}"));
        }

        if (death == DeathState.Alive && health == 0)
        {
            into.Add(new HandlerFuzzBreak("alive-with-zero-health", $"DeathState=Alive Health=0 IsAlive={player.IsAlive}"));
        }

        // A released ghost keeps 1 health (Unit.IsAlive); only a body (JustDied / Corpse) must be at 0.
        if (death is DeathState.Corpse or DeathState.JustDied && health != 0)
        {
            into.Add(new HandlerFuzzBreak("dead-with-health", $"DeathState={death} Health={health}"));
        }

        if (death == DeathState.Dead && health > 1)
        {
            into.Add(new HandlerFuzzBreak("ghost-health", $"DeathState=Dead Health={health}"));
        }

        if (ghost != (death == DeathState.Dead))
        {
            into.Add(new HandlerFuzzBreak("ghost-flag-mismatch", $"Ghost flag={ghost} DeathState={death}"));
        }

        HandlerFuzzInventory after = HandlerFuzzInventory.Take(player);
        if (!mayChange)
        {
            if (after.Money != before.Money)
            {
                into.Add(new HandlerFuzzBreak("money-changed-outside-set", $"money {before.Money} -> {after.Money} (candidate: opcode is not in MayChangeInventoryOrMoney)"));
            }

            if (before.StackDiff(after) is { } diff)
            {
                into.Add(new HandlerFuzzBreak("inventory-changed-outside-set", $"{diff} (candidate: opcode is not in MayChangeInventoryOrMoney)"));
            }
        }
        else
        {
            if (after.Money > EconomyOptions.MaxMoney)
            {
                into.Add(new HandlerFuzzBreak("money-above-cap", $"money {after.Money} > cap {EconomyOptions.MaxMoney}"));
            }

            foreach (Item item in player.Inventory.AllItems)
            {
                if (item.Count > Math.Max(1u, item.Template.Stackable))
                {
                    into.Add(new HandlerFuzzBreak("stack-above-max", $"item {item.Entry} count {item.Count} > stackable {item.Template.Stackable}"));
                    break;
                }
            }
        }

        return after;
    }

    /// <summary>
    /// Opcodes whose handler may legitimately change money or the player's items, by group. Anything outside the set must leave both
    /// untouched; a difference there is reported as a candidate finding, never silently added here.
    /// </summary>
    internal static readonly HashSet<WorldOpcode> MayChangeInventoryOrMoney =
    [
        // item moves, stack splits, destroy, equip, auto-store, use, open, wrap, ammo: they move or consume items
        WorldOpcode.CmsgSwapInvItem, WorldOpcode.CmsgSwapItem, WorldOpcode.CmsgSplitItem, WorldOpcode.CmsgDestroyitem,
        WorldOpcode.CmsgAutoequipItem, WorldOpcode.CmsgAutoequipItemSlot, WorldOpcode.CmsgAutostoreBagItem, WorldOpcode.CmsgUseItem,
        WorldOpcode.CmsgOpenItem, WorldOpcode.CmsgWrapItem, WorldOpcode.CmsgSetAmmo,

        // loot: items and money move from the loot window to the player
        WorldOpcode.CmsgAutostoreLootItem, WorldOpcode.CmsgLootMoney, WorldOpcode.CmsgLoot, WorldOpcode.CmsgLootRelease,
        WorldOpcode.CmsgLootMasterGive, WorldOpcode.CmsgLootRoll,

        // mail: postage, attachments, cash on delivery, money and item attachments taken
        WorldOpcode.CmsgSendMail, WorldOpcode.CmsgMailTakeItem, WorldOpcode.CmsgMailTakeMoney, WorldOpcode.CmsgMailDelete,
        WorldOpcode.CmsgMailReturnToSender, WorldOpcode.CmsgMailCreateTextItem,

        // trade: the accepted trade swaps items and gold
        WorldOpcode.CmsgAcceptTrade, WorldOpcode.CmsgSetTradeGold, WorldOpcode.CmsgSetTradeItem,

        // vendors: buy, sell, buyback, repair
        WorldOpcode.CmsgBuyItem, WorldOpcode.CmsgBuyItemInSlot, WorldOpcode.CmsgSellItem, WorldOpcode.CmsgBuybackItem, WorldOpcode.CmsgRepairItem,

        // bank and bank slots, stable slots and stabled pets
        WorldOpcode.CmsgAutobankItem, WorldOpcode.CmsgAutostoreBankItem, WorldOpcode.CmsgBuyBankSlot, WorldOpcode.CmsgBuyStableSlot,
        WorldOpcode.CmsgStablePet, WorldOpcode.CmsgUnstablePet, WorldOpcode.CmsgStableSwapPet, WorldOpcode.CmsgStableRevivePet,

        // auction: deposit, bid, cancel, return of items
        WorldOpcode.CmsgAuctionSellItem, WorldOpcode.CmsgAuctionPlaceBid, WorldOpcode.CmsgAuctionRemoveItem,

        // quest givers: reward choice, completion, accept (provided items), abandon (removes quest items)
        WorldOpcode.CmsgQuestgiverChooseReward, WorldOpcode.CmsgQuestgiverCompleteQuest, WorldOpcode.CmsgQuestgiverAcceptQuest,
        WorldOpcode.CmsgQuestlogRemoveQuest, WorldOpcode.CmsgQuestConfirmAccept,

        // trainers, taxis, binder, spirit healer, pet unlearn, talent wipe: they charge money
        WorldOpcode.CmsgTrainerBuySpell, WorldOpcode.CmsgActivatetaxi, WorldOpcode.CmsgActivatetaxiexpress, WorldOpcode.CmsgBinderActivate,
        WorldOpcode.CmsgSpiritHealerActivate, WorldOpcode.CmsgPetUnlearn, WorldOpcode.MsgTalentWipeConfirm,

        // petitions, guild creation, guild emblem (tabard design): charters cost money and are items
        WorldOpcode.CmsgPetitionBuy, WorldOpcode.CmsgTurnInPetition, WorldOpcode.CmsgGuildCreate, WorldOpcode.MsgSaveGuildEmblem,

        // spells: reagents, conjured items, channelled and auto-repeat costs
        WorldOpcode.CmsgCastSpell, WorldOpcode.CmsgCancelCast, WorldOpcode.CmsgCancelChannelling,

        // gossip: paid options (inn, flight, banker)
        WorldOpcode.CmsgGossipSelectOption,
    ];

    /// <summary>
    /// Opcodes whose handler is known to disconnect the session on the fuzz's input, each with the reason. A kick on any other opcode is a
    /// finding. Built from what kicks today: the four guild text handlers (GuildOptions: disconnect a client that sends over-long guild
    /// text, GuildManager.RejectOversized). No movement, anti-cheat or logout handler kicked on any generated payload.
    /// </summary>
    internal static readonly IReadOnlyDictionary<WorldOpcode, string> KickAllowlist = new Dictionary<WorldOpcode, string>
    {
        [WorldOpcode.CmsgGuildCreate] = "guild over-length text kick (GuildManager.RejectOversized)",
        [WorldOpcode.CmsgGuildMotd] = "guild over-length text kick (GuildManager.RejectOversized)",
        [WorldOpcode.CmsgGuildAddRank] = "guild over-length text kick (GuildManager.RejectOversized)",
        [WorldOpcode.CmsgGuildInfoText] = "guild over-length text kick (GuildManager.RejectOversized)",
    };

    /// <summary>
    /// Opcodes allowed to run out of recoveries (the fuzz kicked the primary more than three times). Any other capped opcode is a finding,
    /// because the rest of its cases were skipped.
    /// </summary>
    internal static readonly IReadOnlyDictionary<WorldOpcode, string> RecoveryCapAllowlist = new Dictionary<WorldOpcode, string>
    {
        [WorldOpcode.CmsgGuildMotd] = "guild over-length text kick (GuildManager.RejectOversized): every over-long MOTD disconnects the sender",
    };

    /// <summary>Every break name <see cref="CheckPlayer"/> can emit (the invariant tests show that each one fires).</summary>
    internal static readonly string[] AllBreakNames =
    [
        "position-not-finite", "position-out-of-range", "map-null", "map-id-mismatch", "max-health-zero", "health-above-max",
        "alive-with-zero-health", "dead-with-health", "ghost-health", "ghost-flag-mismatch", "money-changed-outside-set",
        "inventory-changed-outside-set", "money-above-cap", "stack-above-max",
    ];

    /// <summary>Full-run floor: traced handlers (228 at the time of writing, with the fixture creature in place).</summary>
    internal const int MinTracedHandlers = 220;

    /// <summary>Full-run floor: cases executed in the light run (about 74,000 at the time of writing).</summary>
    internal const int MinLightCases = 70_000;

    /// <summary>The nearest creature to the player on its map within a radius (world thread), or null.</summary>
    internal static Creature? NearestCreature(Player player)
    {
        if (player.Map?.FindUpdater<CreatureMapSystem>() is not { } system)
        {
            return null;
        }

        var near = new List<Creature>();
        system.CollectCreaturesNear(player.X, player.Y, 300f, near);
        Creature? best = null;
        float bestDistance = float.MaxValue;
        foreach (Creature creature in near)
        {
            float dx = creature.X - player.X, dy = creature.Y - player.Y;
            float d = (dx * dx) + (dy * dy);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = creature;
            }
        }

        return best;
    }

    /// <summary>The GUIDs spliced into 64-bit and packed-GUID fields (world thread; the select character's GUID is added for the character screen).</summary>
    internal static List<(string Name, ulong Value)> BuildGuidSet(Player primary, Player? other, ulong? selectCharacter)
    {
        var guids = new List<(string, ulong)>
        {
            ("zero", 0),
            ("self", primary.Guid.Value),
            ("nonexistent player", ObjectGuid.Player(0x7FFF_FFF0).Value),
            ("nonexistent creature", ObjectGuid.WithEntry(HighGuid.Unit, 1, 0xFFFFF).Value),
            ("nonexistent gameobject", ObjectGuid.WithEntry(HighGuid.GameObject, 1, 0xFFFFF).Value),
        };
        if (other is not null)
        {
            guids.Add(("other-map player", other.Guid.Value));
        }

        if (NearestCreature(primary) is { } creature)
        {
            guids.Add(("nearest creature", creature.Guid.Value));
        }

        ObjectGuid chest = ObjectGuid.WithEntry(HighGuid.GameObject, FixtureGameObjectEntry, FixtureSpawn);
        if (primary.Map?.FindObject(chest) is not null)
        {
            guids.Add(("fixture gameobject", chest.Value));
        }

        Item? item = primary.Inventory.AllItems.FirstOrDefault();
        if (item is not null)
        {
            guids.Add(("first item", item.Guid.Value));
        }

        if (selectCharacter is { } character)
        {
            guids.Add(("character-select character", character));
        }

        guids.Add(("ulong.MaxValue", ulong.MaxValue));
        guids.Add(("0x8000000000000000", 0x8000_0000_0000_0000UL));
        return guids;
    }

    internal static string Letters(int counter)
    {
        var sb = new StringBuilder();
        do
        {
            sb.Insert(0, (char)('a' + (counter % 26)));
            counter /= 26;
        }
        while (counter > 0);
        return sb.ToString();
    }
}
