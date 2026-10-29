using System.Collections.Concurrent;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Protocol;
using ArcaneCore.World.RealmList;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.RealmList;

/// <summary>
/// The world keeps the logon daemon's per-realm character count of an account in step with the characters database: it recounts after a
/// create and after a delete (vmangos CharacterHandler.cpp:312-314, Player.cpp:4492 → World.cpp:2891-2905) and repairs a count that drifted
/// when the account lists its characters. The write happens on the session task after the client has its answer, so the tests wait for it.
/// </summary>
public sealed class RealmCharacterCountWorldTests
{
    private const string Account = "COUNTER";

    [Fact]
    public async Task CharacterCount_IsMaintainedOnCreateAndDelete()
    {
        var counts = new CountStore();
        await using var host = WorldTestHost.Start(configureServices: services => Register(services, counts, realmId: 7));
        byte[] key = await host.AddAccountAsync(Account);
        int accountId = (await host.Accounts.FindByUsernameAsync(Account))!.Id;

        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(Account, key);

        await client.CreateCharacterAsync("First");
        await WaitForCountAsync(counts, 7, accountId, 1);
        await client.CreateCharacterAsync("Second");
        await WaitForCountAsync(counts, 7, accountId, 2);
        Assert.False(counts.Has(1, accountId)); // the count is per realm: realm 1 is not this world's

        int firstId = (await host.Characters.GetByAccountAsync(accountId)).First().Id;
        await client.SendAsync(WorldOpcode.CmsgCharDelete, BitConverter.GetBytes((ulong)firstId));
        Assert.Equal((byte)CharResult.CharDeleteSuccess, (await client.ReadAsync()).Payload[0]);
        await WaitForCountAsync(counts, 7, accountId, 1);
    }

    [Fact]
    public async Task ListingTheCharacters_RepairsADriftedCount()
    {
        var counts = new CountStore();
        await using var host = WorldTestHost.Start(configureServices: services => Register(services, counts, realmId: 7));
        byte[] key = await host.AddAccountAsync(Account);
        int accountId = (await host.Accounts.FindByUsernameAsync(Account))!.Id;
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(Account, key);
        await client.CreateCharacterAsync("Only");
        await WaitForCountAsync(counts, 7, accountId, 1);

        await counts.SetCountAsync(7, accountId, 5); // a count that no longer matches the characters
        await client.SendAsync(WorldOpcode.CmsgCharEnum, []);
        Assert.Equal(WorldOpcode.SmsgCharEnum, (await client.ReadAsync()).Opcode);
        await WaitForCountAsync(counts, 7, accountId, 1);
    }

    [Fact]
    public async Task RealmIdZero_WritesNoCounts()
    {
        var counts = new CountStore();
        await using var host = WorldTestHost.Start(configureServices: services => Register(services, counts, realmId: 0));
        byte[] key = await host.AddAccountAsync(Account);
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(Account, key);
        await client.CreateCharacterAsync("Quiet");
        await client.SendAsync(WorldOpcode.CmsgCharEnum, []);
        Assert.Equal(WorldOpcode.SmsgCharEnum, (await client.ReadAsync()).Opcode);

        Assert.Equal(0, counts.Writes);
    }

    private static void Register(IServiceCollection services, CountStore counts, int realmId)
    {
        services.AddSingleton<IRealmCharacterCountStore>(counts);
        services.AddSingleton(Options.Create(new RealmStatusOptions { Id = realmId }));
    }

    private static async Task WaitForCountAsync(CountStore counts, int realmId, int accountId, int expected)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            IReadOnlyDictionary<int, int> now = await counts.GetCountsAsync(accountId);
            if (now.TryGetValue(realmId, out int value) && value == expected)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for realm {realmId} count {expected}; it is {(now.TryGetValue(realmId, out int v) ? v : "absent")}");
            await Task.Delay(5);
        }
    }

    private sealed class CountStore : IRealmCharacterCountStore
    {
        private readonly ConcurrentDictionary<(int Realm, int Account), int> _counts = new();
        private int _writes;

        public int Writes => Volatile.Read(ref _writes);

        public bool Has(int realmId, int accountId) => _counts.ContainsKey((realmId, accountId));

        public Task<IReadOnlyDictionary<int, int>> GetCountsAsync(int accountId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<int, int>>(_counts.Where(c => c.Key.Account == accountId).ToDictionary(c => c.Key.Realm, c => c.Value));

        public Task SetCountAsync(int realmId, int accountId, int count, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writes);
            _counts[(realmId, accountId)] = count;
            return Task.CompletedTask;
        }
    }
}
