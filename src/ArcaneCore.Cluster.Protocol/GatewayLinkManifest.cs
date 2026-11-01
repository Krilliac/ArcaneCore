using ArcaneCore.Kernel.Cluster;

namespace ArcaneCore.World.Cluster.Gateway;

/// <summary>Builds the <see cref="ClusterNodeManifest"/> both ends of a gateway link advertise.</summary>
public static class GatewayLinkManifest
{
    public static ClusterNodeManifest Create(
        ClusterRole role, string nodeId, Guid incarnation, int realmId, string contentRevision, int charactersSchemaVersion, int worldSchemaVersion,
        string ownedMaps = "", bool allowInstanceMaps = true)
        => new(realmId, nodeId, incarnation, role, ClusterNodeManifest.CurrentContractVersion, charactersSchemaVersion, worldSchemaVersion, contentRevision,
            ownedMaps, allowInstanceMaps);
}
