using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster.Control;
using ArcaneCore.World.Cluster.Gateway;

namespace ArcaneCore.World.Cluster.Handoff.Remote;

/// <summary>
/// The draining world A of a whole-World handoff, reached over the gateway's control link (design _wf/handoff-control-DESIGN.md section 3.1). Implements the unchanged
/// <see cref="IWorldHandoffSource"/>: every call is one control verb for this handoff's operation id. The worker checks the journal row before it acts, so a verb the
/// journal does not allow is refused (the call throws <see cref="ControlRefusedException"/>). The synchronous properties are the ones <see cref="ConnectAsync"/> read
/// with <c>Describe</c>. Immutable after construction; thread-safe.
/// </summary>
public sealed class RemoteWorldHandoffSource : IWorldHandoffSource
{
    private readonly LinkControlChannel _channel;
    private readonly Guid _handoffId;

    private RemoteWorldHandoffSource(LinkControlChannel channel, Guid handoffId, WorkerNodeId workerId, CharacterOwnershipToken ownership)
    {
        _channel = channel;
        _handoffId = handoffId;
        WorkerId = workerId;
        Ownership = ownership;
    }

    /// <inheritdoc />
    public WorkerNodeId WorkerId { get; }

    /// <inheritdoc />
    public CharacterOwnershipToken Ownership { get; }

    /// <summary>
    /// Sends <c>Describe</c> to the worker behind <paramref name="channel"/> and builds the proxy for handoff <paramref name="handoffId"/>. Throws, journalling nothing, when the worker
    /// answers otherwise than as an active handoff source: control off, a standby, no characters ownership held, or more than one admitted gateway (one gateway per realm is the contract).
    /// </summary>
    public static async Task<RemoteWorldHandoffSource> ConnectAsync(LinkControlChannel channel, Guid handoffId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ControlDescribePayload describe = await RemoteHandoffDescribe.DescribeAsync(channel, handoffId, cancellationToken).ConfigureAwait(false);
        if (describe.IsStandby)
        {
            throw new InvalidOperationException($"worker {describe.WorkerId} is a standby; it cannot be the draining source of a handoff");
        }

        if (describe.HeldSourceToken is not { } held)
        {
            throw new InvalidOperationException($"worker {describe.WorkerId} holds no characters ownership to hand over");
        }

        return new RemoteWorldHandoffSource(channel, handoffId, new WorkerNodeId(describe.WorkerId), held);
    }

    /// <inheritdoc />
    public Task StopAdmissionsAsync(CancellationToken cancellationToken) => Call(ControlVerb.StopAdmissions, cancellationToken);

    /// <inheritdoc />
    public Task FreezeAsync(CancellationToken cancellationToken) => Call(ControlVerb.Freeze, cancellationToken);

    /// <inheritdoc />
    public Task FlushAsync(CancellationToken cancellationToken) => Call(ControlVerb.Flush, cancellationToken);

    /// <inheritdoc />
    public Task ResumeAsync(CancellationToken cancellationToken) => Call(ControlVerb.Resume, cancellationToken);

    /// <inheritdoc />
    public Task RetireAsync(CancellationToken cancellationToken) => Call(ControlVerb.Retire, cancellationToken);

    /// <inheritdoc />
    public Task ReleaseWriterLockAsync(CancellationToken cancellationToken) => Call(ControlVerb.ReleaseWriterLock, cancellationToken);

    private Task Call(ControlVerb verb, CancellationToken cancellationToken) =>
        _channel.CallAsync(ControlKind.WorldHandoff, _handoffId, verb, ControlNoArguments.Instance, cancellationToken);
}

/// <summary>The <c>Describe</c> exchange both remote handoff participants start with.</summary>
internal static class RemoteHandoffDescribe
{
    public static async Task<ControlDescribePayload> DescribeAsync(LinkControlChannel channel, Guid handoffId, CancellationToken cancellationToken)
    {
        ControlReply reply = await channel.CallAsync(ControlKind.WorldHandoff, handoffId, ControlVerb.Describe, ControlNoArguments.Instance, cancellationToken).ConfigureAwait(false);
        if (reply.Payload is not ControlDescribePayload describe)
        {
            throw new InvalidDataException($"the Describe reply of worker {channel.Worker} carries no describe payload");
        }

        if (!describe.HandoffEnabled)
        {
            throw new InvalidOperationException($"worker {describe.WorkerId} has whole-World handoff control off (Cluster:Worker:Handoff:Enabled)");
        }

        if (describe.AdmittedGateways > 1)
        {
            throw new InvalidOperationException(
                $"worker {describe.WorkerId} reports {describe.AdmittedGateways} admitted gateways; a handoff needs exactly one gateway per realm");
        }

        return describe;
    }
}
