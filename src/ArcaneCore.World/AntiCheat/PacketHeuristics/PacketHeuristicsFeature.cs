using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Logging;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.AntiCheat.PacketHeuristics;

/// <summary>
/// Report-only heuristics over the packets sessions send (docs/areas/packet-heuristics.md): wrong-state, unknown-opcode, malformed, shape,
/// cadence, replay, orphan-ack and incapacitated-action findings go to the anomaly ledger under subsystem <c>packet</c>. Off by default.
/// <para>
/// The one rule: this class observes and never decides. No method here returns a verdict to the session, throws, blocks, drops, delays or alters
/// a packet, kicks, or touches the AntiCheat score; a packet that dispatched before still dispatches and one that was dropped is still dropped.
/// The sessions call it through <c>WorldSession.PacketHeuristics.cs</c> and pay one volatile read when it is off.
/// </para>
/// <para>
/// Threads: the settings are an immutable snapshot swapped by reference (any thread); counters are <see cref="Interlocked"/>; per-session state
/// has its own lock (<see cref="PacketHeuristicsSession"/>). There is no global lock. Findings are built and reported on the reporting thread
/// (the network task for the client path, the world thread for world packets and bots), never from a lock the ledger might also take.
/// </para>
/// </summary>
public sealed class PacketHeuristicsFeature : IWorldFeature
{
    private readonly IServiceProvider _services;
    private readonly ILogger? _logger;
    private readonly long[] _counts = new long[PacketChecks.Count * PacketChecks.TrafficKinds];
    private PacketHeuristicsSettings _settings;
    private volatile bool _active;
    private int _epoch;
    private readonly object _applyGate = new();
    private TeleportFeature? _teleport;

    /// <summary>Creates the feature with the options of the <c>World:PacketHeuristics</c> section (defaults when there is none or it is invalid; the startup check refuses an invalid one).</summary>
    public PacketHeuristicsFeature(IServiceProvider services)
    {
        _services = services;
        _logger = services.GetService<ILoggerFactory>()?.CreateLogger("ArcaneCore.PacketHeuristics");
        PacketHeuristicsOptions configured = services.GetService<IOptions<PacketHeuristicsOptions>>()?.Value ?? new PacketHeuristicsOptions();
        IReadOnlyList<string> problems = configured.Validate();
        if (problems.Count > 0)
        {
            _logger?.LogError("{Section} is invalid ({Problems}); packet heuristics stay off with the defaults", PacketHeuristicsOptions.SectionName, string.Join(" ", problems));
            configured = new PacketHeuristicsOptions();
        }

        _settings = new PacketHeuristicsSettings(configured);
        _active = _settings.Enabled;
    }

    /// <summary>The ledger the findings go to; the process-wide one unless a test supplies its own.</summary>
    internal AnomalyLedger Ledger { get; set; } = AnomalyLedger.Shared;

    /// <summary>Monotonic milliseconds; replaceable so tests control time.</summary>
    internal Func<long> NowMs { get; set; } = Clock.Milliseconds;

    /// <summary>True while the checks run (the hot-path switch: one volatile read).</summary>
    public bool Active => _active;

    /// <summary>A copy of the options in force (including runtime changes).</summary>
    public PacketHeuristicsOptions Options => Volatile.Read(ref _settings).Options.Clone();

    internal PacketHeuristicsSettings Settings => Volatile.Read(ref _settings);

    /// <inheritdoc/>
    public void Attach(ArcaneCore.Game.Maps.WorldRuntime world)
    {
        _logger?.LogInformation("Packet heuristics {State} (report only; .packetcheck on|off switches it)", _active ? "enabled" : "disabled");
    }

    /// <summary>Replaces the options (any thread). False, with the problems, when they are invalid; nothing changes then.</summary>
    public bool TryApply(PacketHeuristicsOptions options, out IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(options);
        problems = options.Validate();
        if (problems.Count > 0)
        {
            return false;
        }

        var settings = new PacketHeuristicsSettings(options);
        lock (_applyGate)
        {
            Volatile.Write(ref _settings, settings);
            _active = settings.Enabled;
        }

        return true;
    }

    /// <summary>Switches the checks on or off at runtime (until the next restart).</summary>
    public void SetEnabled(bool enabled) => Mutate(o => o.Enabled = enabled);

    /// <summary>Includes (false) or exempts (true) the managed playerbot sessions at runtime.</summary>
    public void SetExemptBots(bool exempt) => Mutate(o => o.ExemptBots = exempt);

