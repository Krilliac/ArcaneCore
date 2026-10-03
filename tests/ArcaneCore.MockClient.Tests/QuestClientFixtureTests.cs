using System.Security.Cryptography;
using System.Text.Json;
using ArcaneCore.Cryptography;
using ArcaneCore.Data;
using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Npc;
using ArcaneCore.Data.Quests;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.World;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Social;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ArcaneCore.MockClient.Tests;

/// <summary>Persistent repository-authored fixture files and transactional seed safety.</summary>
public sealed class QuestClientFixtureTests : IDisposable
{
    private const string AccountName = "fixtureplayer";
    private const string Password = "Quest123!";
    private readonly OwnedFixtureDirectory _directory = OwnedFixtureDirectory.Create();

    [Theory]
    [InlineData(SyntheticQuestProfile.SelfTest)]
    [InlineData(SyntheticQuestProfile.ManualClient)]
    public async Task Seed_ProfilesKeepTheOrdinaryRewardAndPreserveBaselineSchema(SyntheticQuestProfile profile)
    {
        using var deadline = TestDeadline();
        string path = await CreateWorldDatabaseAsync(deadline.Token);
        await using (WorldDbContext seed = WorldContext(path))
        {
            await SyntheticQuestContent.SeedAsync(seed, profile, deadline.Token);
            Assert.Empty(seed.ChangeTracker.Entries());
            Assert.Null(seed.Database.CurrentTransaction);
        }

        await using WorldDbContext db = WorldContext(path);
        await AssertSeedAsync(db, profile, deadline.Token);
        await AssertBaselineAsync(db, deadline.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SyntheticQuestContent.SeedAsync(
            db, profile == SyntheticQuestProfile.SelfTest ? SyntheticQuestProfile.ManualClient : SyntheticQuestProfile.SelfTest,
            deadline.Token));
        await AssertSeedAsync(db, profile, deadline.Token);
        await AssertBaselineAsync(db, deadline.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seed_ExistingConflictingOrUnrelatedContentIsRefusedWithoutOverwritingIt(bool conflictingItem)
    {
        using var deadline = TestDeadline();
        string path = await CreateWorldDatabaseAsync(deadline.Token);
        await using (WorldDbContext existing = WorldContext(path))
        {
            if (conflictingItem)
            {
                existing.Set<ItemTemplateRow>().Add(new ItemTemplateRow
                    { Entry = 900040, Name = "Previously owned item", DisplayId = 77, Stackable = 3 });
            }
            else
            {
                existing.Set<CreatureMovementRow>().Add(new CreatureMovementRow
                    { SpawnGuid = 777, Point = 1, X = 123, Y = 456, Z = 789 });
            }
            await existing.SaveChangesAsync(deadline.Token);
        }

        await using (WorldDbContext seed = WorldContext(path))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => SyntheticQuestContent.SeedAsync(
                seed, SyntheticQuestProfile.ManualClient, deadline.Token));
            Assert.Empty(seed.ChangeTracker.Entries());
            Assert.Null(seed.Database.CurrentTransaction);
        }

        await using WorldDbContext verify = WorldContext(path);
        Assert.Equal(0, await verify.Set<QuestTemplate>().CountAsync(deadline.Token));
        Assert.Equal(0, await verify.Set<CreatureTemplateRow>().CountAsync(deadline.Token));
        Assert.Equal(0, await verify.Set<CreatureSpawnRow>().CountAsync(deadline.Token));
        if (conflictingItem)
        {
            ItemTemplateRow item = Assert.Single(await verify.Set<ItemTemplateRow>().AsNoTracking().ToListAsync(deadline.Token));
            Assert.Equal((900040u, "Previously owned item", 77u, 3u), (item.Entry, item.Name, item.DisplayId, item.Stackable));
        }
        else
        {
            CreatureMovementRow row = Assert.Single(await verify.Set<CreatureMovementRow>().AsNoTracking().ToListAsync(deadline.Token));
            Assert.Equal((777u, 1u, 123f, 456f, 789f), (row.SpawnGuid, row.Point, row.X, row.Y, row.Z));
            Assert.Equal(0, await verify.Set<ItemTemplateRow>().CountAsync(deadline.Token));
        }
        await AssertBaselineAsync(verify, deadline.Token);
    }

