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

    private void InitObservability(IServiceProvider services)
    {
        if (services.GetService<ClientRejectionLog>() is { Enabled: true } log)
        {
            _rejections = log;
            _rejectionBucket = log.NewBucket();
        }

        if (services.GetService<DuplicatePacketDetector>() is { Enabled: true } detector)
        {
            _duplicates = detector;
            _duplicateState = detector.NewSession();
        }
    }

    /// <summary>Refusals this session has been sent (counted whether or not the line was rate limited away); a handler compares it before and after to tell whether it answered.</summary>
    internal int ClientRejectionsSent => Volatile.Read(ref _rejectionsSent);

    /// <summary>True when a handler that is about to drop a request should build its reason text (the silent-drop log is on). Check it before allocating the text.</summary>
    internal bool SilentDropLogEnabled => _rejections is { LogSilentDrops: true };

    private SessionLogContext LogContext()
    {
        Player? player = Player;
        return new SessionLogContext(IsPlayerbot, AccountId, player is null ? 0 : player.Guid.Low, player?.MapId ?? 0);
    }

    /// <summary>Called by Send for every outbound packet: decode refusals and look for duplicates. Never throws.</summary>
    private void ObserveOutbound(WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        if (_rejections is { } rejections && ClientRejectionDecoder.IsCandidate(opcode)
            && ClientRejectionDecoder.TryDecode(opcode, payload, out ClientRejection rejection))
        {
            Interlocked.Increment(ref _rejectionsSent);
            rejections.LogOutbound(_rejectionGate, ref _rejectionBucket, LogContext(), opcode, rejection);
        }

        if (_duplicates is { } detector && _duplicateState is { } state && (!_managed || detector.IncludeBots)
            && detector.Observe(state, opcode, payload, out DuplicatePacketHit hit))
        {
            detector.Report(state, LogContext(), hit);
        }
    }

    /// <summary>
    /// A handler is about to return without answering the client: log why (docs/ops/observability.md). Players log at Information, bots at
    /// Debug, rate limited per session.
    /// </summary>
    internal void LogSilentDrop(WorldOpcode request, string reason, ulong target = 0, uint entry = 0, uint arg = 0)
        => _rejections?.LogSilentDrop(_rejectionGate, ref _rejectionBucket, LogContext(), request, reason, target, entry, arg);
}
