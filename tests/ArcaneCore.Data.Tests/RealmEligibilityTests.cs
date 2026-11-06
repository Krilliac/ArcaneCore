using ArcaneCore.Data.Auth;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Realms;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class RealmEligibilityTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();
    private static readonly DateTime Now = new(2026, 11, 6, 19, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task EligibilityRequiresFreshOnlineRealmAndActiveOrGraceBuild()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema);
            RealmEntry good = new() { Name = "Good", Address = "legacy:8085", ProtocolVersion = 7, Host = "localhost", Port = 8443, State = RealmRuntimeState.Online, HeartbeatUtc = Now.AddSeconds(-10), AllowedSecurityLevel = 1 };
            RealmEntry stale = new() { Name = "Stale", Address = "legacy:8086", ProtocolVersion = 7, Host = "stale.example", Port = 8443, State = RealmRuntimeState.Online, HeartbeatUtc = Now.AddMinutes(-5) };
            RealmEntry offline = new() { Name = "Offline", Address = "legacy:8087", ProtocolVersion = 7, Host = "offline.example", Port = 8443, State = RealmRuntimeState.Stopped, HeartbeatUtc = Now };
            RealmEntry badHost = new() { Name = "Bad", Address = "legacy:8088", ProtocolVersion = 7, Host = "https://bad.example/", Port = 8443, State = RealmRuntimeState.Online, HeartbeatUtc = Now };
            db.Realms.AddRange(good, stale, offline, badHost);
            await db.SaveChangesAsync();
            db.Set<RealmClientBuildRow>().AddRange(
                new RealmClientBuildRow { RealmId = good.Id, BuildId = 42, Status = LanternfallBuildStatus.Active, ChangedUtc = Now },
                new RealmClientBuildRow { RealmId = stale.Id, BuildId = 42, Status = LanternfallBuildStatus.Active, ChangedUtc = Now },
                new RealmClientBuildRow { RealmId = offline.Id, BuildId = 42, Status = LanternfallBuildStatus.Active, ChangedUtc = Now },
                new RealmClientBuildRow { RealmId = badHost.Id, BuildId = 42, Status = LanternfallBuildStatus.Active, ChangedUtc = Now });
            await db.SaveChangesAsync();
        }
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            IReadOnlyList<LanternfallRealmInfo> result = await new EfLanternfallRealmStore(db).GetEligibleAsync(7, 42, AccountSecurity.Moderator, Now, TimeSpan.FromMinutes(1));
            var realm = Assert.Single(result);
            Assert.Equal("Good", realm.Name);
            Assert.Equal((ushort)8443, realm.Port);
        }
    }

    [Fact]
    public async Task GraceBuildIsAllowedButRetiredAndWrongProtocolAreExcluded()
    {
        DatabaseConnectionOptions connection = await _databases.CreateAsync(DatabaseProvider.Sqlite);
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            await SchemaBootstrapper.EnsureAsync(db, AuthDbContext.Schema);
            RealmEntry grace = new() { Name = "Grace", Address = "legacy:1", ProtocolVersion = 8, Host = "grace.example", Port = 1, State = RealmRuntimeState.Online, HeartbeatUtc = Now };
            RealmEntry retired = new() { Name = "Retired", Address = "legacy:2", ProtocolVersion = 8, Host = "retired.example", Port = 2, State = RealmRuntimeState.Online, HeartbeatUtc = Now };
            db.Realms.AddRange(grace, retired); await db.SaveChangesAsync();
            db.Set<RealmClientBuildRow>().AddRange(
                new RealmClientBuildRow { RealmId = grace.Id, BuildId = 90, Status = LanternfallBuildStatus.Grace, ChangedUtc = Now },
                new RealmClientBuildRow { RealmId = retired.Id, BuildId = 90, Status = LanternfallBuildStatus.Retired, ChangedUtc = Now });
            await db.SaveChangesAsync();
        }
        await using (AuthDbContext db = TestContexts.Create<AuthDbContext>(connection))
        {
            IReadOnlyList<LanternfallRealmInfo> result = await new EfLanternfallRealmStore(db).GetEligibleAsync(8, 90, AccountSecurity.Player, Now, TimeSpan.FromMinutes(1));
            Assert.Equal("Grace", Assert.Single(result).Name);
            Assert.Empty(await new EfLanternfallRealmStore(db).GetEligibleAsync(7, 90, AccountSecurity.Player, Now, TimeSpan.FromMinutes(1)));
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
