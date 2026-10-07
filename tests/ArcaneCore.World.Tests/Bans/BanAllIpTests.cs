using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Bans;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Bans;

/// <summary>An in-memory <see cref="IAccountAddressStore"/> (thread-safe).</summary>
internal sealed class InMemoryAccountAddressStore : IAccountAddressStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, AccountAddressRecord> _rows = [];

    public AccountAddressRecord? Of(int accountId)
    {
        lock (_lock)
        {
            return _rows.GetValueOrDefault(accountId);
        }
    }

    public void Seed(int accountId, string ip)
    {
        lock (_lock)
        {
            _rows[accountId] = new AccountAddressRecord(accountId, ip, 1);
        }
    }

    public Task RecordAsync(int accountId, string ip, long nowUnix, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _rows[accountId] = new AccountAddressRecord(accountId, ip, nowUnix);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AccountAddressRecord>> FindByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<AccountAddressRecord>>(
                [.. _rows.Values.Where(r => r.Ip.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(r => r.AccountId)]);
        }
    }
}

/// <summary>
/// <c>.ban allip</c> (vmangos HandleBanAllIPCommand, AccountCommands.cpp:531-585) and the account last-address record it
/// reads, against the real world host.
/// </summary>
public sealed class BanAllIpTests
{
    private static (WorldTestHost Host, InMemoryAccountAddressStore Addresses) Start(BanOptions? options = null)
    {
        var addresses = new InMemoryAccountAddressStore();
        WorldTestHost host = WorldTestHost.Start(banOptions: options, configureServices: s => s.AddSingleton<IAccountAddressStore>(addresses));
        return (host, addresses);
    }

    private static async Task<int> IdAsync(WorldTestHost host, string account) => (await host.Accounts.FindByUsernameAsync(account))!.Id;

    private static async Task<string[]> LinesAsync(WorldTestClient client, string command)
    {
        await client.CollectAsync();
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        var lines = new List<string>();
        while (true)
        {
            string line = (await client.ReadChatAsync()).Text;
            lines.Add(line);
            if (line.Contains(" on this IP)", StringComparison.Ordinal) || line.StartsWith("No account found", StringComparison.Ordinal)
                || line.StartsWith("Syntax", StringComparison.Ordinal) || line.StartsWith("There is no such command", StringComparison.Ordinal))
            {
                return [.. lines];
            }
        }
    }

    [Fact]
    public async Task EnteringTheWorld_RecordsTheAccountsAddress()
    {
        (WorldTestHost host, InMemoryAccountAddressStore addresses) = Start();
        await using (host)
        {
            await using WorldTestClient client = await host.EnterWorldAsync("SEEN", "Seenone");
            int id = await IdAsync(host, "SEEN");
            await WorldTestHost.WaitForAsync(() => addresses.Of(id) is not null, "the address row");
            Assert.Equal("127.0.0.1", addresses.Of(id)!.Ip);
        }
    }

    [Fact]
    public async Task BanAllIp_BansTheLowLevelAccountsOnThePrefix_SparesHighLevelOnesTheInvokerAndBannedOnes_AndCountsThem()
    {
        (WorldTestHost host, InMemoryAccountAddressStore addresses) = Start();
        await using (host)
        {
            await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
            await using WorldTestClient alt = await host.EnterWorldAsync("ALTONE", "Altone");       // level 1, online: kicked
            await using WorldTestClient main = await host.EnterWorldAsync("MAINONE", "Mainone");    // level 60: spared
            await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Mainone")!.Level = 60);   // live level, not saved yet
            await host.AddAccountAsync("OLDBAN");
            await host.AddAccountAsync("ELSEWHERE");
            int oldBan = await IdAsync(host, "OLDBAN");
            await host.Bans.BanAccountAsync(new BanRequest(oldBan, 0, "earlier", "Someone"));
            addresses.Seed(oldBan, "127.0.0.9");                        // already banned: not counted as banned now
            addresses.Seed(await IdAsync(host, "ELSEWHERE"), "10.1.1.1"); // another address
            int altId = await IdAsync(host, "ALTONE");
            int mainId = await IdAsync(host, "MAINONE");
            int adminId = await IdAsync(host, "ADMIN");
            await WorldTestHost.WaitForAsync(() => addresses.Of(altId) is not null && addresses.Of(mainId) is not null && addresses.Of(adminId) is not null, "the address rows");

            string[] lines = await LinesAsync(admin, ".ban allip 127.0.0. \"multiboxing gold farm\"");

            Assert.Equal(["Account 'ALTONE' permanently banned. Reason: multiboxing gold farm", "1 accounts banned for multiboxing gold farm (4 on this IP)"], lines);
            Assert.True((await host.Bans.GetActiveAccountBanAsync(altId))!.IsPermanent);
            Assert.Null(await host.Bans.GetActiveAccountBanAsync(mainId));
            Assert.Null(await host.Bans.GetActiveAccountBanAsync(adminId));
            Assert.True(await alt.IsClosedByServerAsync());
        }
    }

