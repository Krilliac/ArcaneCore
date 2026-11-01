using ArcaneCore.Data.Characters.Transfers;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster.Control;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Transfer.Remote;

/// <summary>
/// The source worker of a per-player transfer, reached over the gateway's control link (design _wf/handoff-control-DESIGN.md section 4.1). Implements the unchanged
/// <see cref="ITransferSourceWorker"/>: every call is one <see cref="ControlKind.PlayerTransfer"/> verb for the transfer id. The worker checks the journal row before it acts, so a
/// verb the journal does not allow is refused and the call throws <see cref="ControlRefusedException"/>.
/// <list type="bullet">
/// <item><description><see cref="SuspendAndCaptureAsync"/> sends <c>Suspend</c> and decodes the bundle from the reply with <see cref="PlayerTransferBundleJson"/> (the encoding the journal uses).</description></item>
/// <item><description><see cref="ResumeAfterAbortAsync"/> sends <c>ResumeSource</c>; the worker reads the source token from the journal row.</description></item>
/// <item><description><see cref="RetireSourcePlayerAsync"/> sends <c>RetireSource</c>. It is best effort when the source's link is down (the transfer is already committed; the worker retires its frozen player itself when it re-reads the journal, design section 3.5).</description></item>
/// </list>
/// One proxy is registered once per configured slot, because the coordinator's registries are plain dictionaries; the proxy asks its channel provider for the slot's live link on every call and
/// throws <see cref="ControlLinkLostException"/> when there is none. Immutable after construction; thread-safe (the channel is).
/// </summary>
public sealed class RemoteTransferSourceWorker : ITransferSourceWorker
{
    private readonly Func<LinkControlChannel?> _channel;
    private readonly ILogger _logger;

    /// <param name="workerId">The worker id of the slot this proxy speaks for (what the coordinator registers it under).</param>
    /// <param name="channel">The slot's live control channel, or null while its link is down. Called on every request.</param>
    /// <param name="logger">Receives the best-effort retire failures.</param>
    public RemoteTransferSourceWorker(WorkerNodeId workerId, Func<LinkControlChannel?> channel, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        WorkerId = workerId;
        _channel = channel;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public WorkerNodeId WorkerId { get; }

    /// <inheritdoc />
    public async Task<PlayerTransferBundle> SuspendAndCaptureAsync(
        Guid transferId,
        ulong characterGuid,
        WorkerNodeId targetWorkerId,
        uint targetMapId,
        uint targetInstanceId,
        TransferPosition targetPosition,
        CancellationToken cancellationToken = default)
    {
        ControlReply reply = await RemoteTransferLink.Channel(_channel, WorkerId).CallAsync(
            ControlKind.PlayerTransfer, transferId, ControlVerb.Suspend,
            new ControlSuspendArguments(characterGuid, targetWorkerId.Value, targetMapId, targetInstanceId, targetPosition), cancellationToken).ConfigureAwait(false);
        if (reply.Payload is not ControlSuspendPayload suspended)
        {
            throw new InvalidDataException($"the Suspend reply of worker {WorkerId.Value} carries no bundle");
        }

        try
        {
            return PlayerTransferBundleJson.Deserialize(suspended.BundleJson);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new InvalidDataException($"the Suspend reply of worker {WorkerId.Value} carries a bundle that does not parse: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public Task ResumeAfterAbortAsync(
        Guid transferId,
        ulong characterGuid,
        CharacterOwnershipToken ownershipToken,
        string abortReason,
        CancellationToken cancellationToken = default) =>
        RemoteTransferLink.Channel(_channel, WorkerId).CallAsync(
            ControlKind.PlayerTransfer, transferId, ControlVerb.ResumeSource, new ControlReasonArguments(abortReason ?? string.Empty), cancellationToken);

    /// <inheritdoc />
    public async Task RetireSourcePlayerAsync(
        Guid transferId,
        ulong characterGuid,
        CharacterOwnershipToken supersededToken,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await RemoteTransferLink.Channel(_channel, WorkerId).CallAsync(
                ControlKind.PlayerTransfer, transferId, ControlVerb.RetireSource, new ControlTokenArguments(supersededToken), cancellationToken).ConfigureAwait(false);
        }
        catch (ControlLinkLostException ex)
        {
            _logger.LogWarning(
                "RetireSource of transfer {TransferId} was not delivered to {Worker} ({Reason}); the worker retires the committed player itself", transferId, WorkerId.Value, ex.Message);
        }
    }
}

/// <summary>What the remote transfer proxies share.</summary>
internal static class RemoteTransferLink
{
    /// <summary>The slot's live channel, or <see cref="ControlLinkLostException"/> when its link is down.</summary>
    public static LinkControlChannel Channel(Func<LinkControlChannel?> provider, WorkerNodeId worker)
    {
        LinkControlChannel? channel = provider();
        return channel is { IsConnected: true } ? channel : throw new ControlLinkLostException($"the control link to worker {worker.Value} is down");
    }
}
