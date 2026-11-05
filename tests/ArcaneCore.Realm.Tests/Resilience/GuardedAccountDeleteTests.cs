using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Realm.Net;
using ArcaneCore.Realm.Ops.ServerConsole;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ArcaneCore.Realm.Tests.Resilience;

/// <summary>
/// The realm console's <c>account delete</c> goes through the Auth database guard for all three stores it uses (account, realm character
/// counts, account admin), runs the account-row delete before the count-row delete, and leaves a defined state when the second statement fails.
/// </summary>
public sealed class GuardedAccountDeleteTests
{
    private const string AccountName = "ALICE";
    private const int AccountId = 7;
    private const string InnerKey = "ArcaneCore.Realm.Resilience.Inner";

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    /// <summary>Admin store whose delete removes the in-memory account, logs its call into the shared journal, and can be made to fail.</summary>
    private sealed class JournalAdmin(InMemoryAccountStore accounts, List<string> journal) : IAccountAdmin
    {
        public bool Fail { get; set; }

        public Task<bool> DeleteAsync(string username, CancellationToken cancellationToken = default)
        {
            journal.Add("admin.delete");
            if (Fail)
            {
                throw new InvalidOperationException("admin delete failed");
            }

            return Task.FromResult(accounts.Remove(username));
        }

        public Task<bool> SetStatusAsync(string username, AccountStatus status, int? actorAccountId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> RevokeSessionKeyAsync(int accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<int, string>> GetUsernamesAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlySet<int>> FindNonActiveAsync(IReadOnlyCollection<int> accountIds, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class JournalCounts(List<string> journal) : IRealmCharacterCountStore
    {
        private readonly Dictionary<(int Realm, int Account), int> _rows = [];

        public bool Fail { get; set; }

        public int RowsOf(int accountId) => _rows.Keys.Count(k => k.Account == accountId);

        public Task<IReadOnlyDictionary<int, int>> GetCountsAsync(int accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<int, int>>(_rows.Where(r => r.Key.Account == accountId).ToDictionary(r => r.Key.Realm, r => r.Value));

        public Task SetCountAsync(int realmId, int accountId, int count, CancellationToken cancellationToken = default)
        {
            _rows[(realmId, accountId)] = count;
            return Task.CompletedTask;
        }

        public Task<int> DeleteByAccountAsync(int accountId, CancellationToken cancellationToken = default)
        {
            journal.Add("counts.delete");
            if (Fail)
            {
                throw new InvalidOperationException("count delete failed");
            }

            (int Realm, int Account)[] keys = [.. _rows.Keys.Where(k => k.Account == accountId)];
            foreach ((int Realm, int Account) key in keys)
            {
                _rows.Remove(key);
            }

            return Task.FromResult(keys.Length);
        }
    }

    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _provider;

        public InMemoryAccountStore Accounts { get; } = new();

        public List<string> Journal { get; } = [];

        public JournalAdmin Admin { get; }

        public JournalCounts Counts { get; }

        public RealmConsoleCommands Commands { get; }

        public IServiceProvider Services => _provider;

        public Rig()
        {
            Admin = new JournalAdmin(Accounts, Journal);
            Counts = new JournalCounts(Journal);
            Accounts.CreateAsync(new Account { Id = AccountId, Username = AccountName, Salt = new byte[32], Verifier = new byte[32] }).GetAwaiter().GetResult();
            Counts.SetCountAsync(1, AccountId, 0).GetAwaiter().GetResult();
            Counts.SetCountAsync(2, AccountId, 0).GetAwaiter().GetResult();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IAccountStore>(Accounts);
            services.AddSingleton<IAccountAdmin>(Admin);
            services.AddSingleton<IRealmCharacterCountStore>(Counts);
            services.AddSingleton<IRealmStore>(_ => new InMemoryRealmStore(
                [new RealmEntry { Id = 1, Name = "R1", Address = "a:1" }, new RealmEntry { Id = 2, Name = "R2", Address = "a:1" }]));
            services.AddRealmResilience(new ConfigurationBuilder().Build());
            _provider = services.BuildServiceProvider();
            Commands = new RealmConsoleCommands(
                _provider.GetRequiredService<IServiceScopeFactory>(), () => 0, TimeProvider.System, new FakeLifetime());
        }

        public async Task<string> RunAsync(string line)
        {
            var output = new StringWriter();
            await Commands.ExecuteAsync(line, output, CancellationToken.None);
            return output.ToString();
        }

        public void Dispose() => _provider.Dispose();
    }

    [Fact]
    public void AddRealmResilience_GuardsTheAccountAdminAndTheCountStore_AndKeepsTheInnerOnesUnderTheInnerKey()
    {
        using var rig = new Rig();

        using IServiceScope scope = rig.Services.CreateScope();
        Assert.Equal("GuardedAccountAdmin", scope.ServiceProvider.GetRequiredService<IAccountAdmin>().GetType().Name);
        Assert.Equal("GuardedRealmCharacterCountStore", scope.ServiceProvider.GetRequiredService<IRealmCharacterCountStore>().GetType().Name);
        Assert.Equal("GuardedAccountStore", scope.ServiceProvider.GetRequiredService<IAccountStore>().GetType().Name);
        Assert.Same(rig.Admin, scope.ServiceProvider.GetRequiredKeyedService<IAccountAdmin>(InnerKey));
        Assert.Same(rig.Counts, scope.ServiceProvider.GetRequiredKeyedService<IRealmCharacterCountStore>(InnerKey));
    }

    [Fact]
    public void ProductionGraph_ResolvesTheGuardedAccountAdminAndCountStore_OverTheEfStores()
    {
        string dir = Path.Combine(Path.GetTempPath(), "arcanecore-realmdelete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = dir, EnvironmentName = Environments.Production });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Auth:Provider"] = "Sqlite",
                ["Database:Auth:ConnectionString"] = $"Data Source={Path.Combine(dir, "auth.db")}",
            });
            builder.Services.AddRealmProductionServices(builder.Configuration);
            builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }));
            using IHost host = builder.Build();