    [Fact]
    public async Task BanAllIp_ReadsTheStoredLevelOfAnOfflineCharacter_AndDefaultsTheReason()
    {
        (WorldTestHost host, InMemoryAccountAddressStore addresses) = Start();
        await using (host)
        {
            await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
            WorldTestClient high = await host.EnterWorldAsync("HIGHOFF", "Highoff");
            WorldTestClient low = await host.EnterWorldAsync("LOWOFF", "Lowoff");
            await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Highoff")!.Level = 11);   // saved at logout: above the limit
            await high.DisposeAsync();
            await low.DisposeAsync();
            int highId = await IdAsync(host, "HIGHOFF");
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Highoff") is null && host.World.FindOnlinePlayer("Lowoff") is null, "both to leave the world");
            await WorldTestHost.WaitForAsync(() => host.SaveQueue.Pending == 0, "the logout saves");
            Assert.Equal(11, (await host.Characters.GetByAccountAsync(highId)).Single().Level);

            string[] lines = await LinesAsync(admin, ".ban allip 127.");

            Assert.Equal(["Account 'LOWOFF' permanently banned. Reason: <no reason given>", "1 accounts banned for <no reason given> (3 on this IP)"], lines);
            Assert.Null(await host.Bans.GetActiveAccountBanAsync(highId));
        }
    }

    [Fact]
    public async Task BanAllIp_ProtectHigherSecurity_SparesStaff_AndOffItBansThemAsVmangos()
    {
        (WorldTestHost host, InMemoryAccountAddressStore addresses) = Start();
        await using (host)
        {
            await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
            await host.AddAccountAsync("OTHERADM", AccountSecurity.Administrator);
            await host.AddAccountAsync("AGM", AccountSecurity.GameMaster);
            addresses.Seed(await IdAsync(host, "OTHERADM"), "10.9.9.1");
            addresses.Seed(await IdAsync(host, "AGM"), "10.9.9.2");

            Assert.Equal(["Account 'AGM' permanently banned. Reason: x", "1 accounts banned for x (2 on this IP)"], await LinesAsync(admin, ".ban allip 10.9.9. x"));
        }

        (host, addresses) = Start(new BanOptions { ProtectHigherSecurity = false });
        await using (host)
        {
            await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
            await host.AddAccountAsync("OTHERADM", AccountSecurity.Administrator);
            addresses.Seed(await IdAsync(host, "OTHERADM"), "10.9.9.1");

            Assert.Equal(["Account 'OTHERADM' permanently banned. Reason: x", "1 accounts banned for x (1 on this IP)"], await LinesAsync(admin, ".ban allip 10.9.9. x"));
        }
    }

    [Fact]
    public async Task BanAllIp_NoAccountOnTheAddress_ABadPrefix_AndTheSecurityLevel()
    {
        (WorldTestHost host, _) = Start();
        await using (host)
        {
            await using WorldTestClient admin = await host.EnterWorldAsync("ADMIN", "Admin", AccountSecurity.Administrator);
            await using WorldTestClient gm = await host.EnterWorldAsync("GMACC", "Gamemaster", AccountSecurity.GameMaster);

            Assert.Equal(["No account found on IP '9.9.9.'"], await LinesAsync(admin, ".ban allip 9.9.9."));
            Assert.StartsWith("Syntax: .ban allip", (await LinesAsync(admin, ".ban allip 10.0.0.1;drop"))[0], StringComparison.Ordinal);
            Assert.StartsWith("Syntax: .ban allip", (await LinesAsync(admin, ".ban allip"))[0], StringComparison.Ordinal);
            await gm.CollectAsync();
            await gm.SendChatAsync(ChatType.Say, Language.Common, ".ban allip 127.");   // Administrator only
            Assert.DoesNotContain("accounts banned", (await gm.ReadChatAsync()).Text, StringComparison.Ordinal);
            Assert.Null(await host.Bans.GetActiveAccountBanAsync(await IdAsync(host, "GMACC")));
        }
    }
}
