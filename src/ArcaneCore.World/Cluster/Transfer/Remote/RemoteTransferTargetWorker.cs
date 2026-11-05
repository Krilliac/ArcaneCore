using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster.Control;
using ArcaneCore.World.Cluster.Gateway;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Transfer.Remote;

/// <summary>
/// The target worker of a per-player transfer, reached over the gateway's control link (design _wf/handoff-control-DESIGN.md section 4.1). Implements the unchanged
/// <see cref="ITransferTargetWorker"/>:
/// <list type="bullet">
/// <item><description><see cref="Incarnation"/> and <see cref="OwnershipScope"/> are the ones the latest <c>TargetDescribe</c> reported. <see cref="RefreshAsync"/> sends it (the hosting calls it whenever the slot's link comes up) and
/// <see cref="ReserveSlotAsync"/> refreshes it first, so the incarnation the coordinator commits is the one the target reserved under.</description></item>
/// <item><description>The four stage methods map to <c>Reserve</c>, <c>Install</c>, <c>Activate</c> and <c>Discard</c>. <c>Discard</c> is best effort when the link is down: the transfer is already aborted in the journal and a target that is gone
/// holds nothing to discard.</description></item>
/// <item><description>Q8 (the target allocates instance ids): <see cref="ReserveSlotAsync"/> returns the instance id from the <c>Reserve</c> reply, which is the requested id unless the target allocated one for an instance-0 request; the coordinator
/// carries it through the later stages and the captured bundle that the journal holds names it.</description></item>
/// <item><description><see cref="IncarnationUnchangedSinceReserveAsync"/> is the pre-commit check: it describes the target again and says whether its incarnation is still the one the transfer reserved under.</description></item>
/// </list>
/// Registered once per configured slot; the live link is asked of the channel provider on every call. Thread-safe: the cached describe and the per-transfer incarnations are guarded by a lock.
/// </summary>
public sealed class RemoteTransferTargetWorker : ITransferTargetWorker
{
    /// <summary>How many transfers' reserve-time incarnations are remembered; a transfer that never reaches Activate or Discard (the gateway died) must not grow the table without bound.</summary>
    private const int RememberedReservations = 4096;

    private readonly Func<LinkControlChannel?> _channel;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Guid> _reservedUnder = [];
    private readonly Queue<Guid> _reservedOrder = new();
    private Guid _incarnation;
    private string? _ownershipScope;

    /// <param name="workerId">The worker id of the slot this proxy speaks for (what the coordinator registers it under).</param>
    /// <param name="channel">The slot's live control channel, or null while its link is down. Called on every request.</param>
    /// <param name="logger">Receives the best-effort discard failures.</param>
    public RemoteTransferTargetWorker(WorkerNodeId workerId, Func<LinkControlChannel?> channel, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        WorkerId = workerId;
        _channel = channel;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public WorkerNodeId WorkerId { get; }

    /// <summary>The target's incarnation from the latest <c>TargetDescribe</c>; <see cref="Guid.Empty"/> until the first one answered.</summary>
    public Guid Incarnation
    {
        get
        {
            lock (_gate)
            {
                return _incarnation;
            }
        }
    }

    /// <summary>The ownership scope from the latest <c>TargetDescribe</c>; null when the target reported none (it claims in the realm-wide scope) or has not answered yet.</summary>
    public string? OwnershipScope
    {
        get
        {
            lock (_gate)
            {
                return _ownershipScope;
            }
        }
    }

    /// <summary>Sends <c>TargetDescribe</c>, caches the incarnation and scope it reports and returns the whole answer (admission and free reservations too). Throws like any control call.</summary>
    public async Task<ControlTargetDescribePayload> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ControlReply reply = await RemoteTransferLink.Channel(_channel, WorkerId).CallAsync(
            ControlKind.PlayerTransfer, Guid.Empty, ControlVerb.TargetDescribe, ControlNoArguments.Instance, cancellationToken).ConfigureAwait(false);
        if (reply.Payload is not ControlTargetDescribePayload describe)
        {
            throw new InvalidDataException($"the TargetDescribe reply of worker {WorkerId.Value} carries no describe payload");
        }

        lock (_gate)
        {
            _incarnation = describe.Incarnation;
            _ownershipScope = describe.OwnershipScope.Length == 0 ? null : describe.OwnershipScope;
        }

        return describe;
    }

    /// <inheritdoc />
    /// <remarks>Describes the target again (<see cref="RefreshAsync"/>), so a restart the cached answer has not seen yet is found.</remarks>
    public async Task<Guid> CurrentIncarnationAsync(CancellationToken cancellationToken = default)
        => (await RefreshAsync(cancellationToken).ConfigureAwait(false)).Incarnation;

