using System.Diagnostics;
using System.Text;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Net;

/// <summary>What the detector found: the same (opcode, payload) sent twice within the window.</summary>
public readonly record struct DuplicatePacketHit(WorldOpcode Opcode, int PayloadLength, uint Hash, long DeltaMs, string? FirstStack);

/// <summary>
/// Reports an identical outbound packet sent twice to one client within a short window (docs/ops/observability.md), logger category
/// <see cref="Category"/>. The cost per send is one <see cref="HashCode"/> pass over the payload and one compare against a small ring of
/// recent (opcode, hash, length, time) entries under a per-session lock; nothing is allocated unless the opcode is armed (see below) or a
/// duplicate is reported. Opcodes that legitimately repeat (movement, updates, time sync, combat and spell logs, chat, queries) are never
/// checked. The first send of a pair has no stack (capturing one per send would cost far more than the hash); the second send's stack is
/// captured when the duplicate is found, and the opcode is then <em>armed</em>: later sends of it keep their stack in the ring (at most
/// <see cref="MaxCapturesPerSecond"/> per second process-wide), so the next report of that opcode carries both stacks. Shared and
/// thread-safe; a <see cref="SessionState"/> belongs to one session.
/// </summary>
public sealed partial class DuplicatePacketDetector
{
    public const string Category = "ArcaneCore.DuplicatePackets";

    /// <summary>Stack captures allowed per second across the process for armed opcodes.</summary>
    public const int MaxCapturesPerSecond = 50;

    private static readonly bool[] Allowed = BuildAllowList();

    private readonly DuplicatePacketOptions _options;
    private readonly ILogger _logger;
    private readonly Func<long> _timestamp;
    private readonly long _windowTicks;
    private readonly ulong[] _armed = new ulong[16];
    private long _captureSecond;
    private int _captures;

    public DuplicatePacketDetector(ObservabilityOptions options, ILoggerFactory loggerFactory)
        : this(options.DuplicatePackets, loggerFactory.CreateLogger(Category), Stopwatch.GetTimestamp)
    {
    }

    public DuplicatePacketDetector(DuplicatePacketOptions options, ILogger logger, Func<long> timestamp)
    {
        _options = options;
        _logger = logger;
        _timestamp = timestamp;
        _windowTicks = Math.Clamp(options.WindowMs, 1, 5000) * Stopwatch.Frequency / 1000;
    }

    public bool Enabled => _options.Enabled;

    public bool IncludeBots => _options.IncludeBots;

    /// <summary>True when an opcode is on the never-check list (it legitimately repeats).</summary>
    public static bool IsAllowListed(WorldOpcode opcode) => (uint)(ushort)opcode >= (uint)Allowed.Length || Allowed[(ushort)opcode];

