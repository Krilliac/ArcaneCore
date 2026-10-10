using System.Diagnostics.Metrics;

namespace ArcaneCore.Kernel.Ops.Metrics;

/// <summary>
/// Per-opcode packet and byte accounting for the network meter. Modelled on TrinityCore src/common/Metric (named, tagged
/// counters) but built so the per-packet cost stays flat: the four instruments are <see cref="ObservableCounter{T}"/>s
/// read only when the store collects, and each protocol's <see cref="OpcodeTable"/> counts into preallocated
/// <c>long[]</c> slots with <see cref="Interlocked.Add(ref long, long)"/>. A tagged synchronous <c>Counter.Add</c> would
/// build a lookup key per measurement in <see cref="MetricsStore"/>; this path allocates nothing and looks nothing up.
/// <para>
/// Series are labelled <c>protocol</c> and <c>opcode</c>. Every value a table does not know (gaps, out-of-range, unregistered)
/// shares the one opcode label <see cref="OpcodeTable.UnknownLabel"/>, so cardinality is bounded at the registered count plus
/// one per protocol per instrument, and only non-zero slots are reported (docs/ops/metrics.md).
/// </para>
/// <para>
/// Thread affinity: <see cref="OpcodeTable"/> recorders are safe from any thread (lock-free); <see cref="Register"/> is
/// serialised by a lock and publishes a new table array, so a collection never sees a half-built table.
/// </para>
/// </summary>
public sealed class OpcodeTrafficMeter
{
    private readonly Lock _gate = new();
    private readonly ObservableCounter<long> _packetsIn;
    private readonly ObservableCounter<long> _packetsOut;
    private readonly ObservableCounter<long> _bytesIn;
    private readonly ObservableCounter<long> _bytesOut;
    private OpcodeTable[] _tables = [];

    /// <summary>Create the four per-opcode instruments on <paramref name="meter"/>.</summary>
    public OpcodeTrafficMeter(Meter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);
        _packetsIn = meter.CreateObservableCounter("arcanecore.net.opcode.packets_in", () => Observe(OpcodeTable.Series.PacketsIn),
            "{packet}", "Client packets received, by protocol and opcode.");
        _packetsOut = meter.CreateObservableCounter("arcanecore.net.opcode.packets_out", () => Observe(OpcodeTable.Series.PacketsOut),
            "{packet}", "Server packets sent, by protocol and opcode.");
        _bytesIn = meter.CreateObservableCounter("arcanecore.net.opcode.bytes_in", () => Observe(OpcodeTable.Series.BytesIn),
            "By", "Client bytes received, by protocol and opcode.");
        _bytesOut = meter.CreateObservableCounter("arcanecore.net.opcode.bytes_out", () => Observe(OpcodeTable.Series.BytesOut),
            "By", "Server bytes sent, by protocol and opcode.");
    }

    /// <summary>True while a listener observes any of the four instruments; the recorders return at once otherwise.</summary>
    public bool Enabled => _packetsIn.Enabled || _packetsOut.Enabled || _bytesIn.Enabled || _bytesOut.Enabled;

    /// <summary>
    /// Register a protocol's known opcodes and return the table that counts them.
    /// </summary>
    /// <param name="protocol">The <c>protocol</c> label value; unique per meter.</param>
    /// <param name="known">Wire value (0..65535) to the <c>opcode</c> label; values unique, names non-empty and never <c>unknown</c>.</param>
    /// <exception cref="ArgumentException">Any of the above is violated.</exception>
    public OpcodeTable Register(string protocol, IEnumerable<KeyValuePair<int, string>> known)
    {
        ArgumentNullException.ThrowIfNull(known);
        if (string.IsNullOrWhiteSpace(protocol))
        {
            throw new ArgumentException("The protocol label must not be empty.", nameof(protocol));
        }

        var entries = new List<KeyValuePair<int, string>>();
        var seen = new HashSet<int>();
        int max = -1;
        foreach (KeyValuePair<int, string> entry in known)
        {
            if (entry.Key < 0 || entry.Key > ushort.MaxValue)
            {
                throw new ArgumentException($"Opcode value {entry.Key} is outside 0..{ushort.MaxValue}.", nameof(known));
            }

            if (string.IsNullOrWhiteSpace(entry.Value))
            {
                throw new ArgumentException($"Opcode value {entry.Key} has an empty name.", nameof(known));
            }

            if (string.Equals(entry.Value, OpcodeTable.UnknownLabel, StringComparison.Ordinal))
            {
                throw new ArgumentException($"Opcode value {entry.Key} uses the reserved name '{OpcodeTable.UnknownLabel}'.", nameof(known));
            }

            if (!seen.Add(entry.Key))
            {
                throw new ArgumentException($"Opcode value {entry.Key} is registered twice.", nameof(known));
            }

            max = Math.Max(max, entry.Key);
            entries.Add(entry);
        }

        lock (_gate)
        {
            foreach (OpcodeTable existing in _tables)
            {
                if (string.Equals(existing.Protocol, protocol, StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Protocol '{protocol}' is already registered.", nameof(protocol));
                }
            }

            var table = new OpcodeTable(this, protocol, entries, max);
            var next = new OpcodeTable[_tables.Length + 1];
            _tables.CopyTo(next, 0);
            next[^1] = table;
            Volatile.Write(ref _tables, next);
            return table;
        }
    }

    // Runs only at collection time, on the collector's thread. Reports one measurement per non-zero slot.
    private IEnumerable<Measurement<long>> Observe(OpcodeTable.Series series)
    {
        foreach (OpcodeTable table in Volatile.Read(ref _tables))
        {
            long[] counts = table.Counts(series);
            for (int slot = 0; slot < counts.Length; slot++)
            {
                long value = Volatile.Read(ref counts[slot]);
                if (value != 0)
                {
                    yield return new Measurement<long>(value, table.Tags(slot));
                }
            }
        }
    }
}