    /// <inheritdoc />
    public async Task<bool> TryReserveSlotAsync(
        Guid transferId, ulong characterGuid, uint targetMapId, uint targetInstanceId, CancellationToken cancellationToken = default) =>
        (await ReserveSlotAsync(transferId, characterGuid, targetMapId, targetInstanceId, cancellationToken).ConfigureAwait(false)).Reserved;

    /// <inheritdoc />
    public async Task<TransferSlotReservation> ReserveSlotAsync(
        Guid transferId, ulong characterGuid, uint targetMapId, uint targetInstanceId, CancellationToken cancellationToken = default)
    {
        ControlTargetDescribePayload describe = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        ControlReply reply = await RemoteTransferLink.Channel(_channel, WorkerId).CallAsync(
            ControlKind.PlayerTransfer, transferId, ControlVerb.Reserve, new ControlReserveArguments(characterGuid, targetMapId, targetInstanceId), cancellationToken).ConfigureAwait(false);
        if (reply.Payload is not ControlReservePayload reserved)
        {
            throw new InvalidDataException($"the Reserve reply of worker {WorkerId.Value} carries no reserve payload");
        }

        if (reserved.Reserved)
        {
            Remember(transferId, describe.Incarnation);
        }

        return new TransferSlotReservation(reserved.Reserved, reserved.InstanceId);
    }

    /// <summary>
    /// The pre-commit check (design section 4.3): describes the target again and returns false when its incarnation is no longer the one <paramref name="transferId"/> reserved under (the
    /// target process restarted and lost the reservation and the shadow), so the coordinator aborts instead of committing to a worker that holds nothing. A transfer this proxy did not reserve
    /// answers false. The cached incarnation is refreshed either way.
    /// </summary>
    public async Task<bool> IncarnationUnchangedSinceReserveAsync(Guid transferId, CancellationToken cancellationToken = default)
    {
        ControlTargetDescribePayload describe = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return _reservedUnder.TryGetValue(transferId, out Guid reservedUnder) && reservedUnder == describe.Incarnation;
        }
    }

    /// <inheritdoc />
    public Task InstallPassiveShadowAsync(Guid transferId, PlayerTransferBundle bundle, CancellationToken cancellationToken = default) =>
        // The worker installs the bundle its journal row holds (the coordinator journalled it at stage 2); nothing but the verb travels.
        RemoteTransferLink.Channel(_channel, WorkerId).CallAsync(
            ControlKind.PlayerTransfer, transferId, ControlVerb.Install, ControlNoArguments.Instance, cancellationToken);

    /// <inheritdoc />
    public async Task DiscardPassiveShadowAsync(Guid transferId, ulong characterGuid, string abortReason, CancellationToken cancellationToken = default)
    {
        Forget(transferId);
        try
        {
            await RemoteTransferLink.Channel(_channel, WorkerId).CallAsync(
                ControlKind.PlayerTransfer, transferId, ControlVerb.Discard, new ControlReasonArguments(abortReason ?? string.Empty), cancellationToken).ConfigureAwait(false);
        }
        catch (ControlLinkLostException ex)
        {
            _logger.LogWarning("Discard of transfer {TransferId} was not delivered to {Worker} ({Reason}); the journal row is aborted", transferId, WorkerId.Value, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task ActivateTargetPlayerAsync(
        Guid transferId, PlayerTransferBundle bundle, CharacterOwnershipToken targetOwnershipToken, CancellationToken cancellationToken = default)
    {
        await RemoteTransferLink.Channel(_channel, WorkerId).CallAsync(
            ControlKind.PlayerTransfer, transferId, ControlVerb.Activate, new ControlTokenArguments(targetOwnershipToken), cancellationToken).ConfigureAwait(false);
        Forget(transferId);
    }

    private void Remember(Guid transferId, Guid incarnation)
    {
        lock (_gate)
        {
            if (_reservedUnder.TryAdd(transferId, incarnation))
            {
                _reservedOrder.Enqueue(transferId);
            }
            else
            {
                _reservedUnder[transferId] = incarnation;
            }

            while (_reservedOrder.Count > RememberedReservations)
            {
                _reservedUnder.Remove(_reservedOrder.Dequeue());
            }
        }
    }

    private void Forget(Guid transferId)
    {
        lock (_gate)
        {
            _reservedUnder.Remove(transferId);
        }
    }
}