    private static bool[] BuildAllowList()
    {
        var table = new bool[1024];
        WorldOpcode[] explicitOpcodes =
        [
            WorldOpcode.SmsgUpdateObject, WorldOpcode.SmsgCompressedUpdateObject, WorldOpcode.SmsgDestroyObject, WorldOpcode.SmsgPong,
            WorldOpcode.SmsgMessagechat, WorldOpcode.SmsgEmote, WorldOpcode.SmsgTextEmote, WorldOpcode.SmsgAttackerstateupdate,
            WorldOpcode.SmsgAttackstart, WorldOpcode.SmsgAttackstop, WorldOpcode.SmsgSpellStart, WorldOpcode.SmsgSpellGo,
            WorldOpcode.SmsgSpellnonmeleedamagelog, WorldOpcode.SmsgSpellheallog, WorldOpcode.SmsgSpellenergizelog, WorldOpcode.SmsgPeriodicauralog,
            WorldOpcode.SmsgSpelllogmiss, WorldOpcode.SmsgSpelllogexecute, WorldOpcode.SmsgSpelldamageshield, WorldOpcode.SmsgSpelldispellog,
            WorldOpcode.SmsgAuracastlog, WorldOpcode.SmsgEnvironmentaldamagelog, WorldOpcode.SmsgResistlog, WorldOpcode.SmsgProcresist,
            WorldOpcode.SmsgSpellordamageImmune, WorldOpcode.SmsgSpellFailure, WorldOpcode.SmsgSpellFailedOther, WorldOpcode.SmsgSpellCooldown,
            WorldOpcode.SmsgCooldownEvent, WorldOpcode.SmsgClearCooldown, WorldOpcode.SmsgUpdateAuraDuration, WorldOpcode.SmsgPlaySpellVisual,
            WorldOpcode.SmsgPlaySpellImpact, WorldOpcode.SmsgPlaySound, WorldOpcode.SmsgPlayMusic, WorldOpcode.SmsgPlayObjectSound,
            WorldOpcode.SmsgQueryTimeResponse, WorldOpcode.SmsgGametimeUpdate, WorldOpcode.SmsgServertime, WorldOpcode.SmsgLoginSettimespeed,
            WorldOpcode.SmsgPartyMemberStats, WorldOpcode.SmsgPartyMemberStatsFull, WorldOpcode.SmsgWeather, WorldOpcode.SmsgUpdateWorldState,
            WorldOpcode.SmsgItemCooldown, WorldOpcode.SmsgItemTimeUpdate, WorldOpcode.SmsgItemEnchantTimeUpdate, WorldOpcode.SmsgAiReaction,
            WorldOpcode.SmsgNotification, WorldOpcode.SmsgAreaTriggerMessage, WorldOpcode.SmsgServerMessage, WorldOpcode.SmsgChannelNotify,
            WorldOpcode.SmsgGameobjectCustomAnim, WorldOpcode.SmsgGameobjectSpawnAnim, WorldOpcode.SmsgGameobjectDespawnAnim,
            WorldOpcode.SmsgClientControlUpdate, WorldOpcode.SmsgSetRestStart, WorldOpcode.SmsgCancelCombat, WorldOpcode.SmsgCancelAutoRepeat,
            WorldOpcode.SmsgStandstateUpdate, WorldOpcode.SmsgStartMirrorTimer, WorldOpcode.SmsgPauseMirrorTimer, WorldOpcode.SmsgStopMirrorTimer,
            WorldOpcode.SmsgSetFactionStanding, WorldOpcode.SmsgSetFlatSpellModifier, WorldOpcode.SmsgSetPctSpellModifier,
            WorldOpcode.SmsgGroupList, WorldOpcode.SmsgGossipMessage, WorldOpcode.SmsgGossipComplete, WorldOpcode.SmsgListInventory,
            WorldOpcode.SmsgTrainerList, WorldOpcode.SmsgQuestgiverStatus, WorldOpcode.SmsgQuestgiverQuestList, WorldOpcode.SmsgPowergainlogObsolete,
            WorldOpcode.SmsgPetSpells, WorldOpcode.SmsgPetActionSound, WorldOpcode.SmsgLogXpgain,
        ];
        foreach (WorldOpcode opcode in explicitOpcodes)
        {
            table[(ushort)opcode] = true;
        }

        // Families by name: every bidirectional MSG_ opcode (movement and others), the movement and spline families, query replies.
        foreach (WorldOpcode opcode in Enum.GetValues<WorldOpcode>())
        {
            if ((ushort)opcode >= table.Length)
            {
                continue;
            }

            string name = opcode.ToString();
            if (name.StartsWith("Msg", StringComparison.Ordinal) || name.StartsWith("SmsgSpline", StringComparison.Ordinal)
                || name.StartsWith("SmsgMove", StringComparison.Ordinal) || name.StartsWith("SmsgForce", StringComparison.Ordinal)
                || name.StartsWith("SmsgMonsterMove", StringComparison.Ordinal) || name.EndsWith("QueryResponse", StringComparison.Ordinal)
                || name.StartsWith("Cmsg", StringComparison.Ordinal))
            {
                table[(ushort)opcode] = true;
            }
        }

        return table;
    }

    /// <summary>The detector's per-session state: a ring of recent sends and the warning bucket.</summary>
    public sealed class SessionState
    {
        internal readonly Entry[] Ring;
        internal int Next;
        internal TokenBucket Bucket;

        internal SessionState(int ringSize, int burst, int perMinute)
        {
            Ring = new Entry[ringSize];
            Bucket = new TokenBucket(burst, perMinute);
        }
    }

    internal struct Entry
    {
        public long Ticks;
        public uint Hash;
        public int Length;
        public ushort Opcode;
        public string? Stack;
    }

    public SessionState NewSession() => new(
        Math.Clamp(_options.RingSize, 4, 256),
        Math.Clamp(_options.WarnBurstPerSession, 1, 100),
        Math.Clamp(_options.WarnPerMinute, 1, 600));

