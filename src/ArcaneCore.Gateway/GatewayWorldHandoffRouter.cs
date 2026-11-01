using System.Net;
using ArcaneCore.World.Cluster.Handoff;

namespace ArcaneCore.Gateway;

/// <summary>
/// The routing steps of a whole-World handoff (Tier 3b) for a coordinator that runs in the gateway process. When A freezes the gateway holds every client's
/// input (<see cref="GatewayServer.HoldSessionsForHandoff"/>; an abort returns it to A through <see cref="GatewayServer.ReleaseHandoffHold"/>). After B is promoted
/// the gateway's worker link moves to B and every session moves with it (<see cref="GatewayServer.RetargetWorkerSeamlessAsync"/>): B adopts each character,
/// the client sees a synthesized SMSG_TRANSFER_PENDING / SMSG_NEW_WORLD at the position of the save, and the held input is delivered to B. A client whose move fails
/// is disconnected and logs in again. A gateway restart still drops every client: sessions live in the gateway process.
/// </summary>
public sealed class GatewayWorldHandoffRouter : IWorldHandoffRouter
{
    private readonly GatewayServer _gateway;
    private readonly IPEndPoint _target;

    public GatewayWorldHandoffRouter(GatewayServer gateway, IPEndPoint target)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(target);
        _gateway = gateway;
        _target = target;
    }

    public Task HoldAsync(CancellationToken cancellationToken)
    {
        _gateway.HoldSessionsForHandoff();
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(CancellationToken cancellationToken)
    {
        _gateway.ReleaseHandoffHold();
        return Task.CompletedTask;
    }

    public Task RerouteAsync(CancellationToken cancellationToken) => _gateway.RetargetWorkerSeamlessAsync(_target, cancellationToken);
}
