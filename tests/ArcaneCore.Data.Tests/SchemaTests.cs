using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The schema bootstrapper against real engines: components sharing one database, adoption of
/// pre-M5 databases, fail-closed mismatches, and additive upgrades. SQLite always runs;
/// MariaDB and PostgreSQL run when <see cref="TestDatabases"/> finds their connection strings
/// (CI provides both as service containers).
/// </summary>
public sealed class SchemaTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task AllComponents_ShareOneDatabase(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);

        // M1–M4 used EnsureCreated, which skips a context as soon as the database has any table;
        // the second and third components would never have been created.
        await using (AuthDbContext auth = Context<AuthDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(auth, AuthDbContext.Schema);
        }

        await using (CharacterDbContext chars = Context<CharacterDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(chars, CharacterDbContext.Schema);
        }

        await using (WorldDbContext world = Context<WorldDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(world, WorldDbContext.Schema);
        }

        await using (CharacterDbContext chars = Context<CharacterDbContext>(cs))
        {
            Assert.Equal(0, await chars.Characters.CountAsync());
            Assert.Equal(1, (await chars.Set<SchemaVersionRow>().SingleAsync()).Version);
        }

        await using (WorldDbContext world = Context<WorldDbContext>(cs))
        {
            Assert.Equal(0, await world.RaceInfo.CountAsync());
        }

        // A second start is a no-op.
        await using (AuthDbContext auth = Context<AuthDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(auth, AuthDbContext.Schema);
            Assert.Equal(0, await auth.Accounts.CountAsync());
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PreM5Database_IsAdoptedAsVersion1(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (CharacterDbContext chars = Context<CharacterDbContext>(cs))
        {
            // What M3's EnsureCreated produced: the tables, no version table.
            await chars.GetService<IRelationalDatabaseCreator>().CreateAsync();
            await chars.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
            await chars.Database.ExecuteSqlRawAsync("DROP TABLE " + Quote(chars, "characters_schema"));
            chars.Characters.Add(new CharacterRecord { AccountId = 1, Name = "Old" });
            await chars.SaveChangesAsync();
        }

        await using (CharacterDbContext chars = Context<CharacterDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(chars, CharacterDbContext.Schema);
            Assert.Equal(1, (await chars.Set<SchemaVersionRow>().SingleAsync()).Version);
            Assert.Equal("Old", (await chars.Characters.SingleAsync()).Name); // data kept
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task NewerDatabase_FailsClosed(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (AuthDbContext auth = Context<AuthDbContext>(cs))
        {
            await SchemaBootstrapper.EnsureAsync(auth, AuthDbContext.Schema);
            await auth.Database.ExecuteSqlRawAsync("UPDATE " + Quote(auth, "auth_schema") + " SET " + Quote(auth, "Version") + " = 99");
        }

        await using (AuthDbContext auth = Context<AuthDbContext>(cs))
        {
            var ex = await Assert.ThrowsAsync<SchemaMismatchException>(() => SchemaBootstrapper.EnsureAsync(auth, AuthDbContext.Schema));
            Assert.Contains("99", ex.Message);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PartialTables_FailClosed(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (WorldDbContext world = Context<WorldDbContext>(cs))
        {
            await world.GetService<IRelationalDatabaseCreator>().CreateAsync();
            await world.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
            await world.Database.ExecuteSqlRawAsync("DROP TABLE " + Quote(world, "world_schema"));
            await world.Database.ExecuteSqlRawAsync("DROP TABLE " + Quote(world, "class_info"));
        }

        await using (WorldDbContext world = Context<WorldDbContext>(cs))
        {
            await Assert.ThrowsAsync<SchemaMismatchException>(() => SchemaBootstrapper.EnsureAsync(world, WorldDbContext.Schema));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task UpgradeStep_AddsColumnAndTable_KeepingRows(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using (VersionOneContext v1 = new(Options<VersionOneContext>(cs)))
        {
            await SchemaBootstrapper.EnsureAsync(v1, VersionOneContext.Schema);
            v1.Widgets.Add(new WidgetV1 { Id = 1, Name = "kept" });
            await v1.SaveChangesAsync();
        }

        await using (VersionTwoContext v2 = new(Options<VersionTwoContext>(cs)))
        {
            await SchemaBootstrapper.EnsureAsync(v2, VersionTwoContext.Schema);

            WidgetV2 widget = await v2.Widgets.SingleAsync();
            Assert.Equal("kept", widget.Name);
            Assert.Equal(0u, widget.Weight); // added NOT NULL column defaulted for existing rows
            v2.Gadgets.Add(new Gadget { Id = 5 });
            await v2.SaveChangesAsync();
            Assert.Equal(2, (await v2.Set<SchemaVersionRow>().SingleAsync()).Version);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CharacterStore_SaveState_RoundTrips(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = Context<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);
        var store = new EfCharacterStore(db);

        CharacterRecord created = await store.CreateAsync(new CharacterRecord { AccountId = 3, Name = "Mover", MapId = 0, X = 1 });
        await store.SaveStateAsync(new CharacterState(created.Id, 1, 14, -618.5f, -4251.6f, 38.7f, 3.1f, 4, 600));

        CharacterRecord reloaded = (await store.GetByIdAsync(created.Id))!;
        Assert.Equal(1u, reloaded.MapId);
        Assert.Equal(14u, reloaded.ZoneId);
        Assert.Equal(-618.5f, reloaded.X);
        Assert.Equal(3.1f, reloaded.Orientation);
        Assert.Equal(4, reloaded.Level);
        Assert.Equal(600u, reloaded.PlayedTime);

        // Saving a deleted character is a no-op, not an error.
        await store.SaveStateAsync(new CharacterState(9999, 0, 0, 0, 0, 0, 0, 1, 1));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    private static string Quote(DbContext db, string identifier)
        => db.GetService<ISqlGenerationHelper>().DelimitIdentifier(identifier);

    private static DbContextOptions<T> Options<T>(DatabaseConnectionOptions cs) where T : DbContext
    {
        var builder = new DbContextOptionsBuilder<T>();
        DataServiceCollectionExtensions.ConfigureProvider(builder, cs);
        return builder.Options;
    }

    private static T Context<T>(DatabaseConnectionOptions cs) where T : DbContext
        => (T)Activator.CreateInstance(typeof(T), Options<T>(cs))!;

    // --- a component that evolves from version 1 to 2 ---------------------------------

    private sealed class WidgetV1
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class WidgetV2
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public uint Weight { get; set; }
    }

    private sealed class Gadget
    {
        public int Id { get; set; }
    }

    private sealed class VersionOneContext(DbContextOptions<VersionOneContext> options) : DbContext(options)
    {
        public static readonly SchemaDefinition Schema = new()
        {
            Component = "widgets",
            CurrentVersion = 1,
            Version1Tables = ["widget"],
        };

        public DbSet<WidgetV1> Widgets => Set<WidgetV1>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            SchemaBootstrapper.MapVersionTable(modelBuilder, Schema);
            modelBuilder.Entity<WidgetV1>(e =>
            {
                e.ToTable("widget");
                e.Property(w => w.Id).ValueGeneratedNever();
            });
        }
    }

    private sealed class VersionTwoContext(DbContextOptions<VersionTwoContext> options) : DbContext(options)
    {
        public static readonly SchemaDefinition Schema = new()
        {
            Component = "widgets",
            CurrentVersion = 2,
            Version1Tables = ["widget"],
            Steps =
            [
                new SchemaStep(2, [new AddColumnChange("widget", "Weight"), new CreateTableChange("gadget")]),
            ],
        };

        public DbSet<WidgetV2> Widgets => Set<WidgetV2>();

        public DbSet<Gadget> Gadgets => Set<Gadget>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            SchemaBootstrapper.MapVersionTable(modelBuilder, Schema);
            modelBuilder.Entity<WidgetV2>(e =>
            {
                e.ToTable("widget");
                e.Property(w => w.Id).ValueGeneratedNever();
            });
            modelBuilder.Entity<Gadget>(e =>
            {
                e.ToTable("gadget");
                e.Property(g => g.Id).ValueGeneratedNever();
            });
        }
    }
}
