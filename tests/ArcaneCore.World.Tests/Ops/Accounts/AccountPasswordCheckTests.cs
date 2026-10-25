using ArcaneCore.Cryptography;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Ops.Accounts;
using ArcaneCore.World.Tests.Bans;
using Xunit;

namespace ArcaneCore.World.Tests.Ops.Accounts;

/// <summary>
/// The shared name-and-password check (remote console and web login): equal work for every name, status column and ban rows judged as the realm
/// and the gateway judge them.
/// </summary>
public sealed class AccountPasswordCheckTests
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    private static async Task<(InMemoryAccountStore Accounts, InMemoryBanStore Bans, Account Account)> StoresAsync(
        AccountStatus status = AccountStatus.Active, string name = "CHECKME")
    {
        var accounts = new InMemoryAccountStore();
        var bans = new InMemoryBanStore();
        byte[] salt = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];
        Account account = await accounts.CreateAsync(new Account
        {
            Username = name,
            Salt = salt,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, name, "secret"), 32),
            Status = status,
            Security = AccountSecurity.GameMaster,
            SessionKey = new byte[40],
        });
        return (accounts, bans, account);
    }

    [Fact]
    public async Task ARightPassword_OfAnActiveUnbannedAccount_Passes_CaseInsensitively()
    {
        (InMemoryAccountStore accounts, InMemoryBanStore bans, Account account) = await StoresAsync();

        AccountCheckResult result = await AccountPasswordCheck.CheckAsync(accounts, bans, "checkme", "secret", Clock, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Same(account, result.Account);
    }

    [Theory]
    [InlineData("CHECKME", "wrong")]
    [InlineData("NOSUCHACCOUNT", "secret")]
    [InlineData("CHECKME", "")]
    [InlineData("", "secret")]
    public async Task AWrongPasswordOrUnknownName_IsRefused_WithoutAnAccount(string name, string password)
    {
        (InMemoryAccountStore accounts, InMemoryBanStore bans, _) = await StoresAsync();

        AccountCheckResult result = await AccountPasswordCheck.CheckAsync(accounts, bans, name, password, Clock, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Null(result.Account);
        Assert.Equal(AccountCheckOutcome.UnknownOrWrongPassword, result.Outcome);
    }

    [Fact]
    public async Task AnActiveColumn_WithAPermanentBanRow_IsBanned()
    {
        (InMemoryAccountStore accounts, InMemoryBanStore bans, Account account) = await StoresAsync();
        long now = Clock.GetUtcNow().ToUnixTimeSeconds();
        bans.AddAccountRow(account.Id, now, now);

        AccountCheckResult result = await AccountPasswordCheck.CheckAsync(accounts, bans, "CHECKME", "secret", Clock, CancellationToken.None);

        Assert.Equal(AccountCheckOutcome.Banned, result.Outcome);
        Assert.Null(result.Account);
    }

    [Fact]
    public async Task ATemporaryBanRow_Refuses_UntilItEnds()
    {
        (InMemoryAccountStore accounts, InMemoryBanStore bans, Account account) = await StoresAsync();
        long now = Clock.GetUtcNow().ToUnixTimeSeconds();
        bans.AddAccountRow(account.Id, now - 100, now + 3600);
        var later = new FixedClock(Clock.GetUtcNow().AddHours(2));

        Assert.Equal(AccountCheckOutcome.Banned, (await AccountPasswordCheck.CheckAsync(accounts, bans, "CHECKME", "secret", Clock, CancellationToken.None)).Outcome);
        // The store itself still lists the row; the evaluator, on the later clock, no longer calls it in force.
        Assert.True((await AccountPasswordCheck.CheckAsync(accounts, bans, "CHECKME", "secret", later, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task ASuspendedColumn_IsNotActive()
    {
        (InMemoryAccountStore accounts, InMemoryBanStore bans, _) = await StoresAsync(AccountStatus.Suspended);

        AccountCheckResult result = await AccountPasswordCheck.CheckAsync(accounts, bans, "CHECKME", "secret", Clock, CancellationToken.None);

        Assert.Equal(AccountCheckOutcome.NotActive, result.Outcome);
    }

    [Fact]
    public async Task WithoutABanStore_OnlyTheStatusColumnCounts()
    {
        (InMemoryAccountStore accounts, _, _) = await StoresAsync();

        Assert.True((await AccountPasswordCheck.CheckAsync(accounts, null, "CHECKME", "secret", Clock, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task EveryNameAsksTheBanStoreOnce_SoTheWorkDoesNotTellAnAccountFromAnInventedName()
    {
        (InMemoryAccountStore accounts, InMemoryBanStore bans, _) = await StoresAsync();
        int queries = 0;
        bans.AfterAccountQuery = _ => queries++;

        await AccountPasswordCheck.CheckAsync(accounts, bans, "CHECKME", "secret", Clock, CancellationToken.None);
        int right = queries;
        await AccountPasswordCheck.CheckAsync(accounts, bans, "CHECKME", "wrong", Clock, CancellationToken.None);
        int wrong = queries - right;
        await AccountPasswordCheck.CheckAsync(accounts, bans, "NOSUCH", "secret", Clock, CancellationToken.None);
        int unknown = queries - right - wrong;

        Assert.Equal((1, 1, 1), (right, wrong, unknown));
    }

    [Fact]
    public async Task IsStillActive_FollowsTheColumnAndTheBanRows()
    {
        (InMemoryAccountStore accounts, InMemoryBanStore bans, Account account) = await StoresAsync();
        Assert.True(await AccountPasswordCheck.IsStillActiveAsync(bans, account, Clock, CancellationToken.None));

        long now = Clock.GetUtcNow().ToUnixTimeSeconds();
        bans.AddAccountRow(account.Id, now, now);
        Assert.False(await AccountPasswordCheck.IsStillActiveAsync(bans, account, Clock, CancellationToken.None));

        account.Status = AccountStatus.Banned;
        Assert.False(await AccountPasswordCheck.IsStillActiveAsync(null, account, Clock, CancellationToken.None));
        _ = accounts;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