            using IServiceScope scope = host.Services.CreateScope();
            Assert.Equal("GuardedAccountAdmin", scope.ServiceProvider.GetRequiredService<IAccountAdmin>().GetType().Name);
            Assert.Equal("GuardedRealmCharacterCountStore", scope.ServiceProvider.GetRequiredService<IRealmCharacterCountStore>().GetType().Name);
            Assert.Equal("EfAccountStore", scope.ServiceProvider.GetRequiredKeyedService<IAccountAdmin>(InnerKey).GetType().Name);
            Assert.Equal("EfRealmCharacterCountStore", scope.ServiceProvider.GetRequiredKeyedService<IRealmCharacterCountStore>(InnerKey).GetType().Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GuardedCountStore_DeleteByAccount_RemovesTheRowsAndReturnsTheCount()
    {
        using var rig = new Rig();
        await rig.Counts.SetCountAsync(1, 99, 0);
        using IServiceScope scope = rig.Services.CreateScope();
        IRealmCharacterCountStore guarded = scope.ServiceProvider.GetRequiredService<IRealmCharacterCountStore>();
        Assert.Equal("GuardedRealmCharacterCountStore", guarded.GetType().Name);
        Assert.NotSame(rig.Counts, guarded);

        Assert.Equal(2, await guarded.DeleteByAccountAsync(AccountId));

        Assert.Equal(0, rig.Counts.RowsOf(AccountId));
        Assert.Equal(1, rig.Counts.RowsOf(99)); // another account's row stays
        Assert.Equal(0, await guarded.DeleteByAccountAsync(AccountId)); // nothing left
    }

    [Fact]
    public async Task AccountDelete_RemovesTheAccountRowFirst_ThenTheCountRows()
    {
        using var rig = new Rig();

        await rig.RunAsync("account delete alice");
        Assert.Empty(rig.Journal);
        string reply = await rig.RunAsync("account delete alice confirm");

        Assert.Contains("deleted account 'ALICE'", reply, StringComparison.Ordinal);
        Assert.Equal(["admin.delete", "counts.delete"], rig.Journal);
        Assert.Null(await rig.Accounts.FindByUsernameAsync(AccountName));
        Assert.Equal(0, rig.Counts.RowsOf(AccountId));
    }

    [Fact]
    public async Task AccountDelete_WhenTheCountDeleteFails_TheAccountIsAlreadyGone_AndOnlyDeadCountRowsRemain()
    {
        using var rig = new Rig();
        rig.Counts.Fail = true;
        await rig.RunAsync("account delete alice");

        await Assert.ThrowsAnyAsync<Exception>(() => rig.RunAsync("account delete alice confirm"));

        Assert.Equal(["admin.delete", "counts.delete"], rig.Journal);
        Assert.Null(await rig.Accounts.FindByUsernameAsync(AccountName)); // not a live account with its count rows gone
        Assert.Equal(2, rig.Counts.RowsOf(AccountId));
    }

    [Fact]
    public async Task AccountDelete_WhenTheAccountDeleteFails_TheCountRowsAreNotTouched()
    {
        using var rig = new Rig();
        rig.Admin.Fail = true;
        await rig.RunAsync("account delete alice");

        await Assert.ThrowsAnyAsync<Exception>(() => rig.RunAsync("account delete alice confirm"));

        Assert.Equal(["admin.delete"], rig.Journal);
        Assert.NotNull(await rig.Accounts.FindByUsernameAsync(AccountName));
        Assert.Equal(2, rig.Counts.RowsOf(AccountId));
    }
}