/// <summary>Counters read back from one opcode slot.</summary>
public readonly record struct OpcodeCounts(long PacketsIn, long BytesIn, long PacketsOut, long BytesOut);

/// <summary>
/// One protocol's opcode-to-slot table. Slot 0 is the shared <see cref="UnknownLabel"/> bucket; the registered opcodes
/// follow. Immutable after construction except for the counters, which are updated with <see cref="Interlocked"/>.
/// </summary>
public sealed class OpcodeTable
{
    /// <summary>The label of the single bucket that takes every opcode the table does not know.</summary>
    public const string UnknownLabel = "unknown";

    internal enum Series
    {
        PacketsIn,
        PacketsOut,
        BytesIn,
        BytesOut,
    }

    private readonly OpcodeTrafficMeter _owner;
    private readonly int[] _slotOf; // wire value -> slot; 0 (unknown) for gaps; values past the end are unknown too
    private readonly string[] _labels;
    private readonly KeyValuePair<string, object?>[][] _tags;
    private readonly long[] _packetsIn;
    private readonly long[] _packetsOut;
    private readonly long[] _bytesIn;
    private readonly long[] _bytesOut;

    internal OpcodeTable(OpcodeTrafficMeter owner, string protocol, List<KeyValuePair<int, string>> known, int maxValue)
    {
        _owner = owner;
        Protocol = protocol;
        _slotOf = new int[maxValue + 1];
        int slots = known.Count + 1;
        _labels = new string[slots];
        _tags = new KeyValuePair<string, object?>[slots][];
        _labels[0] = UnknownLabel;
        for (int i = 0; i < known.Count; i++)
        {
            _slotOf[known[i].Key] = i + 1;
            _labels[i + 1] = known[i].Value;
        }

        for (int slot = 0; slot < slots; slot++)
        {
            _tags[slot] = [new("protocol", protocol), new("opcode", _labels[slot])];
        }

        _packetsIn = new long[slots];
        _packetsOut = new long[slots];
        _bytesIn = new long[slots];
        _bytesOut = new long[slots];
    }

    /// <summary>The <c>protocol</c> label value of this table.</summary>
    public string Protocol { get; }

    /// <summary>The <c>opcode</c> label of a wire value: its registered name, or <see cref="UnknownLabel"/>.</summary>
    public string LabelOf(uint opcode) => _labels[SlotOf(opcode)];

    /// <summary>Count one inbound packet of <paramref name="bytes"/> bytes.</summary>
    public void RecordIn(uint opcode, int bytes)
    {
        if (!_owner.Enabled)
        {
            return;
        }

        int slot = SlotOf(opcode);
        Interlocked.Add(ref _packetsIn[slot], 1);
        Interlocked.Add(ref _bytesIn[slot], bytes);
    }

    /// <summary>Count <paramref name="bytes"/> more inbound bytes of a packet already counted by <see cref="RecordIn"/> (multi-part reads).</summary>
    public void RecordInBytes(uint opcode, int bytes)
    {
        if (!_owner.Enabled)
        {
            return;
        }

        Interlocked.Add(ref _bytesIn[SlotOf(opcode)], bytes);
    }

    /// <summary>Count one outbound packet of <paramref name="bytes"/> bytes.</summary>
    public void RecordOut(uint opcode, int bytes)
    {
        if (!_owner.Enabled)
        {
            return;
        }

        int slot = SlotOf(opcode);
        Interlocked.Add(ref _packetsOut[slot], 1);
        Interlocked.Add(ref _bytesOut[slot], bytes);
    }

    /// <summary>The counters of the slot <paramref name="opcode"/> maps to (the unknown slot for an unknown value).</summary>
    public OpcodeCounts Read(uint opcode)
    {
        int slot = SlotOf(opcode);
        return new OpcodeCounts(
            Volatile.Read(ref _packetsIn[slot]),
            Volatile.Read(ref _bytesIn[slot]),
            Volatile.Read(ref _packetsOut[slot]),
            Volatile.Read(ref _bytesOut[slot]));
    }

    internal long[] Counts(Series series) => series switch
    {
        Series.PacketsIn => _packetsIn,
        Series.PacketsOut => _packetsOut,
        Series.BytesIn => _bytesIn,
        _ => _bytesOut,
    };

    internal KeyValuePair<string, object?>[] Tags(int slot) => _tags[slot];

    private int SlotOf(uint opcode) => opcode < (uint)_slotOf.Length ? _slotOf[opcode] : 0;
}