    [Fact]
    public async Task Seed_InterruptionAfterRealSaveChangesRollsBackEveryContentTableAndCanRetry()
    {
        using var deadline = TestDeadline();
        string path = await CreateWorldDatabaseAsync(deadline.Token);
        var failure = new SeedSaveFailure();
        await using (WorldDbContext seed = WorldContext(path, failure))
        {
            await Assert.ThrowsAsync<IOException>(() => SyntheticQuestContent.SeedAsync(
                seed, SyntheticQuestProfile.ManualClient, deadline.Token));
            Assert.True(failure.AfterSaveReached);
            Assert.Empty(seed.ChangeTracker.Entries());
            Assert.Null(seed.Database.CurrentTransaction);
            Assert.Equal(0, await seed.SaveChangesAsync(deadline.Token));
        }

        await using (WorldDbContext verify = WorldContext(path))
        {
            Assert.Equal(0, await verify.Set<QuestTemplate>().CountAsync(deadline.Token));
            Assert.Equal(0, await verify.Set<CreatureTemplateRow>().CountAsync(deadline.Token));
            Assert.Equal(0, await verify.Set<CreatureSpawnRow>().CountAsync(deadline.Token));
            Assert.Equal(0, await verify.Set<CreatureModelInfoRow>().CountAsync(deadline.Token));
            Assert.Equal(0, await verify.Set<CreatureQuestStarterRow>().CountAsync(deadline.Token));
            Assert.Equal(0, await verify.Set<CreatureQuestEnderRow>().CountAsync(deadline.Token));
            Assert.Equal(0, await verify.Set<ItemTemplateRow>().CountAsync(deadline.Token));
            await AssertBaselineAsync(verify, deadline.Token);
        }
        await using (WorldDbContext retry = WorldContext(path))
        {
            await SyntheticQuestContent.SeedAsync(retry, SyntheticQuestProfile.ManualClient, deadline.Token);
        }
        await using WorldDbContext after = WorldContext(path);
        await AssertSeedAsync(after, SyntheticQuestProfile.ManualClient, deadline.Token);
        await AssertBaselineAsync(after, deadline.Token);
    }

