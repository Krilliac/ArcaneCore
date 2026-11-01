using System.Net;
using ArcaneCore.World.Cluster.Handoff;

namespace ArcaneCore.Gateway;

/// <summary>
/// The routing steps of a whole-World handoff (Tier 3b) for a coordinator that runs in the gateway process. When A freezes the gateway holds every client's
/// input (<see cref="GatewayServer.HoldSessionsForHandoff()"/>; an abort returns it to A through <see cref="GatewayServer.ReleaseHandoffHold()"/>). After B is promoted
/// the gateway's worker link moves to B and every session moves with it (<see cref="GatewayServer.RetargetWorkerSeamlessAsync(IPEndPoint, CancellationToken)"/>): B adopts each character,
/// the client sees a synthesized SMSG_TRANSFER_PENDING / SMSG_NEW_WORLD at the position of the save, and the held input is delivered to B. A client whose move fails
/// is disconnected and logs in again. A gateway restart still drops every client: sessions live in the gateway process.
/// Built with a source node id, the router acts on that worker only (<see cref="GatewayServer.HoldSessionsForHandoff(string)"/>,
/// <see cref="GatewayServer.ReleaseHandoffHold(string)"/>, <see cref="GatewayServer.RetargetWorkerSeamlessAsync(string, IPEndPoint, CancellationToken)"/>): other workers' sessions
/// keep flowing. Built without one it acts on the first configured worker, as before.
/// </summary>
public sealed class GatewayWorldHandoffRouter : IWorldHandoffRouter
{
    private readonly GatewayServer _gateway;
    private readonly IPEndPoint _target;
    private readonly string? _sourceNode;

    /// <summary>A router for the first configured worker (single-worker gateways and the in-process handoff).</summary>
    public GatewayWorldHandoffRouter(GatewayServer gateway, IPEndPoint target)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(target);
        _gateway = gateway;
        _target = target;
    }

    /// <summary>A router for the worker <paramref name="sourceNode"/> (its Gateway:Workers:N:NodeId), the one handing off to <paramref name="target"/>.</summary>
    public GatewayWorldHandoffRouter(GatewayServer gateway, string sourceNode, IPEndPoint target)
        : this(gateway, target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceNode);
        _sourceNode = sourceNode;
    }

    public Task HoldAsync(CancellationToken cancellationToken)
    {
        if (_sourceNode is null)
        {
            _gateway.HoldSessionsForHandoff();
        }
        else
        {
            _gateway.HoldSessionsForHandoff(_sourceNode);
        }

        return Task.CompletedTask;
    }

    public Task ReleaseAsync(CancellationToken cancellationToken)
    {
        if (_sourceNode is null)
        {
            _gateway.ReleaseHandoffHold();
        }
        else
        {
            _gateway.ReleaseHandoffHold(_sourceNode);
        }

        return Task.CompletedTask;
    }

    public Task RerouteAsync(CancellationToken cancellationToken) => _sourceNode is null
        ? _gateway.RetargetWorkerSeamlessAsync(_target, cancellationToken)
        : _gateway.RetargetWorkerSeamlessAsync(_sourceNode, _target, cancellationToken);
}
