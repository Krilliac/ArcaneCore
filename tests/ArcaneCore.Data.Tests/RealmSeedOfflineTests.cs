using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Auth;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Realms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>A realm row seeded from configuration starts Stopped (offline) until a world reports in; rows that already exist are never touched.</summary>
public sealed class RealmSeedOfflineTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task CleanInstall_SeedsOffline_AndRerunIsNoOp(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        for (int pass = 0; pass < 2; pass++)
        {
            await RunInitializerAsync(connection);
            await using AuthDbContext db = TestContexts.Create<AuthDbContext>(connection);
            RealmEntry row = await db.Realms.SingleAsync();
            Assert.Equal("Seeded", row.Name);
            Assert.Equal(RealmRuntimeState.Stopped, row.State);
            Assert.Null(row.HeartbeatUtc);
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task ExistingUnknownRow_IsUntouched(DatabaseProvider provider)
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(provider);
        await using (AuthDbContext first = TestContexts.Create<AuthDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(first, AuthDbContext.Schema);
            first.Realms.Add(new RealmEntry { Name = "Manual", Address = "10.0.0.1:8085", State = RealmRuntimeState.Unknown });
            await first.SaveChangesAsync();
        }

        await RunInitializerAsync(connection);

        await using AuthDbContext db = TestContexts.Create<AuthDbContext>(connection);
        RealmEntry row = await db.Realms.SingleAsync();
        Assert.Equal("Manual", row.Name);
        Assert.Equal(RealmRuntimeState.Unknown, row.State);
    }

    private static async Task RunInitializerAsync(DatabaseConnectionOptions connection)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => TestContexts.Create<AuthDbContext>(connection));
        services.AddSingleton<IOptions<RealmSeedOptions>>(Microsoft.Extensions.Options.Options.Create(new RealmSeedOptions
        {
            Seed = [new RealmSeedEntry { Name = "Seeded", Address = "127.0.0.1:8085" }],
        }));
        await using ServiceProvider provider = services.BuildServiceProvider();
        await new AuthDbInitializer(provider, NullLogger<AuthDbInitializer>.Instance).InitializeAsync();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
