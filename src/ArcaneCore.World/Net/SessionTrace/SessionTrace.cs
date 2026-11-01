using System.Diagnostics;
using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.AntiCheat;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Net.SessionTrace;

/// <summary>
/// One traced character (<c>.debug trace session</c>): the line writer, the per-session token bucket, and the little state the event
/// lines need. The line shape is <c>[{Endpoint}] trace {Character} guid={Guid.Low} {event}[ {detail}]</c> at Information in the category
/// <see cref="SessionTraceFeature.LogCategory"/>; nothing else is ever written to that category.
/// <para>
/// Threads: the inbound reader thread (<see cref="Inbound"/>), any sending thread (<see cref="Outbound"/>) and the world and map threads write
/// lines at the same time. The bucket decision is made under <see cref="_gate"/>, the logger call is outside it, and every counter is
/// read and written with <see cref="Interlocked"/>. Fields marked "world thread" are touched only by the world thread or, for the movement view and the shadow, by the map thread of the player in the parallel phase (never both in one phase). The entry in the feature's registry
/// outlives the connection: <see cref="Endpoint"/> is "offline" while no session is attached.
/// </para>
/// </summary>
public sealed class SessionTrace
{
    /// <summary>The endpoint shown while the traced character has no connection.</summary>
    public const string OfflineEndpoint = "offline";

    private static readonly Func<string, Exception?, string> Identity = static (line, _) => line;

    private readonly ILogger _lines;
    private readonly ILogger _faults;
    private readonly TimeProvider _clock;
    private readonly SessionTraceOptions _options;
    private readonly object _gate = new();
    private readonly object _attachGate = new();
    private readonly TraceRateLimiter _limiter;
    private WorldSession? _session;
    private string _endpoint = OfflineEndpoint;
    private long _expiresUtcTicks;
    private long _lineCount;
    private long _droppedCount;
    private long _reportedDropped;

    // World thread: the clock view of the movement lines.
    private bool _hasOffset;
    private uint _firstOffset;

    // World thread: the last polled state.
    private bool _hasState;
    private DeathState _death;
    private PlayerFlags _playerFlags;
    private UnitFlags _unitFlags;
    private uint _dynamicFlags;
    private uint _mapId;
    private bool _alive;

    internal SessionTrace(
        ObjectGuid guid, string character, SessionTraceOptions options, TimeProvider clock, ILogger lines, ILogger faults, DateTimeOffset expiresAt)
    {
        Guid = guid;
        Character = character;
        _options = options;
        _clock = clock;
        _lines = lines;
        _faults = faults;
        _limiter = new TraceRateLimiter(options.LinesPerSecond, options.BurstLines, clock.GetTimestamp(), clock.TimestampFrequency);
        _expiresUtcTicks = expiresAt.UtcTicks;
        Shadow = new SessionTraceShadow(this);
    }

    /// <summary>The traced character.</summary>
    public ObjectGuid Guid { get; }

    /// <summary>The character's name when the trace was turned on.</summary>
    public string Character { get; }

    /// <summary>The connection's remote endpoint, or <see cref="OfflineEndpoint"/> while the character has none.</summary>
    public string Endpoint => Volatile.Read(ref _endpoint);

    /// <summary>When the trace ends by itself.</summary>
    public DateTimeOffset ExpiresAt => new(Interlocked.Read(ref _expiresUtcTicks), TimeSpan.Zero);

    /// <summary>Lines written so far, the drop report and the control lines included.</summary>
    public long Lines => Interlocked.Read(ref _lineCount);

    /// <summary>Lines refused by the bucket so far.</summary>
    public long Dropped => Interlocked.Read(ref _droppedCount);

    /// <summary>The read-only anti-cheat shadow of this character (exempt players only).</summary>
    internal SessionTraceShadow Shadow { get; }

    /// <summary>The account security of the character when the trace was turned on; decides who may see or stop an offline trace.</summary>
    internal AccountSecurity Security { get; set; }

