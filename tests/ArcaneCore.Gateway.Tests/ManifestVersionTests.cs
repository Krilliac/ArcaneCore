using ArcaneCore.Kernel.Cluster;
using ArcaneCore.World.Cluster.Gateway;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>The versions a worker advertises (recorded in the databases, or the explicit label override) decide link admission, failing closed.</summary>
public sealed class ManifestVersionTests
{
    private static ClusterNodeManifest GatewayManifest(int characters, int world, string content) =>
        GatewayLinkManifest.Create(ClusterRole.Gateway, "gw", Guid.NewGuid(), 1, content, characters, world);

    private static ClusterNodeManifest WorkerManifest(GatewayWorkerOptions options, GatewayManifestVersions versions) =>
        options.ToManifest(Guid.NewGuid(), versions);

    [Fact]
    public void WorkerWhoseCharactersSchemaIsBehindTheGateways_IsRefused()
    {
        var options = new GatewayWorkerOptions { VersionsFromDatabase = true };
        GatewayManifestVersions worker = GatewayManifestVersions.Resolve(options, 39, 12);
        ClusterNodeManifest gateway = GatewayManifest(40, 12, GatewayManifestVersions.ContentRevisionFor(12));

        bool admitted = gateway.TryAdmit(WorkerManifest(options, worker), out string? reason);

        Assert.False(admitted);
        Assert.Contains("characters schema mismatch", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EqualVersions_AreAdmitted()
    {
        var options = new GatewayWorkerOptions { VersionsFromDatabase = true };
        GatewayManifestVersions worker = GatewayManifestVersions.Resolve(options, 40, 12);
        ClusterNodeManifest gateway = GatewayManifest(40, 12, GatewayManifestVersions.ContentRevisionFor(12));

        Assert.True(gateway.TryAdmit(WorkerManifest(options, worker), out string? reason), reason);
    }

    [Fact]
    public void WorldSchemaOrContentRevisionMismatch_IsRefused()
    {
        var options = new GatewayWorkerOptions { VersionsFromDatabase = true };
        GatewayManifestVersions worker = GatewayManifestVersions.Resolve(options, 40, 13);
        ClusterNodeManifest gateway = GatewayManifest(40, 12, GatewayManifestVersions.ContentRevisionFor(12));

        Assert.False(gateway.TryAdmit(WorkerManifest(options, worker), out _));
    }

    [Fact]
    public void OverrideLabels_StillWork_AndIgnoreTheDatabases()
    {
        var options = new GatewayWorkerOptions { CharactersSchemaVersion = 7, WorldSchemaVersion = 8, ContentRevision = "pinned" };
        GatewayManifestVersions worker = GatewayManifestVersions.Resolve(options, 40, 12);

        Assert.Equal(new GatewayManifestVersions(7, 8, "pinned"), worker);
        Assert.True(GatewayManifest(7, 8, "pinned").TryAdmit(WorkerManifest(options, worker), out string? reason), reason);
    }

    [Theory]
    [InlineData(null, 12)]
    [InlineData(0, 12)]
    [InlineData(-1, 12)]
    [InlineData(40, null)]
    [InlineData(40, 0)]
    public void UnreadableRecordedVersion_FailsClosed(int? characters, int? world)
    {
        var options = new GatewayWorkerOptions { VersionsFromDatabase = true };

        Assert.Throws<InvalidOperationException>(() => GatewayManifestVersions.Resolve(options, characters, world));
    }
}
