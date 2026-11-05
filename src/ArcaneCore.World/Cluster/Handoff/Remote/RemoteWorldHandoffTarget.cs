using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster.Control;
using ArcaneCore.World.Cluster.Gateway;

namespace ArcaneCore.World.Cluster.Handoff.Remote;

/// <summary>
/// The warmed standby world B of a whole-World handoff, reached over the gateway's control link (design _wf/handoff-control-DESIGN.md section 3.1). Implements the unchanged
/// <see cref="IWorldHandoffTarget"/>: <see cref="WarmAsync"/> and <see cref="PromoteAsync"/> are control verbs for this handoff's operation id; the synchronous properties are the
/// ones <see cref="ConnectAsync"/> read with <c>Describe</c> (the epoch takeover reads them right after the flush). Immutable after construction; thread-safe.
/// </summary>
public sealed class RemoteWorldHandoffTarget : IWorldHandoffTarget
{
    private readonly LinkControlChannel _channel;
    private readonly Guid _handoffId;
    private readonly string _slotNode;
    private readonly uint[]? _ownedMaps;

    private RemoteWorldHandoffTarget(LinkControlChannel channel, Guid handoffId, string slotNode, ControlDescribePayload describe)
    {
        _channel = channel;
        _handoffId = handoffId;
        _slotNode = slotNode;
        WorkerId = new WorkerNodeId(describe.WorkerId);
        OwnerNode = describe.OwnerNode;
        Incarnation = describe.Incarnation;
        _ownedMaps = describe.OwnedMaps.Count == 0 ? null : describe.OwnedMaps.ToArray();
    }

    /// <inheritdoc />
    public WorkerNodeId WorkerId { get; }

    /// <inheritdoc />
    public string OwnerNode { get; }

    /// <inheritdoc />
    public Guid Incarnation { get; }

    /// <summary>The maps the standby serves; null (no maps reported) leaves the shard row's maps untouched at the takeover.</summary>
    public IReadOnlyCollection<uint>? WorkerOwnedMaps => _ownedMaps;

    /// <summary>
    /// Sends <c>Describe</c> to the worker behind <paramref name="channel"/> and builds the proxy for handoff <paramref name="handoffId"/>. Throws, journalling nothing, when the worker
    /// answers otherwise than as a standby target: control off, not a standby, or more than one admitted gateway. <paramref name="slotNode"/> is the slot it stands behind: the promotion tells the worker that name.
    /// </summary>
    public static async Task<RemoteWorldHandoffTarget> ConnectAsync(LinkControlChannel channel, Guid handoffId, string slotNode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotNode);
        ControlDescribePayload describe = await RemoteHandoffDescribe.DescribeAsync(channel, handoffId, cancellationToken).ConfigureAwait(false);
        if (!describe.IsStandby)
        {
            throw new InvalidOperationException($"worker {describe.WorkerId} is not a standby; it cannot be the target of a handoff");
        }

        return new RemoteWorldHandoffTarget(channel, handoffId, slotNode, describe);
    }

    /// <inheritdoc />
    public Task WarmAsync(CancellationToken cancellationToken) =>
        _channel.CallAsync(ControlKind.WorldHandoff, _handoffId, ControlVerb.Warm, new ControlWarmArguments(Incarnation), cancellationToken);

    /// <inheritdoc />
    public Task PromoteAsync(CharacterOwnershipToken takenOver, CancellationToken cancellationToken) =>
        _channel.CallAsync(ControlKind.WorldHandoff, _handoffId, ControlVerb.Promote, new ControlPromoteArguments(takenOver, _slotNode), cancellationToken);
}
