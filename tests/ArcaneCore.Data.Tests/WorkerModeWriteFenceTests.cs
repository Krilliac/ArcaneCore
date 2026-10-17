using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Social;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Social;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// Tier 3b groundwork (docs/design/hot-reload-tiers.md, section 5.3): character fencing is mandatory in worker mode. With
/// <c>Cluster:Worker:Enabled</c> the characters write fence is attached even when <c>Cluster:Fencing:Enabled</c> is unset or false,
/// so a world whose players can be handed to another process never writes a character without the ownership check. Combined mode
/// (worker off, fencing unset) is unfenced exactly as before.
/// </summary>
public sealed class WorkerModeWriteFenceTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("false", null, false)]
    [InlineData(null, "false", false)]
    [InlineData("true", null, true)]
    [InlineData(null, "true", true)]
    [InlineData("false", "true", true)]
    [InlineData("true", "true", true)]
    public void TheFenceIsRequired_WhenFencingIsOn_OrWorkerModeIsOn(string? fencing, string? worker, bool required)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DataServiceCollectionExtensions.FencingEnabledKey] = fencing,
            [DataServiceCollectionExtensions.WorkerEnabledKey] = worker,
        }).Build();

        Assert.Equal(required, DataServiceCollectionExtensions.IsCharacterWriteFenceRequired(configuration));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task WorkerMode_FencesEveryWrite_EvenWithFencingUnset(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using ServiceProvider services = await BuildAsync(connection, new() { ["Cluster:Worker:Enabled"] = "true" });
        int id = await CreateCharacterAsync(connection, "Workerfence");

        // Another world owns the characters database; this process holds no token (or an old one) and must write nothing.
        CharacterOwnershipToken other = await AcquireAsync(services, "other-world");
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            Assert.True(CharacterWrites.IsFenced(db));
            await AssertRejectedAsync(() => new EfSocialStore(db).SetSocialAsync(id, 900, SocialFlags.Friend));
        }

        // The owner's token passes the same fence.
        services.GetRequiredService<CharacterOwnershipContext>().Current = other;
        await using (AsyncServiceScope scope = services.CreateAsyncScope())
        {
            CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
            await new EfSocialStore(db).SetSocialAsync(id, 901, SocialFlags.Friend);
        }

        await using CharacterDbContext read = TestContexts.Create<CharacterDbContext>(connection);
        SocialEntry only = Assert.Single(await new EfSocialStore(read).GetSocialAsync(id));
        Assert.Equal(901, only.OtherId);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task WorkerMode_FencesWrites_EvenWithFencingExplicitlyOff(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using ServiceProvider services = await BuildAsync(connection, new()
        {
            ["Cluster:Worker:Enabled"] = "true",
            ["Cluster:Fencing:Enabled"] = "false",
        });
        int id = await CreateCharacterAsync(connection, "Workeroff");
        await AcquireAsync(services, "other-world");

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        Assert.True(CharacterWrites.IsFenced(db));
        await AssertRejectedAsync(() => new EfSocialStore(db).SetSocialAsync(id, 900, SocialFlags.Friend));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CombinedMode_WithFencingUnset_IsUnfencedAsBefore(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using ServiceProvider services = await BuildAsync(connection, []);
        int id = await CreateCharacterAsync(connection, "Combined");
        await AcquireAsync(services, "other-world");

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        CharacterDbContext db = scope.ServiceProvider.GetRequiredService<CharacterDbContext>();
        Assert.False(CharacterWrites.IsFenced(db));
        await new EfSocialStore(db).SetSocialAsync(id, 900, SocialFlags.Friend); // no fence: the write lands (the host's claim watch is the guard)
    }

    private static async Task<ServiceProvider> BuildAsync(DatabaseConnectionOptions connection, Dictionary<string, string?> extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = connection.Provider.ToString(),
            ["Database:ConnectionString"] = connection.ConnectionString,
        };
        foreach ((string key, string? value) in extra)
            settings[key] = value;
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCharacterDatabase(configuration);
        ServiceProvider provider = services.BuildServiceProvider();
        await provider.GetRequiredService<CharacterDbInitializer>().InitializeAsync();
        return provider;
    }

    private static async Task<CharacterOwnershipToken> AcquireAsync(ServiceProvider services, string node)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ICharacterOwnershipStore>().TryAcquireAsync("world", node, Guid.NewGuid()))!;
    }

    private static async Task<int> CreateCharacterAsync(DatabaseConnectionOptions connection, string name)
    {
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(connection);
        return (await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 1, Name = name, Race = 1, Class = 1, Level = 1 })).Id;
    }

    private static async Task AssertRejectedAsync(Func<Task> write)
    {
        Exception? thrown = await Record.ExceptionAsync(write);
        Assert.NotNull(thrown);
        for (Exception? e = thrown; e is not null; e = e.InnerException)
        {
            if (e is CharacterOwnershipRejectedException)
                return;
        }

        Assert.Fail("expected a CharacterOwnershipRejectedException but got " + thrown);
    }
}
