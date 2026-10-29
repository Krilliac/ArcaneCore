using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Battlegrounds;
using ArcaneCore.World.Reload;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary><c>World:Playerbots:Battlegrounds</c>: defaults, range checks, binding and live <c>.reload config</c>.</summary>
public sealed class PlayerbotBattlegroundOptionsTests : IDisposable
{
    private const string Prefix = PlayerbotOptions.SectionName + ":Battlegrounds:";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "arcane-bg-reload-" + Guid.NewGuid().ToString("N"));
    private readonly WorldRuntime _world;

    public PlayerbotBattlegroundOptionsTests()
    {
        Directory.CreateDirectory(_directory);
        _world = new WorldRuntime(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 }, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
    }

    public void Dispose()
    {
        _world.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Defaults()
    {
        var options = new PlayerbotBattlegroundOptions();
        Assert.True(options.Enabled);
        Assert.Equal(0.05f, options.QueueChancePerMinute);
        Assert.Equal(20, options.MaxBotsPerBattleground);
        Assert.True(options.PreferFillingRealPlayerQueues);
        Assert.True(options.RaiseCapToFillTemplate);
        Assert.Equal(1500, options.MaxTravelYards);
        Assert.Equal(30, options.MaxQueueWaitMinutes);
        options.Validate();
    }

    [Theory]
    [InlineData(nameof(PlayerbotBattlegroundOptions.QueueChancePerMinute), -0.01f)]
    [InlineData(nameof(PlayerbotBattlegroundOptions.QueueChancePerMinute), 1.01f)]
    [InlineData(nameof(PlayerbotBattlegroundOptions.QueueChancePerMinute), float.NaN)]
    [InlineData(nameof(PlayerbotBattlegroundOptions.QueueChancePerMinute), float.PositiveInfinity)]
    [InlineData(nameof(PlayerbotBattlegroundOptions.MaxBotsPerBattleground), -1)]
    [InlineData(nameof(PlayerbotBattlegroundOptions.MaxBotsPerBattleground), 81)]
    [InlineData(nameof(PlayerbotBattlegroundOptions.MaxTravelYards), 99)]
    [InlineData(nameof(PlayerbotBattlegroundOptions.MaxTravelYards), 20_001)]
    [InlineData(nameof(PlayerbotBattlegroundOptions.MaxQueueWaitMinutes), 0)]
    [InlineData(nameof(PlayerbotBattlegroundOptions.MaxQueueWaitMinutes), 241)]
    public void Validate_RejectsOutOfRange(string property, object value)
    {
        var options = new PlayerbotBattlegroundOptions();
        typeof(PlayerbotBattlegroundOptions).GetProperty(property)!.SetValue(options, value);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(property, error.Message);

        // The same failure surfaces through the owning options.
        var playerbots = new PlayerbotOptions { Battlegrounds = options };
        Assert.Throws<InvalidOperationException>(playerbots.Validate);
    }

    [Theory]
    [InlineData(nameof(PlayerbotBattlegroundOptions.QueueChancePerMinute), 0f)]
    [InlineData(nameof(PlayerbotBattlegroundOptions.QueueChancePerMinute), 1f)]
    [InlineData(nameof(PlayerbotBattlegroundOptions.MaxBotsPerBattleground), 0)]
    [InlineData(nameof(PlayerbotBattlegroundOptions.MaxBotsPerBattleground), 80)]
    [InlineData(nameof(PlayerbotBattlegroundOptions.MaxTravelYards), 100)]
    [InlineData(nameof(PlayerbotBattlegroundOptions.MaxTravelYards), 20_000)]
    [InlineData(nameof(PlayerbotBattlegroundOptions.MaxQueueWaitMinutes), 1)]
    [InlineData(nameof(PlayerbotBattlegroundOptions.MaxQueueWaitMinutes), 240)]
    public void Validate_AcceptsTheBoundaries(string property, object value)
    {
        var options = new PlayerbotBattlegroundOptions();
        typeof(PlayerbotBattlegroundOptions).GetProperty(property)!.SetValue(options, value);
        options.Validate();
    }

    [Fact]
    public void BindsFromWorldPlayerbotsBattlegrounds()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [Prefix + "Enabled"] = "false",
            [Prefix + "QueueChancePerMinute"] = "0.25",
            [Prefix + "MaxBotsPerBattleground"] = "12",
            [Prefix + "PreferFillingRealPlayerQueues"] = "false",
            [Prefix + "RaiseCapToFillTemplate"] = "false",
            [Prefix + "MaxTravelYards"] = "800",
            [Prefix + "MaxQueueWaitMinutes"] = "7",
        }).Build();
        using ServiceProvider services = new ServiceCollection().AddLogging().AddWorldDaemon(configuration).BuildServiceProvider();
        PlayerbotOptions options = services.GetRequiredService<IOptions<PlayerbotOptions>>().Value;
        options.Validate();
        PlayerbotBattlegroundOptions bg = options.Battlegrounds;
        Assert.False(bg.Enabled);
        Assert.Equal(0.25f, bg.QueueChancePerMinute);
        Assert.Equal(12, bg.MaxBotsPerBattleground);
        Assert.False(bg.PreferFillingRealPlayerQueues);
        Assert.False(bg.RaiseCapToFillTemplate);
        Assert.Equal((800, 7), (bg.MaxTravelYards, bg.MaxQueueWaitMinutes));
        Assert.Equal(bg.MaxBotsPerBattleground, PlayerbotOptions.Bind(configuration).Battlegrounds.MaxBotsPerBattleground);
    }

    [Fact]
    public void EveryBattlegroundOption_HasALiveReloadKey()
    {
        string[] keys = [.. WorldConfigKeys.All.Where(key => key.Path.StartsWith(Prefix, StringComparison.Ordinal)).Select(key => key.Path)];
        string[] properties = [.. typeof(PlayerbotBattlegroundOptions).GetProperties().Select(p => Prefix + p.Name)];
        Assert.Equal(properties.Order(), keys.Order());
        Assert.All(WorldConfigKeys.All.Where(key => keys.Contains(key.Path)), key => Assert.True(key.Live, key.Path));
    }

    [Fact]
    public async Task ReloadConfig_IsLive()
    {
        string path = Path.Combine(_directory, "appsettings.json");
        File.WriteAllText(path, """
            { "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Enabled": true, "Battlegrounds": {
                "Enabled": false, "QueueChancePerMinute": 0.5, "MaxBotsPerBattleground": 8, "PreferFillingRealPlayerQueues": false, "RaiseCapToFillTemplate": false,
                "MaxTravelYards": 900, "MaxQueueWaitMinutes": 12 } } } }
            """);
        var playerbots = new PlayerbotOptions { Enabled = true };
        PlayerbotBattlegroundOptions bg = playerbots.Battlegrounds;
        ReloadCoordinator coordinator = Reloader(path, playerbots);

        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.Same(bg, playerbots.Battlegrounds); // changed in place: the coordinator holds the same object
        Assert.False(bg.Enabled);
        Assert.Equal(0.5f, bg.QueueChancePerMinute);
        Assert.Equal((8, 900, 12), (bg.MaxBotsPerBattleground, bg.MaxTravelYards, bg.MaxQueueWaitMinutes));
        Assert.False(bg.PreferFillingRealPlayerQueues);
        Assert.False(bg.RaiseCapToFillTemplate);
        Assert.Contains("no changes", (await coordinator.ReloadAsync("config")).Message);

        // Out of range: refused, nothing changes.
        foreach (string bad in new[] { "\"QueueChancePerMinute\": 1.5", "\"MaxBotsPerBattleground\": 81", "\"MaxTravelYards\": 5", "\"MaxQueueWaitMinutes\": 0" })
        {
            File.WriteAllText(path, $$"""{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5, "Playerbots": { "Battlegrounds": { {{bad}} } } } }""");
            Assert.NotEqual(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
            Assert.Equal((0.5f, 8, 900, 12), (bg.QueueChancePerMinute, bg.MaxBotsPerBattleground, bg.MaxTravelYards, bg.MaxQueueWaitMinutes));
        }

        // Removed: the defaults come back.
        File.WriteAllText(path, """{ "World": { "AutosaveIntervalMs": 0, "TickIntervalMs": 5 } }""");
        Assert.Equal(ReloadStatus.Applied, (await coordinator.ReloadAsync("config")).Status);
        Assert.True(bg.Enabled && bg.PreferFillingRealPlayerQueues && bg.RaiseCapToFillTemplate);
        Assert.Equal((0.05f, 20, 1500, 30), (bg.QueueChancePerMinute, bg.MaxBotsPerBattleground, bg.MaxTravelYards, bg.MaxQueueWaitMinutes));
    }

    private ReloadCoordinator Reloader(string path, PlayerbotOptions playerbots)
    {
        var configuration = new ConfigurationManager();
        configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
        IServiceCollection services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddSingleton(Options.Create(playerbots));
        var coordinator = new ReloadCoordinator(NullLogger.Instance);
        coordinator.Attach(_world);
        coordinator.Register(new ConfigContentReloadable(services.BuildServiceProvider()));
        _world.Start();
        return coordinator;
    }

    [Fact]
    public void ResolveWarsongMidfield_WhenMidpointIsInsideUnwalkableStumpFootprint_SnapsHorizontallyToWalkableMesh()
    {
        var map = _world.GetMap(489);
        var ownStand = new System.Numerics.Vector3(1539.5f, 1481.2f, 352.0f);
        var enemyStand = new System.Numerics.Vector3(917.4f, 1434.0f, 345.0f);
        var midpoint = (ownStand + enemyStand) / 2f;
        var stumpMesh = new MidfieldStumpMesh(midpoint, stumpRadius: 4.5f, floorZ: 308.0f);
        ArcaneCore.Game.Maps.Collision.WorldCollision.Of(_world).Install(lineOfSight: stumpMesh, pathfinder: stumpMesh);

        System.Numerics.Vector3 resolved = PlayerbotBattlegroundCoordinator.ResolveWarsongMidfield(map, ownStand, enemyStand);

        Assert.True(PlayerbotNavigation.OnWalkableMesh(map, resolved), $"expected resolved midfield {resolved} to be on walkable mesh outside stump");
    }

    /// <summary>The midfield reach probe reads its tile without blocking the world thread: it asks non-blocking and takes "pending" as walkable.</summary>
    [Fact]
    public void ResolveWarsongMidfield_ReadsTilesWithoutBlocking_AndAcceptsAPendingProbe()
    {
        var map = _world.GetMap(489);
        var ownStand = new System.Numerics.Vector3(1539.5f, 1481.2f, 352.0f);
        var enemyStand = new System.Numerics.Vector3(917.4f, 1434.0f, 345.0f);
        var mesh = new TileReadingMidfieldMesh(floorZ: 308.0f);
        ArcaneCore.Game.Maps.Collision.WorldCollision.Of(_world).Install(lineOfSight: mesh, pathfinder: mesh);

        System.Numerics.Vector3 resolved = PlayerbotBattlegroundCoordinator.ResolveWarsongMidfield(map, ownStand, enemyStand);

        Assert.Equal(0, mesh.BlockingReads);
        Assert.True(mesh.PendingAnswers > 0);
        Assert.Equal(((ownStand + enemyStand) / 2f).X, resolved.X, 0.01f); // pending is walkable: the midpoint stands
    }

    private sealed class TileReadingMidfieldMesh(float floorZ)
        : ArcaneCore.Game.Maps.Collision.ILineOfSight, ArcaneCore.Game.Maps.Collision.IPathfinder, ArcaneCore.Game.Maps.Collision.IMeshPointQuery
    {
        public int BlockingReads { get; private set; }
        public int PendingAnswers { get; private set; }
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, System.Numerics.Vector3 from, System.Numerics.Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, System.Numerics.Vector3 from, System.Numerics.Vector3 to, float modifyDistance, out System.Numerics.Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float zSearch, float maxSearchDistance) => floorZ;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float zSearch, out ArcaneCore.Game.Maps.Collision.ModelAreaInfo info) { info = default; return false; }

        public ArcaneCore.Game.Maps.Collision.PathResult FindPath(uint mapId, System.Numerics.Vector3 start, System.Numerics.Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
        {
            if (options is { LoadTiles: true, NonBlockingTiles: false })
            {
                BlockingReads++;
                return ArcaneCore.Game.Maps.Collision.PathResult.StraightLine(start with { Z = floorZ }, end with { Z = floorZ }, ArcaneCore.Game.Maps.Collision.PathType.Normal);
            }

            PendingAnswers++;
            return new ArcaneCore.Game.Maps.Collision.PathResult(ArcaneCore.Game.Maps.Collision.PathType.TilesPending, [start]);
        }

        public bool TryGetRandomPointAround(uint mapId, System.Numerics.Vector3 center, float radius, Random random, ArcaneCore.Game.Maps.Collision.PathOptions? options, out System.Numerics.Vector3 point)
        {
            point = center;
            return false;
        }

        public ArcaneCore.Game.Maps.Collision.MeshRaycastResult Raycast(uint mapId, System.Numerics.Vector3 start, System.Numerics.Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
            => new(true, false, 1f, end with { Z = floorZ });
    }

    private sealed class MidfieldStumpMesh(System.Numerics.Vector3 stumpCenter, float stumpRadius, float floorZ)
        : ArcaneCore.Game.Maps.Collision.ILineOfSight, ArcaneCore.Game.Maps.Collision.IPathfinder, ArcaneCore.Game.Maps.Collision.IMeshPointQuery
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, System.Numerics.Vector3 from, System.Numerics.Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, System.Numerics.Vector3 from, System.Numerics.Vector3 to, float modifyDistance, out System.Numerics.Vector3 hit) { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float zSearch, float maxSearchDistance) => floorZ;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float zSearch, out ArcaneCore.Game.Maps.Collision.ModelAreaInfo info) { info = default; return false; }

        public ArcaneCore.Game.Maps.Collision.PathResult FindPath(uint mapId, System.Numerics.Vector3 start, System.Numerics.Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
        {
            if (System.Numerics.Vector2.Distance(new System.Numerics.Vector2(start.X, start.Y), new System.Numerics.Vector2(stumpCenter.X, stumpCenter.Y)) < stumpRadius)
                return ArcaneCore.Game.Maps.Collision.PathResult.None(start);
            return ArcaneCore.Game.Maps.Collision.PathResult.StraightLine(start with { Z = floorZ }, end with { Z = floorZ }, ArcaneCore.Game.Maps.Collision.PathType.Normal);
        }

        public bool TryGetRandomPointAround(uint mapId, System.Numerics.Vector3 center, float radius, Random random, ArcaneCore.Game.Maps.Collision.PathOptions? options, out System.Numerics.Vector3 point)
        {
            point = center;
            return false;
        }

        public ArcaneCore.Game.Maps.Collision.MeshRaycastResult Raycast(uint mapId, System.Numerics.Vector3 start, System.Numerics.Vector3 end, ArcaneCore.Game.Maps.Collision.PathOptions? options = null)
        {
            if (System.Numerics.Vector2.Distance(new System.Numerics.Vector2(start.X, start.Y), new System.Numerics.Vector2(stumpCenter.X, stumpCenter.Y)) < stumpRadius)
                return ArcaneCore.Game.Maps.Collision.MeshRaycastResult.Unavailable(end);
            return new ArcaneCore.Game.Maps.Collision.MeshRaycastResult(true, false, 1f, end with { Z = floorZ });
        }
    }
}