    /// <summary>
    /// Record one outbound packet and look for an identical one in the window. Returns true (with the details) on a duplicate; the caller
    /// logs through <see cref="Report"/>. Cheap path: allow-list table load, one hash, one ring scan.
    /// </summary>
    public bool Observe(SessionState state, WorldOpcode opcode, ReadOnlySpan<byte> payload, out DuplicatePacketHit hit)
    {
        hit = default;
        if (IsAllowListed(opcode))
        {
            return false;
        }

        var hasher = new HashCode();
        hasher.Add((ushort)opcode);
        hasher.AddBytes(payload);
        uint hash = (uint)hasher.ToHashCode();
        long now = _timestamp();
        bool armed = IsArmed(opcode);
        string? stack = armed && _options.CaptureStacks && TryTakeCapture(now) ? CompactStack() : null;

        lock (state)
        {
            Entry[] ring = state.Ring;
            int found = -1;
            for (int i = 0; i < ring.Length; i++)
            {
                ref Entry e = ref ring[i];
                if (e.Ticks != 0 && e.Hash == hash && e.Length == payload.Length && e.Opcode == (ushort)opcode && now - e.Ticks <= _windowTicks)
                {
                    found = i;
                    break;
                }
            }

            if (found >= 0)
            {
                hit = new DuplicatePacketHit(opcode, payload.Length, hash, (now - ring[found].Ticks) * 1000 / Stopwatch.Frequency, ring[found].Stack);
            }

            ring[state.Next] = new Entry { Ticks = now == 0 ? 1 : now, Hash = hash, Length = payload.Length, Opcode = (ushort)opcode, Stack = stack };
            state.Next = (state.Next + 1) % ring.Length;
            return found >= 0;
        }
    }

    /// <summary>Log a duplicate found by <see cref="Observe"/> as a Warning (rate limited per session), arm the opcode and capture the second send's stack.</summary>
    public void Report(SessionState state, in SessionLogContext who, in DuplicatePacketHit hit)
    {
        Arm(hit.Opcode);
        if (!_logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        long suppressed;
        lock (state)
        {
            if (!state.Bucket.TryTake(Environment.TickCount64, out suppressed))
            {
                return;
            }
        }

        string second = _options.CaptureStacks ? CompactStack() : "(stack capture off)";
        string first = hit.FirstStack ?? (_options.CaptureStacks ? "(not captured: this opcode had not duplicated before; the next report carries it)" : "(stack capture off)");
        Duplicate(_logger, WorldOpcodeNames.GetName(hit.Opcode), hit.DeltaMs, who.CharacterLow, who.MapId, who.AccountId, hit.PayloadLength, hit.Hash, second, first, suppressed);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "duplicate outbound packet {Opcode}: identical payload sent twice within {DeltaMs} ms to character {CharacterLow} (map {MapId}, account {AccountId}, {Length} bytes, hash {Hash:X8}); second send: {SecondStack}; first send: {FirstStack} (suppressed {Suppressed})")]
    private static partial void Duplicate(ILogger logger, string opcode, long deltaMs, uint characterLow, uint mapId, int accountId, int length, uint hash, string secondStack, string firstStack, long suppressed);

    private bool IsArmed(WorldOpcode opcode)
    {
        int index = (ushort)opcode;
        return index < 1024 && (Volatile.Read(ref _armed[index >> 6]) & (1UL << (index & 63))) != 0;
    }

    private void Arm(WorldOpcode opcode)
    {
        int index = (ushort)opcode;
        if (index < 1024)
        {
            Interlocked.Or(ref _armed[index >> 6], 1UL << (index & 63));
        }
    }

    /// <summary>True for an opcode that has duplicated before (tests).</summary>
    public bool IsOpcodeArmed(WorldOpcode opcode) => IsArmed(opcode);

    private bool TryTakeCapture(long nowTicks)
    {
        long second = nowTicks / Stopwatch.Frequency;
        if (Interlocked.Exchange(ref _captureSecond, second) != second)
        {
            Volatile.Write(ref _captures, 0);
        }

        return Interlocked.Increment(ref _captures) <= MaxCapturesPerSecond;
    }

    /// <summary>"Type.Method &lt; Type.Method ..." for the calling frames above the send path, at most 10.</summary>
    internal static string CompactStack()
    {
        var trace = new StackTrace(1, fNeedFileInfo: false);
        var sb = new StringBuilder(200);
        int taken = 0;
        for (int i = 0; i < trace.FrameCount && taken < 10; i++)
        {
            System.Reflection.MethodBase? method = trace.GetFrame(i)?.GetMethod();
            Type? type = method?.DeclaringType;
            if (method is null || type is null)
            {
                continue;
            }

            if (type == typeof(DuplicatePacketDetector) || (type == typeof(WorldSession) && method.Name is "Send" or "ObserveOutbound"))
            {
                continue;
            }

            if (taken > 0)
            {
                sb.Append(" < ");
            }

            sb.Append(type.Name).Append('.').Append(method.Name);
            taken++;
        }

        return taken == 0 ? "(no frames)" : sb.ToString();
    }
}