    internal void SetExpiry(DateTimeOffset expiresAt) => Interlocked.Exchange(ref _expiresUtcTicks, expiresAt.UtcTicks);

    // --- session attachment ------------------------------------------------------------------------------------------

    /// <summary>Make <paramref name="session"/> the connection this trace follows (the previous one stops feeding it). World thread.</summary>
    internal void Attach(WorldSession session)
    {
        WorldSession? previous;
        lock (_attachGate)
        {
            previous = _session;
            _session = session;
            Volatile.Write(ref _endpoint, session.RemoteEndpoint);
        }

        if (previous is not null && !ReferenceEquals(previous, session))
        {
            previous.DetachSessionTrace(this);
        }

        session.AttachSessionTrace(this);
        if (session.State == ArcaneCore.World.Handlers.SessionState.Closed)
        {
            Disconnected(session); // the connection closed before it was attached: Close() saw no trace, so do its part here
        }

        _hasState = false;
        _hasOffset = false;
    }

    /// <summary>The connection <paramref name="session"/> ended: it stops feeding this trace, which keeps its registry entry as offline. Any thread.</summary>
    internal void Detach(WorldSession session)
    {
        lock (_attachGate)
        {
            if (ReferenceEquals(_session, session))
            {
                _session = null;
                Volatile.Write(ref _endpoint, OfflineEndpoint);
            }
        }

        session.DetachSessionTrace(this);
    }

    /// <summary>Whether <paramref name="session"/> is the connection this trace follows now.</summary>
    internal bool IsAttachedTo(WorldSession session)
    {
        lock (_attachGate)
        {
            return ReferenceEquals(_session, session);
        }
    }

    /// <summary>Stop following whatever connection is attached (the trace ended, or the character logged out).</summary>
    internal void DetachCurrent()
    {
        WorldSession? session;
        lock (_attachGate)
        {
            session = _session;
            _session = null;
            Volatile.Write(ref _endpoint, OfflineEndpoint);
        }

        session?.DetachSessionTrace(this);
    }

    // --- the line writer ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes one line when the bucket has a token; otherwise counts a dropped line. Returns whether the line was written.
    /// Thread-safe; never throws.
    /// </summary>
    internal bool Write(string eventName, string detail)
    {
        if (!TryAdmit())
        {
            return false;
        }

        Emit(eventName, detail);
        return true;
    }

    /// <summary>A line that is never refused by the bucket (start, stop, expiry, logout, disconnect, attach and the drop report): it is counted as a line but takes no token.</summary>
    internal void WriteControl(string eventName, string detail) => Emit(eventName, detail);

    private bool TryAdmit()
    {
        bool admitted;
        lock (_gate)
        {
            admitted = _limiter.TryTake(_clock.GetTimestamp());
        }

        if (!admitted)
        {
            Interlocked.Increment(ref _droppedCount);
        }

        return admitted;
    }

    private void Emit(string eventName, string detail)
    {
        try
        {
            Interlocked.Increment(ref _lineCount);
            string line = detail.Length == 0
                ? string.Create(CultureInfo.InvariantCulture, $"[{Endpoint}] trace {Character} guid={Guid.Low} {eventName}")
                : string.Create(CultureInfo.InvariantCulture, $"[{Endpoint}] trace {Character} guid={Guid.Low} {eventName} {detail}");
            _lines.Log(LogLevel.Information, default, line, null, Identity);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _faults.LogFault("session-trace.write", ex, "A session trace line for {Character} could not be written", Character);
        }
    }

    /// <summary>Once per clock second, world thread: writes the number of lines dropped since the last report, if any (the report is not itself dropped).</summary>
    internal void ReportDrops()
    {
        long total = Dropped;
        long since = total - _reportedDropped;
        if (since <= 0)
        {
            return;
        }

        _reportedDropped = total;
        Emit("rate-limit", string.Create(CultureInfo.InvariantCulture, $"dropped={since} total-dropped={total}"));
    }

    // --- session events ----------------------------------------------------------------------------------------------

