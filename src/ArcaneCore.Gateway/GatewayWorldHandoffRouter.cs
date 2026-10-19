using System.Net;
using ArcaneCore.World.Cluster.Handoff;

namespace ArcaneCore.Gateway;

/// <summary>
/// The reroute step of a whole-World handoff (Tier 3b) for a coordinator that runs in the gateway process: move the gateway's worker link
/// to the promoted world B (<see cref="GatewayServer.RetargetWorkerAsync"/>). Clients connected through the link to A are disconnected and log
/// in again through B; carrying a live session across (SMSG_TRANSFER_PENDING / SMSG_NEW_WORLD) is not built.
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

    public Task RerouteAsync(CancellationToken cancellationToken) => _gateway.RetargetWorkerAsync(_target, cancellationToken);
}
