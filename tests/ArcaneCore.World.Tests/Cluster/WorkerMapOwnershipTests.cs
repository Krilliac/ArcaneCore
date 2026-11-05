using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Configuration.Validation;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Supervision;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster;

/// <summary>
/// S4a: a worker owns an explicit map set (Cluster:Worker:OwnedMaps) and refuses maps it does not own; an empty set owns all maps (the
/// combined behaviour); the set is announced in the HelloRegister manifest by appended fields; supervisor Processes entries with overlapping
/// Maps fail the startup check.
/// </summary>
public sealed class WorkerMapOwnershipTests
{
    private static WorldRuntime NewWorld(string ownedMaps, bool allowInstances = true)
    {
        var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
        Assert.True(MapOwnership.TryParse(ownedMaps, allowInstances, out MapOwnership ownership, out string? error), error);
        world.Ownership = ownership;
        return world;
    }

    [Fact]
    public void OwnedMapsZero_RefusesEntryToMapOne_AndGrantsMapZero()
    {
        using WorldRuntime world = NewWorld("0");

        Assert.Equal(MapAccessResult.Granted, world.TryGetMap(0, 0, out Map? zero));
        Assert.NotNull(zero);
        Assert.Equal(MapAccessResult.NotOwned, world.TryGetMap(1, 0, out Map? one));
        Assert.Null(one);
        Assert.Null(world.FindMap(1));
        Assert.Throws<MapNotOwnedException>(() => world.GetMap(1));
        Assert.False(world.Owns(1));
    }

    [Fact]
    public void EmptySet_AcceptsEveryMap()
    {
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
        foreach (string empty in new[] { string.Empty, "  ", ",;" })
        {
            Assert.True(MapOwnership.TryParse(empty, allowInstanceMaps: false, out MapOwnership ownership, out string? error), error);
            Assert.True(ownership.OwnsEverything);
        }

        Assert.True(world.Ownership.OwnsEverything);
        foreach (uint mapId in new uint[] { 0, 1, 30, 489, 9999 })
        {
            Assert.Equal(MapAccessResult.Granted, world.TryGetMap(mapId, 0, out Map? map));
            Assert.NotNull(map);
        }
    }

    [Fact]
    public void InstanceAllowance_GovernsInstanceCopiesOfUnlistedMaps()
    {
        using (WorldRuntime allowing = NewWorld("0", allowInstances: true))
        {
            Assert.Equal(MapAccessResult.Granted, allowing.TryGetMap(33, 7, out Map? instance));
            Assert.NotNull(instance);
            Assert.Equal(MapAccessResult.NotOwned, allowing.TryGetMap(33, 0, out _)); // the shared copy of an unlisted map stays refused
        }

        using WorldRuntime refusing = NewWorld("0", allowInstances: false);
        Assert.Equal(MapAccessResult.NotOwned, refusing.TryGetMap(33, 7, out _));
    }