    /// <summary>An inbound packet (the client reader, any thread). CMSG_PING also reports its sequence and the latency the client measured.</summary>
    internal void Inbound(WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        if (!TryAdmit())
        {
            return;
        }

        string name = WorldOpcodeNames.GetName(opcode);
        var reader = new PacketReader(payload);
        if (opcode == WorldOpcode.CmsgPing && reader.TryReadUInt32(out uint sequence) && reader.TryReadUInt32(out uint latency))
        {
            Emit("in", string.Create(CultureInfo.InvariantCulture, $"{name} size={payload.Length} seq={sequence} latency={latency}ms"));
        }
        else
        {
            Emit("in", string.Create(CultureInfo.InvariantCulture, $"{name} size={payload.Length}"));
        }
    }

    /// <summary>An outbound packet (any thread): written when it is in the curated set (<see cref="SessionTraceOutbound"/>).</summary>
    internal void Outbound(WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        if (!SessionTraceOutbound.IsCurated(opcode))
        {
            return;
        }

        if (opcode is WorldOpcode.SmsgNewWorld or WorldOpcode.MsgMoveTeleportAck)
        {
            Shadow.NoteServerRelocation();
        }

        if (SessionTraceOutbound.Describe(opcode, payload, Guid) is { } text)
        {
            Write("out", text);
        }
    }

    /// <summary>
    /// A handler finished (<see cref="WorldSession"/> hooks): written only above <see cref="SessionTraceOptions.SlowHandlerMicroseconds"/>.
    /// <paramref name="receivedMs"/> is when a world-thread packet was queued (0 for a session handler, which has no queue time).
    /// </summary>
    internal void Handled(WorldOpcode opcode, long startTimestamp, uint receivedMs)
    {
        long micros = (Stopwatch.GetTimestamp() - startTimestamp) * 1_000_000 / Stopwatch.Frequency;
        if (micros <= _options.SlowHandlerMicroseconds)
        {
            return;
        }

        long queuedMs = 0;
        if (receivedMs != 0)
        {
            queuedMs = Math.Max(0, unchecked((uint)Clock.Milliseconds() - receivedMs) - (micros / 1000));
        }

        Write("handled", string.Create(CultureInfo.InvariantCulture, $"{WorldOpcodeNames.GetName(opcode)} us={micros} queuedMs={queuedMs}"));
    }