    [Fact]
    public async Task Prepare_ExportsFreshDatabasesSafeConfigurationCredentialsAndAuthoredFactionData()
    {
        using var deadline = TestDeadline();
        ClientFixtureResult fixture = await PrepareAsync(deadline.Token);
        Assert.Equal("FIXTUREPLAYER", fixture.AccountName);
        Assert.Equal(Path.GetFullPath(fixture.Directory), fixture.Directory);
        string[] files = [fixture.ConfigurationPath, fixture.ManifestPath, fixture.FactionPath,
            fixture.AuthDatabasePath, fixture.CharacterDatabasePath, fixture.WorldDatabasePath];
        Assert.All(files, file =>
        {
            Assert.True(File.Exists(file), file);
            Assert.Equal(fixture.Directory, Path.GetDirectoryName(file));
        });
        Assert.Equal(6, Directory.GetFiles(fixture.Directory).Length);
        IConfigurationRoot configuration = new ConfigurationBuilder().AddJsonFile(fixture.ConfigurationPath).Build();
        using IDisposable configLifetime = (IDisposable)configuration;
        Assert.Equal("127.0.0.1", configuration["Auth:BindAddress"]);
        Assert.Equal("127.0.0.1", configuration["World:BindAddress"]);
        Assert.Equal("3724", configuration["Auth:Port"]);
        Assert.Equal("8085", configuration["World:Port"]);
        Assert.Equal("False", configuration["Auth:AutocreateAccounts"], ignoreCase: true);
        Assert.Equal(fixture.FactionPath, configuration["Quests:FactionTemplateDbcPath"]);
        Assert.Equal("900003", Assert.Single(configuration.GetSection("Quests:OrdinaryRewardQuestIds").GetChildren()).Value);
        foreach ((string component, string path) in new[]
        {
            ("Auth", fixture.AuthDatabasePath), ("Characters", fixture.CharacterDatabasePath), ("World", fixture.WorldDatabasePath),
        })
        {
            Assert.Equal("Sqlite", configuration[$"Database:{component}:Provider"]);
            var connection = new SqliteConnectionStringBuilder(configuration[$"Database:{component}:ConnectionString"]
                ?? throw new InvalidOperationException("The exported fixture has no database connection string."));
            Assert.Equal(path, connection.DataSource);
            Assert.Equal(SqliteOpenMode.ReadWrite, connection.Mode);
            Assert.False(connection.Pooling);
        }

        await using (var auth = new AuthDbContext(Options<AuthDbContext>(fixture.AuthDatabasePath)))
        {
            Account account = Assert.Single(await auth.Accounts.AsNoTracking().ToListAsync(deadline.Token));
            Assert.Equal(fixture.AccountName, account.Username);
            Assert.Equal(AccountStatus.Active, account.Status);
            Assert.Equal(AccountSecurity.Player, account.Security);
            Assert.Null(account.SessionKey);
            Assert.Equal(32, account.Salt.Length);
            Assert.Equal(WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(account.Salt, account.Username, Password),
                WowSrp6.KeyLength), account.Verifier);
            Assert.Equal("127.0.0.1:8085", Assert.Single(await auth.Realms.AsNoTracking().ToListAsync(deadline.Token)).Address);
        }
        await using (var characters = new CharacterDbContext(Options<CharacterDbContext>(fixture.CharacterDatabasePath)))
        {
            Assert.Equal(0, await characters.Set<CharacterRecord>().CountAsync(deadline.Token));
            Assert.Equal(CharacterDbContext.Schema.CurrentVersion,
                Assert.Single(await characters.Set<SchemaVersionRow>().AsNoTracking().ToListAsync(deadline.Token)).Version);
        }
        await using (WorldDbContext world = WorldContext(fixture.WorldDatabasePath))
        {
            await AssertSeedAsync(world, SyntheticQuestProfile.ManualClient, deadline.Token);
            Assert.True(await world.PlayerCreateInfo.AnyAsync(row => row.Race == 1 && row.Class == 1, deadline.Token));
        }

