using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
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
            }
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
