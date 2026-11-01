using ArcaneCore.Game.ClientErrors;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Net;

/// <summary>
/// The session's hooks into the client-visible failure log (<see cref="ClientRejectionLog"/>) and the duplicate outbound packet
/// detector (<see cref="DuplicatePacketDetector"/>); docs/ops/observability.md. Both are resolved once from the connection's services
/// (absent or disabled: the fields stay null and <see cref="Send(WorldOpcode, ReadOnlySpan{byte})"/> pays one null check each).
/// </summary>
public sealed partial class WorldSession
{
    private readonly object _rejectionGate = new();
    private ClientRejectionLog? _rejections;
    private TokenBucket _rejectionBucket;
    private DuplicatePacketDetector? _duplicates;
    private DuplicatePacketDetector.SessionState? _duplicateState;
    private int _rejectionsSent;
    private int _lootReleasesSent;
    private readonly RejectionStormTracker _rejectionStorm = new();
    private long _closedAtTick;
    private int _sendAfterCloseReported;
    private int _kickRecorded;
    private string? _kickReason;
    private int _logoutOverdueReported;

    /// <summary>Longest a session may stay closed while something still sends to it before that is reported (the world thread removes the player within a tick or two).</summary>
    internal long SendAfterCloseGraceMs { get; set; } = 5_000;

    /// <summary>Longest a logout may run past its countdown before it is reported as never completed.</summary>
    internal uint LogoutOverdueGraceMs { get; set; } = 30_000;

    /// <summary>The reason recorded by <see cref="Kick(string)"/>, or null (not kicked, or kicked without a reason).</summary>
    internal string? KickReason => Volatile.Read(ref _kickReason);