        FactionTemplateCatalog catalog = FactionTemplateDbcReader.Load(fixture.FactionPath);
        Assert.Equal(3, catalog.Count);
        Assert.Equal(new FactionTemplateRecord(1, 1, 0, 1, 0, 0), catalog.Find(1));
        Assert.Equal(new FactionTemplateRecord(35, 0, 0, 8, 0, 0), catalog.Find(35));
        Assert.Equal(new FactionTemplateRecord(14, 0, 0, 8, 0, 1), catalog.Find(14));
        Assert.True(catalog.TryNpcHostility(35, 1, out bool guideHostile));
        Assert.False(guideHostile);
        Assert.True(catalog.TryNpcHostility(14, 1, out bool targetHostile));
        Assert.True(targetHostile);
        string manifestText = await File.ReadAllTextAsync(fixture.ManifestPath, deadline.Token);
        Assert.DoesNotContain(Password, manifestText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, await File.ReadAllTextAsync(fixture.ConfigurationPath, deadline.Token), StringComparison.OrdinalIgnoreCase);
        using JsonDocument manifest = JsonDocument.Parse(manifestText);
        Assert.Equal(1, manifest.RootElement.GetProperty("fixtureDefinitionVersion").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(manifest.RootElement.GetProperty("buildInformationalVersion").GetString()));
        Assert.Equal("ManualClient", manifest.RootElement.GetProperty("profile").GetString());
        Assert.Contains("Repository-authored", manifest.RootElement.GetProperty("contentOrigin").GetString()!);
        Assert.False(manifest.RootElement.TryGetProperty("password", out _));
        Assert.False(manifest.RootElement.TryGetProperty("sessionKey", out _));
        JsonElement hashes = manifest.RootElement.GetProperty("initialSha256");
        Assert.Equal(5, hashes.EnumerateObject().Count());
        foreach (string file in files.Where(file => file != fixture.ManifestPath))
        {
            string expected = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file, deadline.Token))).ToLowerInvariant();
            Assert.Equal(expected, hashes.GetProperty(Path.GetFileName(file)).GetString());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Prepare_ExistingEmptyDirectoryPopulatedDirectoryOrFileIsNeverOverwritten(int existing)
    {
        using var deadline = TestDeadline();
        string destination = NewPath("existing");
        string marker = existing == 2 ? destination : Path.Combine(destination, "keep.txt");
        if (existing != 2) Directory.CreateDirectory(destination);
        if (existing != 0) await File.WriteAllTextAsync(marker, "Owned existing content", deadline.Token);
        await Assert.ThrowsAsync<IOException>(() => ClientFixture.PrepareAsync(
            new ClientFixtureOptions(destination, AccountName, Password), deadline.Token));
        if (existing == 0) Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        else Assert.Equal("Owned existing content", await File.ReadAllTextAsync(marker, deadline.Token));
        if (existing == 1) Assert.Single(Directory.GetFiles(destination));
    }

    [Fact]
    public async Task Prepare_PreCanceledRequestDoesNotCreateAnyFixtureOutput()
    {
        string destination = NewPath("canceled");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClientFixture.PrepareAsync(
            new ClientFixtureOptions(destination, AccountName, Password), canceled.Token));
        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(destination));
        Assert.Single(Directory.EnumerateFileSystemEntries(_directory.Path)); // Only the owned-root marker.
    }

    [Fact]
    public async Task GeneratedConfiguration_LoadsManualContentThroughNormalWorldHostAndFactionFile()
    {
        using var deadline = TestDeadline();
        CancellationToken token = deadline.Token;
        ClientFixtureResult fixture = await PrepareAsync(token);
        IConfigurationRoot configuration = new ConfigurationBuilder().AddJsonFile(fixture.ConfigurationPath).Build();
        using IDisposable configLifetime = (IDisposable)configuration;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAuthDatabase(configuration).AddCharacterDatabase(configuration).AddWorldDatabase(configuration);
        services.AddWorldDaemon(configuration);
        // Start only the owned in-process simulation. No daemon listener binds the configured ports.
        services.Remove(services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(WorldServer)));
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
            { ValidateScopes = true, ValidateOnBuild = true });
        Assert.Null(provider.GetService<FactionTemplateCatalog>());
        WorldHost host = provider.GetServices<IHostedService>().OfType<WorldHost>().Single();
        WorldRuntime world = provider.GetRequiredService<WorldRuntime>();
        await host.StartAsync(token);
        try
        {
            await provider.GetRequiredService<SocialFeature>().GuildsLoaded.WaitAsync(token);
            await world.InvokeAsync(() =>
            {
                CreatureWorldFeature creatures = provider.GetRequiredService<CreatureWorldFeature>();
                Assert.Equal(2, creatures.Content.TemplateCount);
                Assert.Equal(3, creatures.Content.SpawnCount);
                var guide = creatures.Content.FindTemplate(900010)!;
                var target = creatures.Content.FindTemplate(900030)!;
                Assert.Equal((35u, 2u, 10u), (guide.Faction, guide.NpcFlags, guide.MinLevelHealth));
                Assert.Equal((14u, 0u, 1u), (target.Faction, target.NpcFlags, target.MinLevelHealth));
                Assert.Equal(new uint[] { 49, 0, 0, 0 }, guide.DisplayIds);
                Assert.Equal(new uint[] { 49, 0, 0, 0 }, target.DisplayIds);
                QuestNpcFeature quests = provider.GetRequiredService<QuestNpcFeature>();
                Assert.Equal(fixture.FactionPath, quests.Options.FactionTemplateDbcPath);
                Assert.IsType<CreatureQuestLookup>(quests.Services.Deps.Creatures);
                Assert.Equal(3, quests.Services.Quests.Count);
                Assert.Equal(new uint[] { 900002, 900003 }, quests.Services.Quests.StartersOf(900010));
                Assert.Equal(new uint[] { 900003 }, quests.Services.Quests.EndersOf(900010));
                Assert.Equal(1234, quests.Services.Quests.Get(900003)!.Template.RewOrReqMoney);
                Assert.Equal(new uint[] { 900003 }, quests.Options.OrdinaryRewardQuestIds);
                return true;
            }).WaitAsync(token);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
        Assert.All(new[] { fixture.AuthDatabasePath, fixture.CharacterDatabasePath, fixture.WorldDatabasePath },
            path => Assert.True(File.Exists(path))); // The exported fixture survives simulation shutdown.
    }

    public void Dispose() => _directory.Delete();

    private Task<ClientFixtureResult> PrepareAsync(CancellationToken token)
        => ClientFixture.PrepareAsync(new ClientFixtureOptions(NewPath("fixture"), AccountName, Password), token);

    private string NewPath(string prefix) => Path.Combine(_directory.Path, prefix + "-" + Guid.NewGuid().ToString("N"));

    private async Task<string> CreateWorldDatabaseAsync(CancellationToken token)
    {
        string path = NewPath("world") + ".sqlite";
        await using WorldDbContext db = WorldContext(path);
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema, cancellationToken: token);
        db.PlayerCreateInfo.Add(new PlayerCreateInfoRow { Race = 1, Class = 1, MapId = 0, ZoneId = 12, X = 11, Y = 22, Z = 33 });
        db.RaceInfo.Add(new RaceInfoRow { Race = 1, Gender = 0, DisplayId = 49, FactionTemplate = 1 });
        db.ClassInfo.Add(new ClassInfoRow { Class = 1, BaseHealth = 60, PowerType = 1 });
        await db.SaveChangesAsync(token);
        return path;
    }

    private static DbContextOptions<TContext> Options<TContext>(string path) where TContext : DbContext
        => new DbContextOptionsBuilder<TContext>().UseSqlite(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false, Cache = SqliteCacheMode.Private, DefaultTimeout = 1 }.ToString()).Options;

    private static WorldDbContext WorldContext(string path, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<WorldDbContext>(Options<WorldDbContext>(path));
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new WorldDbContext(options.Options);
    }

    private static async Task AssertBaselineAsync(WorldDbContext db, CancellationToken token)
    {
        PlayerCreateInfoRow start = Assert.Single(await db.PlayerCreateInfo.AsNoTracking().ToListAsync(token));
        Assert.Equal((1, 1, 0u, 12u, 11f, 22f, 33f), ((int)start.Race, (int)start.Class, start.MapId, start.ZoneId, start.X, start.Y, start.Z));
        Assert.Single(await db.RaceInfo.AsNoTracking().ToListAsync(token));
        Assert.Single(await db.ClassInfo.AsNoTracking().ToListAsync(token));
        Assert.Equal(WorldDbContext.Schema.CurrentVersion, Assert.Single(await db.Set<SchemaVersionRow>().AsNoTracking().ToListAsync(token)).Version);
    }

    private static async Task AssertSeedAsync(WorldDbContext db, SyntheticQuestProfile profile, CancellationToken token)
    {
        bool manual = profile == SyntheticQuestProfile.ManualClient;
        QuestTemplate[] quests = await db.Set<QuestTemplate>().AsNoTracking().OrderBy(row => row.Entry).ToArrayAsync(token);
        Assert.Equal(new uint[] { 900001, 900002, 900003 }, quests.Select(row => row.Entry));
        Assert.Equal(new[] { "Mock journal", "Mock NPC quest", "Mock combat reward" }, quests.Select(row => row.Title));
        QuestTemplate reward = quests[2];
        Assert.Equal((2, 1, 1, 0u, 900030, 2u, 1234),
            ((int)reward.Method, (int)reward.MinLevel, reward.QuestLevel, reward.Type,
                reward.ReqCreatureOrGOId1, reward.ReqCreatureOrGOCount1, reward.RewOrReqMoney));
        Assert.Equal((900040u, 1u, 900041u, 1u, 900042u, 1u),
            (reward.RewItemId1, reward.RewItemCount1, reward.RewChoiceItemId1, reward.RewChoiceItemCount1, reward.RewChoiceItemId2, reward.RewChoiceItemCount2));
        Assert.Equal("Defeat both synthetic combat targets before returning.", reward.RequestItemsText);
        Assert.Equal("Choose one synthetic keepsake.", reward.OfferRewardText);
        Assert.Equal(0u, reward.RewXP);
        Assert.Equal(0u, reward.RewSpell);
        Assert.Equal(0u, reward.RewSpellCast);
        CreatureTemplateRow[] creatures = await db.Set<CreatureTemplateRow>().AsNoTracking().OrderBy(row => row.Entry).ToArrayAsync(token);
        Assert.Equal(2, creatures.Length);
        Assert.Equal((900010u, manual ? 35u : 900011u, 2u, manual ? 49u : 900012u, 10u),
            (creatures[0].Entry, creatures[0].Faction, creatures[0].NpcFlags, creatures[0].DisplayId1, creatures[0].MinLevelHealth));
        Assert.Equal((900030u, manual ? 14u : 900011u, 0u, manual ? 49u : 900012u, 1u),
            (creatures[1].Entry, creatures[1].Faction, creatures[1].NpcFlags, creatures[1].DisplayId1, creatures[1].MinLevelHealth));
        CreatureSpawnRow[] spawns = await db.Set<CreatureSpawnRow>().AsNoTracking().OrderBy(row => row.Guid).ToArrayAsync(token);
        Assert.Equal(new uint[] { 900020, 900021, 900022 }, spawns.Select(row => row.Guid));
        Assert.Equal(manual ? -8949.95f + 2 : -8948.95f, spawns[0].X);
        Assert.Equal(-8949.95f + (manual ? -4 : 0.5f), spawns[1].X);
        Assert.Equal(-8949.95f + (manual ? 4 : -0.5f), spawns[2].X);
        Assert.All(spawns, spawn => Assert.Equal((0u, -132.493f, 83.5312f), (spawn.MapId, spawn.Y, spawn.Z)));
        Assert.All(spawns.Skip(1), spawn => Assert.Equal((3600u, 3600u), (spawn.SpawnTimeMinSeconds, spawn.SpawnTimeMaxSeconds)));
        CreatureModelInfoRow model = Assert.Single(await db.Set<CreatureModelInfoRow>().AsNoTracking().ToListAsync(token));
        Assert.Equal((manual ? 49u : 900012u, 0.5f, 1.5f), (model.DisplayId, model.BoundingRadius, model.CombatReach));
        Assert.Equal(new[] { (900010u, 900002u), (900010u, 900003u) },
            (await db.Set<CreatureQuestStarterRow>().AsNoTracking().OrderBy(row => row.Quest).ToListAsync(token)).Select(row => (row.Id, row.Quest)));
        CreatureQuestEnderRow ender = Assert.Single(await db.Set<CreatureQuestEnderRow>().AsNoTracking().ToListAsync(token));
        Assert.Equal((900010u, 900003u), (ender.Id, ender.Quest));
        ItemTemplateRow[] items = await db.Set<ItemTemplateRow>().AsNoTracking().OrderBy(row => row.Entry).ToArrayAsync(token);
        Assert.Equal(new uint[] { 900040, 900041, 900042 }, items.Select(row => row.Entry));
        Assert.All(items, item =>
        {
            Assert.Equal(manual ? 6418u : item.Entry + 100, item.DisplayId);
            Assert.Equal((15u, 1u, 20u, -1, -1), (item.Class, item.Quality, item.Stackable, item.AllowableClass, item.AllowableRace));
        });
    }

    private sealed class SeedSaveFailure : SaveChangesInterceptor
    {
        internal bool AfterSaveReached { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<QuestTemplate>().Any() == true)
            {
                AfterSaveReached = true;
                throw new IOException("Synthetic interruption after real fixture SaveChanges.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private static CancellationTokenSource TestDeadline() => new(TimeSpan.FromSeconds(30));
}