    /// <summary>
    /// A client movement block, before it is stored (map thread of the player, from the movement handler). The line shows the 1.12.1 time-sync view: the
    /// client's movement timestamp against the server's receive clock (the 1.12.1 opcode table has no time-sync request or response, so this
    /// offset and its change since the first sample of this trace is all there is). Also feeds the anti-cheat shadow.
    /// </summary>
    internal void Movement(WorldSession session, Player player, WorldOpcode opcode, in MovementInfo movement)
    {
        uint offset = unchecked(movement.Time - session.CurrentPacketReceivedMs);
        if (!_hasOffset)
        {
            _hasOffset = true;
            _firstOffset = offset;
        }

        if (TryAdmit())
        {
            long clockOffset = (long)movement.Time - session.CurrentPacketReceivedMs;
            int drift = unchecked((int)(offset - _firstOffset));
            Emit("move", string.Create(CultureInfo.InvariantCulture,
                $"{WorldOpcodeNames.GetName(opcode)} x={movement.X:0.00} y={movement.Y:0.00} z={movement.Z:0.00} flags={movement.Flags} clientTime={movement.Time} clockOffsetMs={clockOffset} drift={drift}"));
        }

        try
        {
            if (session.Services.GetService<AntiCheatFeature>() is { } feature)
            {
                Shadow.OnMovement(feature, session, player, opcode, in movement);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _faults.LogFault("session-trace.shadow-movement", ex, "The anti-cheat shadow of {Character} failed on a movement block", Character);
        }
    }

    /// <summary>After a movement block or acknowledgement was stored: lets the shadow record the position the server now holds.</summary>
    internal void AfterMovement(Player player) => Shadow.AfterMovement(player);

    /// <summary>An honoured movement acknowledgement (map thread of the player). Also feeds the anti-cheat shadow.</summary>
    internal void Ack(WorldSession session, Player player, WorldOpcode opcode, uint clientTime, bool knockback)
    {
        Write("ack", string.Create(CultureInfo.InvariantCulture, $"{WorldOpcodeNames.GetName(opcode)} clientTime={clientTime} knockback={(knockback ? "true" : "false")}"));
        try
        {
            if (session.Services.GetService<AntiCheatFeature>() is { } feature)
            {
                Shadow.OnAcknowledgement(feature, session, player, clientTime, knockback);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _faults.LogFault("session-trace.shadow-ack", ex, "The anti-cheat shadow of {Character} failed on an acknowledgement", Character);
        }
    }

    /// <summary>CMSG_MOVE_TIME_SKIPPED for the traced character's own unit (map thread of the player). Also feeds the anti-cheat shadow.</summary>
    internal void TimeSkip(WorldSession session, Player player, uint skippedMs)
    {
        Write("timeskip", string.Create(CultureInfo.InvariantCulture, $"ms={skippedMs}"));
        try
        {
            if (session.Services.GetService<AntiCheatFeature>() is { } feature)
            {
                Shadow.OnTimeSkip(feature, session, player, skippedMs);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _faults.LogFault("session-trace.shadow-timeskip", ex, "The anti-cheat shadow of {Character} failed on a time skip", Character);
        }
    }

    /// <summary>The traced connection closed (<c>WorldSession.Close</c>, any thread): writes the reason and stops following the connection.</summary>
    internal void Disconnected(WorldSession session)
    {
        WriteControl("disconnect", "reason=" + (session.KickReason ?? "connection closed"));
        Detach(session);
    }

    // --- state polling (world thread, once per clock second) ---------------------------------------------------------

    /// <summary>
    /// Compares the character with what the last poll saw and writes a <c>state</c> line per changed field, plus <c>died</c> or
    /// <c>resurrected</c> when the character stops or starts being alive (spirit healer, corpse reclaim, resurrection spells and a GM revive all
    /// show up here). The first call, and the first after a new connection attaches, only records the baseline.
    /// </summary>
    internal void PollState(Player player)
    {
        var death = player.Combat.DeathState;
        PlayerFlags playerFlags = player.Flags;
        UnitFlags unitFlags = player.UnitFlags;
        uint dynamicFlags = player.GetUInt32(UpdateFields.UnitDynamicFlags);
        uint mapId = player.MapId;
        bool alive = player.IsAlive;
        if (!_hasState)
        {
            _hasState = true;
        }
        else
        {
            if (death != _death)
            {
                Write("state", string.Create(CultureInfo.InvariantCulture, $"DeathState {_death} -> {death}"));
            }

            if (playerFlags != _playerFlags)
            {
                Write("state", string.Create(CultureInfo.InvariantCulture, $"PlayerFlags {_playerFlags} -> {playerFlags}"));
            }

            if (unitFlags != _unitFlags)
            {
                Write("state", string.Create(CultureInfo.InvariantCulture, $"UnitFlags {_unitFlags} -> {unitFlags}"));
            }

            if (dynamicFlags != _dynamicFlags)
            {
                Write("state", string.Create(CultureInfo.InvariantCulture, $"UnitDynamicFlags 0x{_dynamicFlags:X} -> 0x{dynamicFlags:X}"));
            }

            if (mapId != _mapId)
            {
                Write("state", string.Create(CultureInfo.InvariantCulture, $"MapId {_mapId} -> {mapId}"));
            }

            if (alive != _alive)
            {
                Write(alive ? "resurrected" : "died", string.Empty);
            }
        }

        _death = death;
        _playerFlags = playerFlags;
        _unitFlags = unitFlags;
        _dynamicFlags = dynamicFlags;
        _mapId = mapId;
        _alive = alive;
    }
}