    /// <summary>The stable ledger subject of this session: its account (never positions or times).</summary>
    internal string AnomalySubject => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"session account {AccountId}");

    private void InitObservability(IServiceProvider services)
    {
        if (services.GetService<ClientRejectionLog>() is { Enabled: true } log)
        {
            _rejections = log;
            _rejectionBucket = log.NewBucket();
        }

        if (services.GetService<DuplicatePacketDetector>() is { Enabled: true } detector)
        {
            _duplicates = detector; // the per-session state is made on the first checked send: a managed bot is only known after construction
        }
    }

    /// <summary>Refusals this session has been sent (counted whether or not the line was rate limited away); a handler compares it before and after to tell whether it answered.</summary>
    internal int ClientRejectionsSent => Volatile.Read(ref _rejectionsSent);

    /// <summary>
    /// Replies this session has been sent that answer a CMSG_LOOT: the refusals above plus SMSG_LOOT_RELEASE_RESPONSE (LootService.Refuse
    /// answers a refused open with it, and it is not a decoded refusal because the same packet also closes a normal loot window).
    /// </summary>
    internal int ClientLootAnswersSent => Volatile.Read(ref _rejectionsSent) + Volatile.Read(ref _lootReleasesSent);

    /// <summary>True when a handler that is about to drop a request should build its reason text (the silent-drop log is on and its level, Debug for a bot, Information for a player, is enabled). Check it before allocating the text.</summary>
    internal bool SilentDropLogEnabled => _rejections is { } rejections && rejections.WouldLogSilentDrop(IsPlayerbot);

    private SessionLogContext LogContext()
    {
        Player? player = Player;
        return new SessionLogContext(IsPlayerbot, AccountId, player is null ? 0 : player.Guid.Low, player?.MapId ?? 0);
    }

    /// <summary>Called by Send for every outbound packet: decode refusals and look for duplicates. Never throws.</summary>
    private void ObserveOutbound(WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        if (_rejections is { } rejections)
        {
            if (opcode == WorldOpcode.SmsgLootReleaseResponse)
            {
                Interlocked.Increment(ref _lootReleasesSent);
            }
            else if (ClientRejectionDecoder.IsCandidate(opcode) && ClientRejectionDecoder.TryDecode(opcode, payload, out ClientRejection rejection))
            {
                Interlocked.Increment(ref _rejectionsSent);
                rejections.LogOutbound(_rejectionGate, ref _rejectionBucket, LogContext(), opcode, rejection);
                NoteRejectionForStorm(opcode, rejection);
                NoteServerErrorRejection(opcode, rejection);
                NoteRejectionSpike(rejection);
            }
        }
        else if (ClientRejectionDecoder.IsCandidate(opcode) && ClientRejectionDecoder.TryDecode(opcode, payload, out ClientRejection unlogged))
        {
            NoteRejectionForStorm(opcode, unlogged);
            NoteServerErrorRejection(opcode, unlogged);
            NoteRejectionSpike(unlogged);
        }

        if (_duplicates is { } detector && (!_managed || detector.IncludeBots))
        {
            DuplicatePacketDetector.SessionState state = _duplicateState ?? InitDuplicateState(detector);
            if (detector.Observe(state, opcode, payload, out DuplicatePacketHit hit))
            {
                detector.Report(state, LogContext(), hit);
            }
        }
    }

    /// <summary>
    /// H1 outcome check <c>session/rejection-storm</c>: a handler that keeps refusing one account's client (RejectionStormTracker.Threshold refusals within
    /// RejectionStormTracker.WindowMs) is looping on a request nobody fixes. One report per storm; a window below the threshold re-arms it. A synthetic
    /// chaos probe is expected to be refused and is not counted. Cost: one lock and a few integer operations per refusal sent.
    /// </summary>
    private void NoteRejectionForStorm(WorldOpcode opcode, in ClientRejection rejection)
    {
        if (AccountId == 0 || IsInChaosProbe)
        {
            return;
        }

        int count;
        lock (_rejectionGate)
        {
            if (!_rejectionStorm.Note(Environment.TickCount64, out count))
            {
                return;
            }
        }

        ArcaneCore.Kernel.Diagnostics.AnomalyLedger.Shared.Report(
            "session",
            "rejection-storm",
            AnomalySubject,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{count} client refusals within {RejectionStormTracker.WindowMs} ms; latest {rejection.Kind} {rejection.ReasonName} for {WorldOpcodeNames.GetName(opcode)} (entry {rejection.Entry}, bot {IsPlayerbot})"));
    }

    /// <summary>
    /// Outcome check <c>client/rejection-server-error</c>: a refusal whose reason says the server itself failed (<see cref="ClientRejectionDecoder.IsServerError"/>:
    /// a mail or auction internal error, an unknown cast failure, an item or vendor the server cannot find), not that the player asked for something not allowed.
    /// A synthetic chaos probe is expected to be refused and is not reported. Cost on the hot path: one table load per decoded refusal; the text is built only when it reports.
    /// </summary>
    private void NoteServerErrorRejection(WorldOpcode opcode, in ClientRejection rejection)
    {
        if (!ClientRejectionDecoder.IsServerError(rejection) || IsInChaosProbe)
        {
            return;
        }

        ArcaneCore.Kernel.Diagnostics.AnomalyLedger.Shared.Report(
            "client",
            "rejection-server-error",
            AnomalySubject,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{rejection.Kind} {rejection.ReasonName} sent for {WorldOpcodeNames.GetName(opcode)} (entry {rejection.Entry}, bot {IsPlayerbot})"));
    }

    private static readonly ClientRejectionRateTracker RejectionRates = new(static () => Environment.TickCount64);

    /// <summary>
    /// Outcome check <c>client/rejection-spike</c>: one (kind, reason) refusal sent at least <see cref="ClientRejectionRateTracker.Threshold"/> times to at least
    /// <see cref="ClientRejectionRateTracker.MinAccounts"/> accounts within <see cref="ClientRejectionRateTracker.WindowMs"/> ms is a server-wide defect, not one client.
    /// Chaos probes and sessions without an account are not counted. Cost: two interlocked operations per decoded refusal; the text is built only when it reports.
    /// </summary>
    private void NoteRejectionSpike(in ClientRejection rejection)
    {
        if (AccountId == 0 || IsInChaosProbe || !RejectionRates.Note(rejection.Kind, rejection.Reason, (uint)AccountId, out long count, out int accounts))
        {
            return;
        }

        ArcaneCore.Kernel.Diagnostics.AnomalyLedger.Shared.Report(
            "client",
            "rejection-spike",
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{rejection.Kind} {rejection.ReasonName}"),
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{count} refusals sent to at least {accounts} accounts within {ClientRejectionRateTracker.WindowMs / 60_000} minutes (latest entry {rejection.Entry}, bot {IsPlayerbot})"));
    }

    /// <summary>Opcode values (by index) that are never reported as unhandled: obsolete opcodes (a name that says so) and values with no reference name.</summary>
    private static readonly bool[] UnhandledNotReported = BuildUnhandledTables(gameplay: false);

    /// <summary>Opcode values (by index) that are real gameplay requests: an unhandled one is a Warning, any other reported opcode an Info.</summary>
    private static readonly bool[] UnhandledGameplay = BuildUnhandledTables(gameplay: true);

    private static bool[] BuildUnhandledTables(bool gameplay)
    {
        WorldOpcode[] known = Enum.GetValues<WorldOpcode>();
        var table = new bool[known.Max(o => (int)o) + 1];
        WorldOpcode[] gameplayOpcodes = [WorldOpcode.CmsgOpeningCinematic, WorldOpcode.CmsgGmsurveySubmit, WorldOpcode.CmsgStoreLootInSlot, WorldOpcode.CmsgSkillBuyStep];
        foreach (WorldOpcode opcode in known)
        {
            table[(int)opcode] = gameplay
                ? Array.IndexOf(gameplayOpcodes, opcode) >= 0
                : WorldOpcodeNames.GetName(opcode).Contains("OBSOLETE", StringComparison.OrdinalIgnoreCase);
        }

        return table;
    }

    /// <summary>
    /// Outcome check <c>session/opcode-unhandled</c>: a client packet reached the session with no registered handler and was dropped. Warning for a real gameplay
    /// request (the client feature silently does nothing), Info for any other named opcode; obsolete opcodes and unnamed values are not reported. A stripped developer
    /// opcode has a (stripped) handler and never gets here. The subject is the opcode name, so the ledger's subject count says how many opcodes are missing.
    /// Cost: two table loads, then the ledger call (no allocation of its own: the subject is the tracker's label).
    /// </summary>
    private static void NoteUnhandledOpcode(WorldOpcode opcode)
    {
        int index = (ushort)opcode;
        if (index >= UnhandledNotReported.Length || UnhandledNotReported[index] || UnhandledOpcodeTracker.LabelOf(opcode) == UnhandledOpcodeTracker.UnknownLabel)
        {
            return;
        }

        ArcaneCore.Kernel.Diagnostics.AnomalyLedger.Shared.Report(
            "session",
            "opcode-unhandled",
            UnhandledOpcodeTracker.LabelOf(opcode),
            "a client packet had no registered handler and was dropped",
            UnhandledGameplay[index] ? ArcaneCore.Kernel.Diagnostics.AnomalySeverity.Warning : ArcaneCore.Kernel.Diagnostics.AnomalySeverity.Info);
    }

    /// <summary>
    /// H1 outcome check <c>session/send-after-close</c> (Info): a packet was sent to a session that has been closed for more than
    /// <see cref="SendAfterCloseGraceMs"/>, so a player or object kept talking to a logged-out connection (the player was not removed
    /// from the world). One report per session. Runs only on the closed path of Send.
    /// </summary>
    private void NoteSendAfterClose(WorldOpcode opcode)
    {
        long closedAt = Volatile.Read(ref _closedAtTick);
        if (closedAt == 0 || AccountId == 0 || Environment.TickCount64 - closedAt < SendAfterCloseGraceMs
            || Interlocked.Exchange(ref _sendAfterCloseReported, 1) != 0)
        {
            return;
        }

        ArcaneCore.Kernel.Diagnostics.AnomalyLedger.Shared.Report(
            "session",
            "send-after-close",
            AnomalySubject,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{WorldOpcodeNames.GetName(opcode)} sent {Environment.TickCount64 - closedAt} ms after the session closed; the player is still being driven (in world: {Player is not null})"),
            ArcaneCore.Kernel.Diagnostics.AnomalySeverity.Info);
    }

    /// <summary>
    /// H1 outcome check <c>session/kick-no-reason</c> (Info): the session was kicked by a caller that recorded no reason
    /// (<see cref="Kick()"/>, not <see cref="Kick(string)"/>), so the disconnect would be unexplained. The detail names the calling method.
    /// Not reported before authentication, once the session is already closed (the socket went away), or for the console. Runs once per session.
    /// </summary>
    private void ReportKickWithoutReason()
    {
        if (AccountId == 0 || _state is ArcaneCore.World.Handlers.SessionState.Connected or ArcaneCore.World.Handlers.SessionState.Closed)
        {
            return;
        }

        string caller = "unknown";
        try
        {
            // The first frame outside this class, not a fixed depth: the JIT may inline Kick() into its caller, which removes a frame
            // (a fixed skip of 3 then named the caller's caller), and may inline this report and KickCore as well, so no frame is skipped.
            // WorldSession itself must not call Kick(): its frame would be skipped and the report would name its caller instead.
            foreach (System.Diagnostics.StackFrame stackFrame in new System.Diagnostics.StackTrace(false).GetFrames())
            {
                if (stackFrame.GetMethod() is { } method && method.DeclaringType != typeof(WorldSession))
                {
                    caller = (method.DeclaringType?.Name ?? "?") + "." + method.Name;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // the caller stays unknown; the report is what matters
        }

        ArcaneCore.Kernel.Diagnostics.AnomalyLedger.Shared.Report(
            "session",
            "kick-no-reason",
            AnomalySubject,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"session kicked with no recorded reason by {caller} (state {_state}, bot {IsPlayerbot})"),
            ArcaneCore.Kernel.Diagnostics.AnomalySeverity.Info);
    }

    /// <summary>
    /// H1 outcome check <c>session/logout-overdue</c>: a logout countdown that ran <see cref="LogoutOverdueGraceMs"/> past
    /// <c>World:LogoutDelayMs</c> without the map completing it. Called for each in-world session each tick: one nullable test while not logging out.
    /// One report per session.
    /// </summary>
    private void CheckLogoutOverdue(Player player)
    {
        if (!player.IsLoggingOut || _logoutOverdueReported != 0
            || !player.IsLogoutDue(World.NowMs, unchecked(World.Options.LogoutDelayMs + LogoutOverdueGraceMs))
            || Interlocked.Exchange(ref _logoutOverdueReported, 1) != 0)
        {
            return;
        }

        ArcaneCore.Kernel.Diagnostics.AnomalyLedger.Shared.Report(
            "session",
            "logout-overdue",
            AnomalySubject,
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"logout of '{player.Name}' (guid {player.Guid.Low}) did not complete {LogoutOverdueGraceMs} ms after its {World.Options.LogoutDelayMs} ms countdown (map {player.MapId}, in map {player.Map is not null})"));
    }

    /// <summary>True once the duplicate detector has allocated this session's state (tests: a managed bot must never get one while bots are excluded).</summary>
    internal bool HasDuplicateState => Volatile.Read(ref _duplicateState) is not null;

    private DuplicatePacketDetector.SessionState InitDuplicateState(DuplicatePacketDetector detector)
    {
        Interlocked.CompareExchange(ref _duplicateState, detector.NewSession(), null);
        return _duplicateState!;
    }

    /// <summary>
    /// A handler is about to return without answering the client: log why (docs/ops/observability.md). Players log at Information, bots at
    /// Debug, rate limited per session.
    /// </summary>
    internal void LogSilentDrop(WorldOpcode request, string reason, ulong target = 0, uint entry = 0, uint arg = 0)
        => _rejections?.LogSilentDrop(_rejectionGate, ref _rejectionBucket, LogContext(), request, reason, target, entry, arg);
}