    [Theory]
    [InlineData("0,1,30-40", 35u, true)]
    [InlineData("0,1,30-40", 41u, false)]
    [InlineData("0 1;30-40", 1u, true)]
    [InlineData("30-40", 0u, false)]
    public void Parse_AcceptsListsAndRanges(string text, uint mapId, bool owned)
    {
        Assert.True(MapOwnership.TryParse(text, allowInstanceMaps: false, out MapOwnership ownership, out string? error), error);
        Assert.Equal(owned, ownership.Owns(mapId, 0));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("5-3")]
    [InlineData("1-")]
    [InlineData("-1")]
    [InlineData("1,,x")]
    public void Parse_RejectsMalformedLists(string text)
    {
        Assert.False(MapOwnership.TryParse(text, allowInstanceMaps: true, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void WorkerOptions_ParseTheirOwnedMaps()
    {
        var options = new GatewayWorkerOptions { OwnedMaps = "0,1", AllowInstanceMaps = false };
        Assert.True(options.TryGetOwnership(out MapOwnership ownership, out _));
        Assert.True(ownership.Owns(1, 0));
        Assert.False(ownership.Owns(30, 5));
        Assert.False(new GatewayWorkerOptions { OwnedMaps = "x" }.TryGetOwnership(out _, out string? error));
        Assert.NotNull(error);
    }

    // ---- HelloRegister golden bytes -------------------------------------------------------------------------------------------------

    private static readonly Guid GoldenIncarnation = new(Enumerable.Range(1, 16).Select(i => (byte)i).ToArray());

    // The layout before S4a: type, realm, nodeId (len + utf8), incarnation, role, contract, characters schema, world schema, content revision (len + utf8).
    private static readonly byte[] OriginalLayout =
    [
        0x01,
        0x01, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00, 0x77,
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
        0x02, 0x00, 0x00, 0x00,
        0x05, 0x00, 0x00, 0x00, // the gateway link contract version (GatewayLinkManifest.ContractVersion)
        0x03, 0x00, 0x00, 0x00,
        0x04, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00, 0x72,
    ];

    // Appended fields: owned map list (len + utf8), then the instance allowance byte.
    private static readonly byte[] AppendedFields = [0x01, 0x00, 0x00, 0x00, 0x30, 0x01];

    [Fact]
    public void HelloRegisterGolden_ChangesOnlyByAppendedFields()
    {
        ClusterNodeManifest manifest = GatewayLinkManifest.Create(ClusterRole.Worker, "w", GoldenIncarnation, 1, "r", 3, 4, "0", allowInstanceMaps: true);

        byte[] frame = LoopbackGatewayWorkerHost.BuildHelloRegisterFrame(manifest);

        Assert.Equal([.. OriginalLayout, .. AppendedFields], frame);
        Assert.Equal(OriginalLayout, frame[..OriginalLayout.Length]);
    }

    [Fact]
    public void HelloRegister_RoundTripsOwnedMaps_AndAnOlderFrameReadsAsOwnsEverything()
    {
        ClusterNodeManifest manifest = GatewayLinkManifest.Create(ClusterRole.Worker, "w", GoldenIncarnation, 1, "r", 3, 4, "0,30-40", allowInstanceMaps: false);
        byte[] frame = LoopbackGatewayWorkerHost.BuildHelloRegisterFrame(manifest);

        ClusterNodeManifest read = LoopbackGatewayWorkerHost.DeserializeManifest(frame.AsSpan(1));
        Assert.Equal("0,30-40", read.OwnedMaps);
        Assert.False(read.AllowInstanceMaps);

        ClusterNodeManifest old = LoopbackGatewayWorkerHost.DeserializeManifest(OriginalLayout.AsSpan(1));
        Assert.Equal(string.Empty, old.OwnedMaps);
        Assert.True(old.AllowInstanceMaps);
        Assert.Equal("w", old.NodeId);
        Assert.Equal("r", old.ContentRevision);
    }

    // ---- supervisor Processes:*:Maps --------------------------------------------------------------------------------------------------

    private static List<ConfigIssue> Check(string mapsA, string mapsB, string? assignmentsA = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Cluster:Supervisor:Enabled"] = "true",
            ["Cluster:Supervisor:MinClusterProcesses"] = "1",
        };
        foreach ((string key, string maps) in new[] { ("a", mapsA), ("b", mapsB) })
        {
            string prefix = $"Cluster:Supervisor:Processes:{key}:";
            values[prefix + "Name"] = key;
            values[prefix + "Role"] = "worker";
            values[prefix + "ExecutablePath"] = "ArcaneCore.World";
            values[prefix + "Maps"] = maps;
        }

        if (assignmentsA is not null)
        {
            values["Cluster:Supervisor:Processes:a:MapAssignments:0"] = assignmentsA;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return [.. new ClusterSupervisorConfigChecks().Check(configuration).Where(i => i.Severity == ConfigSeverity.Error)];
    }

    [Fact]
    public void OverlappingMaps_FailTheStartupCheck()
    {
        List<ConfigIssue> errors = Check("0,1", "1-5");

        Assert.Contains(errors, e => e.Problem.Contains("overlaps", StringComparison.Ordinal));
    }

    [Fact]
    public void MapsOverlappingAnAssignment_FailTheStartupCheck()
    {
        List<ConfigIssue> errors = Check("2", "0-5", assignmentsA: "2");

        Assert.Contains(errors, e => e.Problem.Contains("overlaps", StringComparison.Ordinal));
    }

    [Fact]
    public void DisjointMaps_PassTheOverlapCheck()
    {
        List<ConfigIssue> errors = Check("0,1", "30-40");

        Assert.DoesNotContain(errors, e => e.Problem.Contains("overlaps", StringComparison.Ordinal) || e.Problem.Contains("Maps is not", StringComparison.Ordinal));
    }

    [Fact]
    public void MalformedMaps_FailTheStartupCheck()
    {
        List<ConfigIssue> errors = Check("zero", "30-40");

        Assert.Contains(errors, e => e.Problem.Contains("Maps is not a map list", StringComparison.Ordinal));
    }
}
