using ArcaneCore.Protocol;

namespace ArcaneCore.World.AntiCheat.PacketHeuristics;

/// <summary>
/// The per-session state of the packet heuristics: the cadence windows, the replay slots and the report throttle of one session. Created on the
/// first packet the heuristics actually inspect (never while the feature is off or the session exempt) and owned by the session.
/// <para>
/// Thread affinity: the network path (client sessions: the reader task, serialised by the dispatch gate) and the world thread both reach it;
/// every member takes <see cref="Gate"/>, which is uncontended except for a finding racing the next packet.
/// </para>
/// </summary>
internal sealed partial class PacketHeuristicsSession
{
    private const int ReplaySlots = 8;
    private const int HashedBytes = 256;

    private readonly SlidingWindow?[] _windows = new SlidingWindow?[PacketOpcodeClasses.ClassCount];
    private readonly Dictionary<int, ThrottleEntry> _throttle = [];
    private ReplaySlot[]? _replay;
    private int _nextReplaySlot;
    private long _lastPingMs = long.MinValue;
    private int _epoch;

    internal object Gate { get; } = new();

    /// <summary>Feeds one packet of <paramref name="kind"/> into its class window; true (with the packets in the second) when it exceeds <paramref name="ceiling"/> per sliding second. Caller holds <see cref="Gate"/>.</summary>
    internal bool NoteCadence(CadenceClass kind, long nowMs, int ceiling, out int inWindow)
    {
        inWindow = 0;
        SlidingWindow? window = _windows[(int)kind];
        if (window is null || window.Stamps.Length != ceiling)
        {
            window = _windows[(int)kind] = new SlidingWindow(ceiling);
        }

        // Ring of the last `ceiling` arrival times: when it is full, the oldest entry is the packet `ceiling` arrivals ago, and a new one
        // inside a second of it is packet number ceiling + 1 of one sliding second.
        bool exceeded = false;
        if (window.Count == ceiling)
        {
            long oldest = window.Stamps[window.Head];
            if (nowMs - oldest < 1000)
            {
                exceeded = true;
                inWindow = ceiling + 1;
            }
        }
        else
        {
            window.Count++;
        }

        window.Stamps[window.Head] = nowMs;
        window.Head = (window.Head + 1) % ceiling;
        return exceeded;
    }

    /// <summary>Notes a CMSG_PING; true (with the gap in ms) when it follows the previous one by less than <paramref name="minIntervalMs"/>. Caller holds <see cref="Gate"/>.</summary>
    internal bool NotePing(long nowMs, int minIntervalMs, out long gapMs)
    {
        gapMs = 0;
        long previous = _lastPingMs;
        _lastPingMs = nowMs;
        if (previous == long.MinValue)
        {
            return false;
        }

        gapMs = nowMs - previous;
        return gapMs < minIntervalMs;
    }

    /// <summary>Notes a payload of an opcode that is checked for replay; true (with the repeat count) when it is the <paramref name="threshold"/>th or later identical one inside the window. Caller holds <see cref="Gate"/>.</summary>
    internal bool NoteReplay(WorldOpcode opcode, byte[] payload, long nowMs, int threshold, long windowMs, out int count)
    {
        count = 0;
        ulong hash = Hash(payload);
        ReplaySlot[] slots = _replay ??= new ReplaySlot[ReplaySlots];
        ushort code = (ushort)opcode;
        for (int i = 0; i < slots.Length; i++)
        {
            ref ReplaySlot slot = ref slots[i];
            if (slot.Count == 0 || slot.Opcode != code || slot.Hash != hash)
            {
                continue;
            }

            if (nowMs - slot.WindowStart <= windowMs)
            {
                slot.Count++;
            }
            else
            {
                slot.Count = 1;
                slot.WindowStart = nowMs;
            }

            count = slot.Count;
            return slot.Count >= threshold;
        }

        // A payload not seen recently takes the oldest slot (round robin); eight distinct recent payloads are tracked per session.
        ref ReplaySlot fresh = ref slots[_nextReplaySlot];
        _nextReplaySlot = (_nextReplaySlot + 1) % slots.Length;
        fresh = new ReplaySlot { Opcode = code, Hash = hash, Count = 1, WindowStart = nowMs };
        count = 1;
        return false;
    }

    /// <summary>
    /// The report throttle: true when a report for <c>(check, opcode)</c> should go to the ledger now, with how many occurrences were held back since
    /// the previous one. At most one report per <paramref name="intervalMs"/>. Caller holds <see cref="Gate"/>.
    /// </summary>
    internal bool ShouldReport(PacketCheck check, WorldOpcode opcode, long nowMs, long intervalMs, int epoch, out int suppressed)
    {
        suppressed = 0;
        if (epoch != _epoch)
        {
            _epoch = epoch;
            _throttle.Clear();
        }

        // Opcodes outside the table share one bucket, so a client cannot grow the dictionary by sending arbitrary values.
        int slot = (uint)opcode < PacketHeuristicsOptions.OpcodeSpace ? (int)opcode : PacketHeuristicsOptions.OpcodeSpace;
        int key = ((int)check * (PacketHeuristicsOptions.OpcodeSpace + 1)) + slot;
        if (_throttle.TryGetValue(key, out ThrottleEntry entry))
        {
            if (nowMs - entry.LastReportMs < intervalMs)
            {
                _throttle[key] = entry with { Held = entry.Held + 1 };
                return false;
            }

            suppressed = entry.Held;
        }

        _throttle[key] = new ThrottleEntry(nowMs, 0);
        return true;
    }

    private static ulong Hash(byte[] payload)
    {
        // FNV-1a over the first bytes plus the length: a replayed packet is byte-identical, so a prefix hash that also folds in the length
        // is enough, and the cost stays bounded for the largest frames.
        ulong hash = 14695981039346656037UL;
        int count = Math.Min(payload.Length, HashedBytes);
        for (int i = 0; i < count; i++)
        {
            hash = (hash ^ payload[i]) * 1099511628211UL;
        }

        return hash ^ ((ulong)payload.Length * 0x9E3779B97F4A7C15UL);
    }

    private sealed class SlidingWindow(int capacity)
    {
        internal long[] Stamps { get; } = new long[capacity];

        internal int Head { get; set; }

        internal int Count { get; set; }
    }

    private struct ReplaySlot
    {
        internal ushort Opcode;
        internal ulong Hash;
        internal int Count;
        internal long WindowStart;
    }

    private readonly record struct ThrottleEntry(long LastReportMs, int Held);
}