    /// <summary>Includes (false) or exempts (true) staff sessions at runtime.</summary>
    public void SetExemptStaff(bool exempt) => Mutate(o => o.ExemptStaff = exempt);

    /// <summary>Zeroes the counters and lets every session report again at once (the report throttle restarts). Findings already in the ledger stay.</summary>
    public void Reset()
    {
        for (int i = 0; i < _counts.Length; i++)
        {
            Interlocked.Exchange(ref _counts[i], 0);
        }

        Interlocked.Increment(ref _epoch);
    }

    /// <summary>How many times <paramref name="check"/> found something in <paramref name="traffic"/> since start or <see cref="Reset"/> (every occurrence, reported or throttled).</summary>
    public long Count(PacketCheck check, PacketTraffic traffic) => Interlocked.Read(ref _counts[Slot(check, traffic)]);

    /// <summary>The status lines of <c>.packetcheck status</c>.</summary>
    public IReadOnlyList<string> StatusLines()
    {
        PacketHeuristicsSettings settings = Settings;
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"Packet heuristics: {(settings.Enabled ? "ON" : "off")} (report only). Bots {(settings.ExemptBots ? "exempt" : "INCLUDED")}, staff {(settings.ExemptStaff ? "exempt" : "INCLUDED")}, one ledger report per session, check and opcode per {settings.ReportIntervalMs / 1000} s."),
            "check                   enabled     player        bot      staff       tool",
        };
        for (int i = 0; i < PacketChecks.Count; i++)
        {
            var check = (PacketCheck)i;
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{PacketChecks.Code(check),-22}  {(settings.CheckOn(check) ? "yes" : "no"),-7} {Count(check, PacketTraffic.Player),10} {Count(check, PacketTraffic.Bot),10} {Count(check, PacketTraffic.Staff),10} {Count(check, PacketTraffic.Tool),10}"));
        }

        lines.Add("Findings are in the anomaly ledger under packet/<check> (.server anomalies).");
        return lines;
    }

    // --- admission -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether <paramref name="session"/> is checked right now and, when it is, its state and traffic kind. Disabled or exempt: false, no state is
    /// allocated. A managed bot probe the chaos engine dispatches on purpose is never checked.
    /// </summary>
    private bool Admit(WorldSession session, out PacketHeuristicsSettings settings, out PacketHeuristicsSession state, out PacketTraffic traffic)
    {
        settings = Volatile.Read(ref _settings);
        state = null!;
        traffic = PacketTraffic.Player;
        if (!settings.Enabled || session.IsInChaosProbe)
        {
            return false;
        }

        PacketHeuristicsSession? existing = session.HeuristicsState;
        if (session.HeuristicsToolPacket)
        {
            // An operator's injected packet is always visible: neither the bot nor the staff exemption applies to the tool.
            traffic = PacketTraffic.Tool;
        }
        else if (session.IsPlayerbot)
        {
            traffic = PacketTraffic.Bot;
            if (settings.ExemptBots)
            {
                return false;
            }
        }
        else if (session.IsHeuristicsStaff)
        {
            traffic = PacketTraffic.Staff;
            if (settings.ExemptStaff)
            {
                return false;
            }
        }

        state = existing ?? session.EnsureHeuristicsState();
        return true;
    }

    private static int Slot(PacketCheck check, PacketTraffic traffic) => ((int)check * PacketChecks.TrafficKinds) + (int)traffic;

    // --- the entry points the sessions call ----------------------------------------------------------------------------------

    /// <summary>
    /// A packet a client session received (network path, after authentication): the shape, cadence and replay checks. Reports after the session
    /// state lock is released.
    /// </summary>
    internal void OnClientPacket(WorldSession session, WorldOpcode opcode, byte[] payload)
    {
        if (Admit(session, out PacketHeuristicsSettings settings, out PacketHeuristicsSession state, out PacketTraffic traffic))
        {
            Inspect(session, settings, state, traffic, opcode, payload);
        }
    }

    /// <summary>A packet a managed bot dispatches to a world handler (world thread): shape, cadence and replay, then the incapacitated-action check.</summary>
    internal void OnManagedPacket(WorldSession session, Player player, WorldOpcode opcode, byte[] payload)
    {
        if (Admit(session, out PacketHeuristicsSettings settings, out PacketHeuristicsSession state, out PacketTraffic traffic))
        {
            Inspect(session, settings, state, traffic, opcode, payload);
            CheckIncapacitated(session, settings, state, traffic, player, opcode);
        }
    }

    /// <summary>A client's world packet about to run its handler on the world thread: the incapacitated-action check (the session refreshed its GM flag before calling).</summary>
    internal void OnWorldPacket(WorldSession session, Player player, WorldOpcode opcode)
    {
        if (Admit(session, out PacketHeuristicsSettings settings, out PacketHeuristicsSession state, out PacketTraffic traffic))
        {
            CheckIncapacitated(session, settings, state, traffic, player, opcode);
        }
    }

    /// <summary>
    /// A packet dropped because of the session state (<paramref name="reason"/> is a constant that says where; <paramref name="state"/> is appended to
    /// the detail only when a report goes out, so nothing is built for a throttled finding).
    /// </summary>
    internal void WrongState(WorldSession session, WorldOpcode opcode, string reason, SessionState? state = null, AnomalySeverity severity = AnomalySeverity.Warning)
    {
        if (Admit(session, out PacketHeuristicsSettings settings, out PacketHeuristicsSession sessionState, out PacketTraffic traffic) && settings.CheckOn(PacketCheck.WrongState))
        {
            Report(session, settings, sessionState, traffic, PacketCheck.WrongState, opcode, severity, (reason, state),
                static s => s.state is { } st ? s.reason + " (state " + st + ")" : s.reason);
        }
    }

    /// <summary>A packet with no handler, or outside the table: Info for a named opcode, Warning for an unnamed one or a server opcode sent by a client.</summary>
    internal void Unhandled(WorldSession session, WorldOpcode opcode, int length)
    {
        if (!Admit(session, out PacketHeuristicsSettings settings, out PacketHeuristicsSession state, out PacketTraffic traffic) || !settings.CheckOn(PacketCheck.UnknownOpcode))
        {
            return;
        }

        OpcodeNameKind kind = PacketOpcodeClasses.NameKindOf(opcode);
        if (kind == OpcodeNameKind.Unnamed)
        {
            Report(session, settings, state, traffic, PacketCheck.UnknownOpcode, opcode, AnomalySeverity.Warning, length,
                static length => string.Create(CultureInfo.InvariantCulture, $"outside the opcode table, {length} bytes"));
        }
        else if (kind == OpcodeNameKind.Server)
        {
            Report(session, settings, state, traffic, PacketCheck.UnknownOpcode, opcode, AnomalySeverity.Warning, length,
                static length => string.Create(CultureInfo.InvariantCulture, $"server-to-client opcode sent by the client, {length} bytes"));
        }
        else
        {
            Report(session, settings, state, traffic, PacketCheck.UnknownOpcode, opcode, AnomalySeverity.Info, length,
                static length => string.Create(CultureInfo.InvariantCulture, $"no handler, {length} bytes"));
        }
    }

    /// <summary>A stripped developer opcode the session dropped unread (never a staff session: those are audited instead).</summary>
    internal void DevOpcode(WorldSession session, WorldOpcode opcode, int length)
    {
        if (Admit(session, out PacketHeuristicsSettings settings, out PacketHeuristicsSession state, out PacketTraffic traffic) && settings.CheckOn(PacketCheck.DevOpcode))
        {
            Report(session, settings, state, traffic, PacketCheck.DevOpcode, opcode, AnomalySeverity.Warning, length,
                static length => string.Create(CultureInfo.InvariantCulture, $"stripped developer opcode, {length} bytes, dropped"));
        }
    }

    /// <summary>A payload a handler rejected as malformed (before the session disconnects), or a bad frame header (<paramref name="opcode"/> then is whatever the header named).</summary>
    internal void Malformed(WorldSession session, WorldOpcode opcode, int length, string reason)
    {
        if (Admit(session, out PacketHeuristicsSettings settings, out PacketHeuristicsSession state, out PacketTraffic traffic) && settings.CheckOn(PacketCheck.Malformed))
        {
            Report(session, settings, state, traffic, PacketCheck.Malformed, opcode, AnomalySeverity.Error, (length, reason),
                static s => string.Create(CultureInfo.InvariantCulture, $"{s.length} bytes: {s.reason}"));
        }
    }

    /// <summary>A frame header the reader refused (size or opcode out of range); <paramref name="opcode"/> is what the header named when it fits the table.</summary>
    internal void BadHeader(WorldSession session, WorldOpcode opcode, int size, uint rawOpcode)
    {
        if (Admit(session, out PacketHeuristicsSettings settings, out PacketHeuristicsSession state, out PacketTraffic traffic) && settings.CheckOn(PacketCheck.Malformed))
        {
            Report(session, settings, state, traffic, PacketCheck.Malformed, opcode, AnomalySeverity.Error, (size, rawOpcode),
                static s => string.Create(CultureInfo.InvariantCulture, $"bad packet header (size {s.size}, opcode 0x{s.rawOpcode:X})"));
        }
    }

    /// <summary>A movement acknowledgement the handler ignores because it matches nothing the server sent.</summary>
    internal void OrphanAck(WorldSession session, WorldOpcode opcode, string reason)
    {
        if (Admit(session, out PacketHeuristicsSettings settings, out PacketHeuristicsSession state, out PacketTraffic traffic) && settings.CheckOn(PacketCheck.OrphanAck))
        {
            Report(session, settings, state, traffic, PacketCheck.OrphanAck, opcode, AnomalySeverity.Warning, reason, static reason => reason);
        }
    }

    // --- the checks ----------------------------------------------------------------------------------------------------------

    private void Inspect(WorldSession session, PacketHeuristicsSettings settings, PacketHeuristicsSession state, PacketTraffic traffic, WorldOpcode opcode, byte[] payload)
    {
        long now = NowMs();
        int expected = settings.CheckOn(PacketCheck.Shape) ? PacketShapeTable.FixedLength(opcode) : PacketShapeTable.NotFixed;
        CadenceClass kind = settings.CheckOn(PacketCheck.Cadence) ? PacketOpcodeClasses.ClassOf(opcode) : CadenceClass.None;
        bool replayed = false;
        int repeats = 0;
        bool fast = false;
        int inWindow = 0;
        long pingGap = 0;
        lock (state.Gate)
        {
            if (kind == CadenceClass.Ping)
            {
                fast = settings.PingMinIntervalMs > 0 && state.NotePing(now, settings.PingMinIntervalMs, out pingGap);
            }
            else if (kind != CadenceClass.None && settings.CeilingOf(kind) is var ceiling and > 0)
            {
                fast = state.NoteCadence(kind, now, ceiling, out inWindow);
            }

            if (settings.CheckOn(PacketCheck.Replay) && (uint)opcode < PacketHeuristicsOptions.OpcodeSpace && settings.ReplayChecked[(int)opcode])
            {
                replayed = state.NoteReplay(opcode, payload, now, settings.ReplayCount, settings.ReplayWindowMs, out repeats);
            }
        }

        if (expected >= 0 && payload.Length != expected)
        {
            Report(session, settings, state, traffic, PacketCheck.Shape, opcode, AnomalySeverity.Warning, (payload.Length, expected),
                static s => s.Length > s.expected
                    ? string.Create(CultureInfo.InvariantCulture, $"trailing {s.Length - s.expected} bytes (length {s.Length}, the message is exactly {s.expected})")
                    : string.Create(CultureInfo.InvariantCulture, $"short by {s.expected - s.Length} bytes (length {s.Length}, the message is exactly {s.expected})"));
        }

        if (fast)
        {
            if (kind == CadenceClass.Ping)
            {
                Report(session, settings, state, traffic, PacketCheck.Cadence, opcode, AnomalySeverity.Warning, (pingGap, settings.PingMinIntervalMs),
                    static s => string.Create(CultureInfo.InvariantCulture, $"overspeed-ping: {s.pingGap} ms after the previous one (< {s.PingMinIntervalMs} ms)"));
            }
            else
            {
                Report(session, settings, state, traffic, PacketCheck.Cadence, opcode, AnomalySeverity.Warning, (kind, inWindow, settings.CeilingOf(kind)),
                    static s => string.Create(CultureInfo.InvariantCulture, $"{s.kind} class {s.inWindow} within a second > {s.Item3}/s"));
            }
        }

        if (replayed)
        {
            Report(session, settings, state, traffic, PacketCheck.Replay, opcode, AnomalySeverity.Warning, (repeats, settings.ReplayWindowMs, payload.Length),
                static s => string.Create(CultureInfo.InvariantCulture, $"identical payload ({s.Length} bytes) {s.repeats} times within {s.ReplayWindowMs} ms"));
        }
    }

    private void CheckIncapacitated(WorldSession session, PacketHeuristicsSettings settings, PacketHeuristicsSession state, PacketTraffic traffic, Player player, WorldOpcode opcode)
    {
        if (!settings.CheckOn(PacketCheck.IncapacitatedAction))
        {
            return;
        }

        PacketOpcodeClasses.Incapacity applicable = PacketOpcodeClasses.ActionMask(opcode);
        if (applicable == PacketOpcodeClasses.Incapacity.None)
        {
            return;
        }

        string? why = null;
        if ((applicable & PacketOpcodeClasses.Incapacity.Dead) != 0 && !player.IsAlive)
        {
            why = "dead or ghost";
        }
        else if ((applicable & PacketOpcodeClasses.Incapacity.Taxi) != 0 && (player.UnitFlags & ArcaneCore.Game.UnitFlags.TaxiFlight) != 0)
        {
            why = "on a taxi flight";
        }
        else if ((applicable & PacketOpcodeClasses.Incapacity.Teleporting) != 0
            && InFarTransfer(player))
        {
            why = "in a far transfer";
        }

        if (why is not null)
        {
            Report(session, settings, state, traffic, PacketCheck.IncapacitatedAction, opcode,
                traffic == PacketTraffic.Bot ? AnomalySeverity.Info : AnomalySeverity.Warning, why, static why => "player is " + why);
        }
    }

    /// <summary>
    /// Whether <paramref name="player"/> is crossing a far teleport right now: scheduled, loading (SMSG_TRANSFER_PENDING to the world-port
    /// acknowledgement) or arriving. The map is null for all of it, so a packet in flight is not a finding. A near teleport (a Blink) is not
    /// a transfer: the client keeps playing and may legitimately act within one round trip.
    /// </summary>
    internal bool InFarTransfer(Player player)
        => (_teleport ??= _services.GetService<TeleportFeature>())?.TeleportsIfAttached?.IsBeingTeleportedFar(player) == true;

    // --- reporting -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Reports the occurrence to the ledger unless the same session, check and opcode already reported inside the interval. The
    /// detail text is only built for a report that goes out, so a flood of one finding costs a counter and a dictionary lookup.
    /// </summary>
    private void Report<TState>(WorldSession session, PacketHeuristicsSettings settings, PacketHeuristicsSession state, PacketTraffic traffic,
        PacketCheck check, WorldOpcode opcode, AnomalySeverity severity, TState detailState, Func<TState, string> detail)
    {
        int suppressed;
        bool report;
        lock (state.Gate)
        {
            report = state.ShouldReport(check, opcode, NowMs(), settings.ReportIntervalMs, Volatile.Read(ref _epoch), out suppressed);
        }

        if (report)
        {
            string text = WorldOpcodeNames.GetName(opcode) + " " + detail(detailState);
            if (suppressed > 0)
            {
                text += string.Create(CultureInfo.InvariantCulture, $" (+{suppressed} more since the last report)");
            }

            Ledger.Report(PacketChecks.Subsystem, PacketChecks.Code(check), SubjectOf(session, traffic), text, severity);
        }

        // Counted last: a reader that sees the count also finds the ledger report of that occurrence (when it was not held back).
        Interlocked.Increment(ref _counts[Slot(check, traffic)]);
    }

    /// <summary>The ledger subject: <c>account N</c> for a player or staff session, <c>botname NAME</c> for a managed bot, <c>tool N</c> (the target's account) for a packet a packet tool injected. Never the <c>bot-</c> subsystem prefix that files bot QA issues, and not a leading <c>bot </c> either: PlayerbotPolicyTelemetry counts every anomaly subject that starts with that.</summary>
    private static string SubjectOf(WorldSession session, PacketTraffic traffic)
    {
        if (traffic == PacketTraffic.Tool)
        {
            return string.Create(CultureInfo.InvariantCulture, $"tool {session.AccountId}");
        }

        if (traffic == PacketTraffic.Bot)
        {
            return "botname " + (session.Player?.Name is { Length: > 0 } name ? name : session.AccountName);
        }

        return string.Create(CultureInfo.InvariantCulture, $"account {session.AccountId}");
    }

    private void Mutate(Action<PacketHeuristicsOptions> change)
    {
        // Read-modify-write under the gate so two concurrent toggles (chat and RA) cannot lose one another's change.
        lock (_applyGate)
        {
            PacketHeuristicsOptions options = Settings.Options.Clone();
            change(options);
            if (!TryApply(options, out IReadOnlyList<string> problems))
            {
                // The values come from validated options and a boolean toggle; this cannot happen, but a refused change must not be silent.
                _logger?.LogError("Packet heuristics change refused: {Problems}", string.Join(" ", problems));
            }
        }
    }
}
