using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.World.Cluster.Tooling;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Tooling;

/// <summary>
/// The manifest the node monitor presents to the cluster nodes carried schema-version literals (61 and 19) that went stale each time a
/// schema module was added; it now takes this build's versions from the database contexts, as <c>.cluster diag</c> does.
/// </summary>
public sealed class ClusterNodeBridgeManifestTests
{
    [Fact]
    public void WorldManifest_TakesTheSchemaVersionsOfThisBuild_NotLiterals()
    {
        Guid incarnation = Guid.NewGuid();

        ClusterNodeManifest manifest = ClusterNodeBridgeFeature.BuildWorldManifest(incarnation);

        Assert.Equal(CharacterDbContext.Schema.CurrentVersion, manifest.CharactersSchemaVersion);
        Assert.Equal(WorldDbContext.Schema.CurrentVersion, manifest.WorldSchemaVersion);
        Assert.Equal(incarnation, manifest.NodeIncarnation);
        Assert.Equal(ClusterRole.Gateway, manifest.Role);
        Assert.Equal(ClusterNodeManifest.CurrentContractVersion, manifest.ContractVersion);
        Assert.True(manifest.IsValid(out string? reason), reason);
    }
}
