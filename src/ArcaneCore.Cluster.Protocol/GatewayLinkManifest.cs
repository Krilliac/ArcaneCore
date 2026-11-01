using ArcaneCore.Kernel.Cluster;

namespace ArcaneCore.World.Cluster.Gateway;

/// <summary>Builds the <see cref="ClusterNodeManifest"/> both ends of a gateway link advertise.</summary>
public static class GatewayLinkManifest
{
    /// <summary>
    /// The contract version both ends of a gateway link advertise. 5: the unified control frames 28-32 (ControlRequest, ControlReply, ControlSubmit,
    /// ControlSubmitReply, ControlNotice); the link closes on any unknown frame, so a version 4 peer is refused at admission instead.
    /// 4: the worker sends WorkerStatus frames (tick counter, sessions, countdown state) for
    /// the gateway's realm liveness writer; a gateway of version 3 would close the link on the unknown frame, so it is refused at admission instead.
    /// 3: mutual link proof (the worker proves the secret to the gateway too)
    /// and a MAC tag on every frame after it (<c>LinkAuth</c>); the earlier assumption was a loopback-only link, so frames were unauthenticated.
    /// 2: every SessionClientPacket carries the gateway's receive time
    /// after its payload and the gateway opens the link with a LinkClock frame; an older peer is refused at admission
    /// (<see cref="ClusterNodeManifest.TryAdmit"/>) instead of misreading the frames.
    /// </summary>
    public const int ContractVersion = 5;

    public static ClusterNodeManifest Create(
        ClusterRole role, string nodeId, Guid incarnation, int realmId, string contentRevision, int charactersSchemaVersion, int worldSchemaVersion,
        string ownedMaps = "", bool allowInstanceMaps = true)
        => new(realmId, nodeId, incarnation, role, ContractVersion, charactersSchemaVersion, worldSchemaVersion, contentRevision,
            ownedMaps, allowInstanceMaps);
}
