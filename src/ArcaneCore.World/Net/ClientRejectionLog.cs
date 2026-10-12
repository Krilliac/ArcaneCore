using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Net;

/// <summary>Who a log line is about: identifiers only (GUID low, never the name, so bot-QA output stays free of player names).</summary>
public readonly record struct SessionLogContext(bool IsBot, int AccountId, uint CharacterLow, uint MapId);

/// <summary>
/// The client-visible failure log (docs/ops/observability.md), logger category <see cref="Category"/>. One structured line for every
/// refusal the server sends a client (decoded from the outbound packet by <see cref="ClientRejectionDecoder"/>, hooked in
/// <c>WorldSession.Send</c>) and for every handler that drops a request without answering. Players log at Information, bots at Debug.
/// Each session owns a <see cref="TokenBucket"/>; a disabled level costs one <see cref="ILogger.IsEnabled"/> check and takes no token,
/// and nothing is formatted or allocated before a token is taken. Shared and thread-safe; the per-session bucket is locked by its owner.
/// </summary>
public sealed partial class ClientRejectionLog
{
    public const string Category = "ArcaneCore.ClientRejections";

    private readonly ILogger _logger;
    private readonly ClientRejectionOptions _options;

    public ClientRejectionLog(ObservabilityOptions options, ILoggerFactory loggerFactory)
        : this(options.ClientRejections, loggerFactory.CreateLogger(Category))
    {
    }

    public ClientRejectionLog(ClientRejectionOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public bool Enabled => _options.Enabled;

    public bool LogSilentDrops => _options.Enabled && _options.LogSilentDrops;

    /// <summary>A fresh per-session bucket.</summary>
    public TokenBucket NewBucket() => new(Math.Clamp(_options.BurstPerSession, 1, 1000), Math.Clamp(_options.RefillPerMinute, 1, 6000));

    /// <summary>Log a refusal the server just sent. <paramref name="gate"/> guards <paramref name="bucket"/> (the session's lock).</summary>
    public void LogOutbound(object gate, ref TokenBucket bucket, in SessionLogContext who, WorldOpcode opcode, in ClientRejection rejection)
    {
        LogLevel level = who.IsBot ? LogLevel.Debug : LogLevel.Information;
        if (!_logger.IsEnabled(level))
        {
            return;
        }

        long suppressed;
        lock (gate)
        {
            if (!bucket.TryTake(Environment.TickCount64, out suppressed))
            {
                return;
            }
        }

        string opcodeName = WorldOpcodeNames.GetName(opcode);
        if (who.IsBot)
        {
            BotRejected(_logger, rejection.Kind, rejection.ReasonName, opcodeName, who.CharacterLow, who.MapId, rejection.Target, rejection.Entry, rejection.Arg, suppressed);
        }
        else
        {
            PlayerRejected(_logger, rejection.Kind, rejection.ReasonName, opcodeName, who.CharacterLow, who.MapId, rejection.Target, rejection.Entry, rejection.Arg, suppressed);
        }
    }

    /// <summary>Log a request a handler dropped without answering the client.</summary>
    public void LogSilentDrop(object gate, ref TokenBucket bucket, in SessionLogContext who, WorldOpcode request, string reason, ulong target, uint entry, uint arg)
    {
        if (!LogSilentDrops)
        {
            return;
        }

        LogLevel level = who.IsBot ? LogLevel.Debug : LogLevel.Information;
        if (!_logger.IsEnabled(level))
        {
            return;
        }

        long suppressed;
        lock (gate)
        {
            if (!bucket.TryTake(Environment.TickCount64, out suppressed))
            {
                return;
            }
        }

        string opcodeName = WorldOpcodeNames.GetName(request);
        if (who.IsBot)
        {
            BotSilentDrop(_logger, reason, opcodeName, who.CharacterLow, who.MapId, target, entry, arg, suppressed);
        }
        else
        {
            PlayerSilentDrop(_logger, reason, opcodeName, who.CharacterLow, who.MapId, target, entry, arg, suppressed);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "client rejected: {Kind} {Reason} (opcode {Opcode}) character {CharacterLow} map {MapId} target {Target:X} entry {Entry} arg {Arg} (suppressed {Suppressed})")]
    private static partial void PlayerRejected(ILogger logger, ClientRejectionKind kind, string reason, string opcode, uint characterLow, uint mapId, ulong target, uint entry, uint arg, long suppressed);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "client rejected: {Kind} {Reason} (opcode {Opcode}) character {CharacterLow} map {MapId} target {Target:X} entry {Entry} arg {Arg} (suppressed {Suppressed})")]
    private static partial void BotRejected(ILogger logger, ClientRejectionKind kind, string reason, string opcode, uint characterLow, uint mapId, ulong target, uint entry, uint arg, long suppressed);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "request dropped without a reply: {Reason} (opcode {Opcode}) character {CharacterLow} map {MapId} target {Target:X} entry {Entry} arg {Arg} (suppressed {Suppressed})")]
    private static partial void PlayerSilentDrop(ILogger logger, string reason, string opcode, uint characterLow, uint mapId, ulong target, uint entry, uint arg, long suppressed);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "request dropped without a reply: {Reason} (opcode {Opcode}) character {CharacterLow} map {MapId} target {Target:X} entry {Entry} arg {Arg} (suppressed {Suppressed})")]
    private static partial void BotSilentDrop(ILogger logger, string reason, string opcode, uint characterLow, uint mapId, ulong target, uint entry, uint arg, long suppressed);
}
