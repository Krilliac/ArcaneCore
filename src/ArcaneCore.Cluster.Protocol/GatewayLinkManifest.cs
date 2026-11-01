using ArcaneCore.Kernel.Cluster;

namespace ArcaneCore.World.Cluster.Gateway;

/// <summary>Builds the <see cref="ClusterNodeManifest"/> both ends of a gateway link advertise.</summary>
public static class GatewayLinkManifest
{
    /// <summary>
    /// The contract version both ends of a gateway link advertise. 2: every SessionClientPacket carries the gateway's receive time
    /// after its payload and the gateway opens the link with a LinkClock frame; a version 1 peer is refused at admission
    /// (<see cref="ClusterNodeManifest.TryAdmit"/>) instead of misreading the frames.
    /// </summary>
    public const int ContractVersion = 2;

    public static ClusterNodeManifest Create(
        ClusterRole role, string nodeId, Guid incarnation, int realmId, string contentRevision, int charactersSchemaVersion, int worldSchemaVersion,
        string ownedMaps = "", bool allowInstanceMaps = true)
        => new(realmId, nodeId, incarnation, role, ContractVersion, charactersSchemaVersion, worldSchemaVersion, contentRevision,
            ownedMaps, allowInstanceMaps);
}
