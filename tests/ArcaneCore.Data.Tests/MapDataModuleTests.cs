using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Maps;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>The world map tables (<see cref="MapDataModule"/>).</summary>
public sealed class MapDataModuleTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void Module_IsDiscovered_AtItsAllocatedWorldVersion()
    {
        IDataModule module = Assert.Single(DataModules.All, m => m is MapDataModule);
        Assert.Equal(DatabaseComponent.World, module.Component);
        Assert.Equal(MapDataModule.Version, module.SchemaVersion);
        Assert.Contains(WorldDbContext.Schema.Steps, s => s.Version == MapDataModule.Version);
        Assert.True(WorldDbContext.Schema.CurrentVersion >= MapDataModule.Version);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task FreshWorldDatabase_HasTheMapTables_AndRoundTrips(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext world = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(world, WorldDbContext.Schema);
            Assert.Equal(WorldDbContext.Schema.CurrentVersion, (await world.Set<SchemaVersionRow>().SingleAsync()).Version);
            Assert.Equal(MapContent.Empty.Maps.Count, (await new EfMapDataStore(world).LoadAsync()).Maps.Count);
            Seed(world);
            await world.SaveChangesAsync();
        }

        await using (WorldDbContext world = TestContexts.Create<WorldDbContext>(cs))
        {
            MapContent content = await new EfMapDataStore(world).LoadAsync();
            AssertSeeded(content);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Version1WorldDatabase_IsUpgraded_KeepingItsRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (MapWorldV1Context v1 = TestContexts.Create<MapWorldV1Context>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(v1, MapWorldV1Context.Schema);
            v1.Set<ClassInfoRow>().Add(new ClassInfoRow { Class = 1, BaseHealth = 60, BaseMana = 0, PowerType = 1 });
            await v1.SaveChangesAsync();
        }

        await using (WorldDbContext world = TestContexts.Create<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(world, WorldDbContext.Schema);
            Assert.Equal(WorldDbContext.Schema.CurrentVersion, (await world.Set<SchemaVersionRow>().SingleAsync()).Version);
            Assert.Equal(60u, (await world.ClassInfo.SingleAsync()).BaseHealth);
            Seed(world);
            await world.SaveChangesAsync();
        }

        await using (WorldDbContext world = TestContexts.Create<WorldDbContext>(cs))
        {
            AssertSeeded(await new EfMapDataStore(world).LoadAsync());
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private static void Seed(WorldDbContext world)
    {
        world.Set<MapTemplateRow>().AddRange(
            new MapTemplateRow { Entry = 0, MapType = 0, MapName = "Eastern Kingdoms" },
            new MapTemplateRow { Entry = 36, Parent = 0, MapType = 1, PlayerLimit = 10, GhostEntranceMap = 0, GhostEntranceX = -11208.4f, GhostEntranceY = 1672.3f, MapName = "Deadmines" });
        world.Set<AreaTemplateRow>().Add(new AreaTemplateRow { Entry = 12, MapId = 0, ZoneId = 0, ExploreFlag = 41, AreaLevel = 1, Name = "Elwynn Forest", Team = 2 });
        world.Set<AreaTriggerTemplateRow>().Add(new AreaTriggerTemplateRow { Id = 78, Name = "Deadmines Entrance", MapId = 0, X = -11208.6f, Y = 1679.6f, Z = 24.6f, Radius = 0, BoxX = 5, BoxY = 10, BoxZ = 8, BoxOrientation = 1.5f });
        world.Set<AreaTriggerTeleportRow>().Add(new AreaTriggerTeleportRow { Id = 78, Name = "Deadmines Entrance", Message = "", RequiredLevel = 10, TargetMap = 36, TargetPositionX = -16.4f, TargetPositionY = -383.07f, TargetPositionZ = 61.78f, TargetOrientation = 1.86f });
        world.Set<GameTeleRow>().Add(new GameTeleRow { Id = 1, PositionX = -8913.23f, PositionY = 554.633f, PositionZ = 93.7944f, Orientation = 0, Map = 0, Name = "Stormwind" });
    }

    private static void AssertSeeded(MapContent content)
    {
        Assert.Equal([0u, 36u], content.Maps.Select(m => m.Entry));
        MapTemplate deadmines = content.Maps[1];
        Assert.Equal(MapType.Instance, deadmines.MapType);
        Assert.True(deadmines.IsDungeon);
        Assert.Equal("Deadmines", deadmines.Name);
        Assert.Equal(-1, content.Maps[0].GhostEntranceMap);
        Assert.Equal(0, deadmines.GhostEntranceMap);
        Assert.Equal(41u, Assert.Single(content.Areas).ExploreFlag);
        AreaTriggerTemplate trigger = Assert.Single(content.AreaTriggers);
        Assert.Equal(10f, trigger.BoxY);
        AreaTriggerTeleport teleport = Assert.Single(content.AreaTriggerTeleports);
        Assert.Equal((byte)10, teleport.RequiredLevel);
        Assert.Equal(36u, teleport.TargetMap);
        Assert.Equal(-383.07f, teleport.TargetY);
        GameTele tele = Assert.Single(content.GameTeles);
        Assert.Equal("Stormwind", tele.Name);
        Assert.Equal(554.633f, tele.Y);
    }
}

/// <summary>The world database as M1–M6 mapped it: world schema version 1 (three tables).</summary>
internal sealed class MapWorldV1Context(DbContextOptions<MapWorldV1Context> options) : DbContext(options)
{
    public static readonly SchemaDefinition Schema = new()
    {
        Component = "world",
        CurrentVersion = 1,
        Version1Tables = ["player_create_info", "race_info", "class_info"],
    };

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        SchemaBootstrapper.MapVersionTable(modelBuilder, Schema);
        modelBuilder.Entity<PlayerCreateInfoRow>(entity =>
        {
            entity.ToTable("player_create_info");
            entity.HasKey(r => new { r.Race, r.Class });
        });
        modelBuilder.Entity<RaceInfoRow>(entity =>
        {
            entity.ToTable("race_info");
            entity.HasKey(r => new { r.Race, r.Gender });
        });
        modelBuilder.Entity<ClassInfoRow>(entity =>
        {
            entity.ToTable("class_info");
            entity.HasKey(r => r.Class);
        });
    }
}
