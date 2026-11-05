using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.World.Cluster.Gateway;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>The versions a worker advertises (recorded in the databases, or the explicit label override) decide link admission, failing closed.</summary>
public sealed class ManifestVersionTests : IDisposable
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
        var options = new GatewayWorkerOptions { VersionsFromDatabase = false, CharactersSchemaVersion = 7, WorldSchemaVersion = 8, ContentRevision = "pinned" };
        GatewayManifestVersions worker = GatewayManifestVersions.Resolve(options, 40, 12);

        Assert.Equal(new GatewayManifestVersions(7, 8, "pinned"), worker);
        Assert.True(GatewayManifest(7, 8, "pinned").TryAdmit(WorkerManifest(options, worker), out string? reason), reason);
    }

    [Fact]
    public void DefaultWorkerAndDefaultGateway_AdmitEachOther()
    {
        var workerOptions = new GatewayWorkerOptions();
        GatewayManifestVersions worker = GatewayManifestVersions.Resolve(
            workerOptions, CharacterDbContext.Schema.CurrentVersion, WorldDbContext.Schema.CurrentVersion);
        ClusterNodeManifest gateway = new GatewayOptions().ToManifest(Guid.NewGuid());

        Assert.True(gateway.TryAdmit(WorkerManifest(workerOptions, worker), out string? reason), reason);
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

    // ---- The real read: SQLite databases whose version rows hold different values ----

    private readonly string _directory = Directory.CreateTempSubdirectory("arcane-manifest-versions-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A scratch directory left behind is harmless; never fail a test on cleanup.
        }
    }

    private CharacterDbContext OpenCharacters(string name)
        => new(new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite($"Data Source={Path.Combine(_directory, name)};Pooling=False").Options);

    private WorldDbContext OpenWorld(string name)
        => new(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={Path.Combine(_directory, name)};Pooling=False").Options);

    /// <summary>Creates the model's tables (so the version table exists) and records <paramref name="version"/> in it; null leaves the table empty.</summary>
    private static async Task RecordAsync(DbContext db, int? version)
    {
        await db.Database.EnsureCreatedAsync();
        if (version is int v)
        {
            db.Set<SchemaVersionRow>().Add(new SchemaVersionRow { Id = 1, Version = v });
            await db.SaveChangesAsync();
        }
    }

    private async Task<GatewayManifestVersions> ReadAsync(string tag, int? characters, int? world, bool createCharacters = true, bool createWorld = true)
    {
        await using CharacterDbContext characterDb = OpenCharacters($"characters-{tag}.db");
        await using WorldDbContext worldDb = OpenWorld($"world-{tag}.db");
        if (createCharacters)
        {
            await RecordAsync(characterDb, characters);
        }

        if (createWorld)
        {
            await RecordAsync(worldDb, world);
        }

        return await GatewayManifestVersions.ReadAsync(new GatewayWorkerOptions { VersionsFromDatabase = true }, characterDb, worldDb);
    }

    [Fact]
    public async Task ReadAsync_ReportsTheValuesTheDatabasesRecord()
    {
        GatewayManifestVersions versions = await ReadAsync("recorded", 39, 12);

        Assert.Equal(new GatewayManifestVersions(39, 12, GatewayManifestVersions.ContentRevisionFor(12)), versions);
    }

    [Fact]
    public async Task DatabaseVersionBehindTheGateways_IsRefusedThroughAdmission()
    {
        GatewayManifestVersions worker = await ReadAsync("behind", 39, 12);
        var options = new GatewayWorkerOptions { VersionsFromDatabase = true };
        ClusterNodeManifest gateway = GatewayManifest(40, 12, GatewayManifestVersions.ContentRevisionFor(12));

        Assert.False(gateway.TryAdmit(WorkerManifest(options, worker), out string? reason));
        Assert.Contains("characters schema mismatch", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EqualDatabaseVersions_AreAdmitted()
    {
        GatewayManifestVersions worker = await ReadAsync("equal", 40, 12);
        var options = new GatewayWorkerOptions { VersionsFromDatabase = true };
        ClusterNodeManifest gateway = GatewayManifest(40, 12, GatewayManifestVersions.ContentRevisionFor(12));

        Assert.True(gateway.TryAdmit(WorkerManifest(options, worker), out string? reason), reason);
    }

    [Fact]
    public async Task WorldDatabaseVersionBehindTheGateways_IsRefusedThroughAdmission()
    {
        GatewayManifestVersions worker = await ReadAsync("worldbehind", 40, 11);
        var options = new GatewayWorkerOptions { VersionsFromDatabase = true };
        ClusterNodeManifest gateway = GatewayManifest(40, 12, GatewayManifestVersions.ContentRevisionFor(12));

        Assert.False(gateway.TryAdmit(WorkerManifest(options, worker), out _));
    }

    [Fact]
    public async Task MissingVersionTable_FailsClosed()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync("notable-c", 40, 12, createCharacters: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync("notable-w", 40, 12, createWorld: false));
    }

    [Fact]
    public async Task MissingVersionRow_FailsClosed()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync("norow-c", null, 12));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync("norow-w", 40, null));
    }

    [Fact]
    public async Task CreateInProgressRowZero_FailsClosed()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync("zero-c", 0, 12));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync("zero-w", 40, 0));
    }

    [Fact]
    public async Task LabelOverride_DoesNotTouchTheDatabases()
    {
        await using CharacterDbContext characterDb = OpenCharacters("override-c.db");
        await using WorldDbContext worldDb = OpenWorld("override-w.db");

        GatewayManifestVersions versions = await GatewayManifestVersions.ReadAsync(
            new GatewayWorkerOptions { VersionsFromDatabase = false, CharactersSchemaVersion = 7, WorldSchemaVersion = 8, ContentRevision = "pinned" },
            characterDb, worldDb);

        Assert.Equal(new GatewayManifestVersions(7, 8, "pinned"), versions);
        Assert.False(File.Exists(Path.Combine(_directory, "override-c.db")));
    }

    [Fact]
    public void WorkerReadsTheDatabasesByDefault() => Assert.True(new GatewayWorkerOptions().VersionsFromDatabase);
}
