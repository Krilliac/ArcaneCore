using System.Net;
using System.Net.Sockets;
using System.Numerics;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Net;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Realm.Net;
using ArcaneCore.Realm.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Realm.Tests.Security;

/// <summary>
/// Security finding S1 (Codex security scan 2026-10-08): a pre-auth connection could send valid logon or reconnect
/// challenges back to back without ever proving, and each one cost an IP-ban read, an account read, a ban-row read and
/// fresh SRP state while the per-address failure budget (Net:Protection:AuthFailureBurstPerIp) was only peeked.
/// vmangos realmd accepts exactly one challenge per connection (AuthSocket.cpp:125-150 dispatches a challenge only in
/// STATUS_CHALLENGE, which no handler ever returns to). ArcaneCore keeps answering a retry after a refused challenge,
/// so: a challenge that abandons an issued, unproven one is charged to the address, and a connection is closed after
/// <see cref="LogonSession.MaxChallengesPerConnection"/> challenges whether or not a guard is configured.
/// </summary>
public sealed class RepeatedChallengeTests
{
    private const string User = "ALICE";
    private const string Password = "ALICEPW";

    private static NetGuard Guard(int burst)
        => new(new NetProtectionOptions { AuthFailureBurstPerIp = burst, AuthFailuresPerMinutePerIp = 1 }, () => 0, () => 0, new NetGuardTests.CapturingLogger());

    private static async Task<(CountingAccountStore Accounts, byte[] Salt)> SeedAsync(byte[]? sessionKey = null)
    {
        var inner = new InMemoryAccountStore();
        byte[] salt = WowSrp6.GenerateSalt();
        BigInteger verifier = WowSrp6.ComputeVerifier(salt, User, Password);
        await inner.CreateAsync(new Account
        {
            Username = User, Salt = salt, Verifier = WowSrp6.ToFixedLittleEndian(verifier, WowSrp6.KeyLength), SessionKey = sessionKey,
        });
        return (new CountingAccountStore(inner), salt);
    }

    private static NetworkStream StartSession(IAccountStore accounts, NetGuard? guard)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using TcpClient server = await listener.AcceptTcpClientAsync();
            listener.Stop();
            await using NetworkStream s = server.GetStream();
            var session = new LogonSession(
                s, accounts, new InMemoryRealmStore([new RealmEntry { Name = "R", Address = "127.0.0.1:8085" }]),
                new AuthOptions(), NullLogger.Instance, "127.0.0.1:50000", banStore: null, guard);
            try
            {
                await session.RunAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // connection closed by the client
            }
        });
        var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        return client.GetStream();
    }

    private static byte[] ReconnectChallenge(string user)
    {
        byte[] packet = CodexNetAuthRealmTests.Challenge(user);
        packet[0] = (byte)AuthCommand.ReconnectChallenge;
        return packet;
    }

    [Fact]
    public async Task RepeatedValidLogonChallenges_SpendTheFailureBudget_AndAreRefusedBeforeTheAccountLookup()
    {
        (CountingAccountStore accounts, _) = await SeedAsync();
        NetGuard guard = Guard(burst: 3);
        await using NetworkStream c = StartSession(accounts, guard);

        // The first challenge is free; each further one abandons an issued, unproven challenge and is charged.
        // Burst 3: challenges 2, 3 and 4 spend the three tokens, so the fourth is refused before any lookup.
        for (int i = 0; i < 3; i++)
        {
            await c.WriteAsync(CodexNetAuthRealmTests.Challenge(User));
            (byte result, _, _) = await CodexNetAuthRealmTests.ReadChallenge(c);
            Assert.Equal((byte)AuthResult.Success, result);
        }

        await c.WriteAsync(CodexNetAuthRealmTests.Challenge(User));
        (byte refused, _, _) = await CodexNetAuthRealmTests.ReadChallenge(c);
        Assert.Equal((byte)AuthResult.FailNoAccess, refused);
        Assert.True(await CodexNetAuthRealmTests.ReadsNothingThenClosesAsync(c));
        Assert.Equal(3, accounts.Lookups);
        Assert.Equal(1, guard.RefusedAuthAttempts);
    }

    [Fact]
    public async Task RepeatedValidReconnectChallenges_SpendTheFailureBudget_AndAreRefusedBeforeTheAccountLookup()
    {
        (CountingAccountStore accounts, _) = await SeedAsync(sessionKey: Enumerable.Range(1, 40).Select(i => (byte)i).ToArray());
        NetGuard guard = Guard(burst: 2);
        await using NetworkStream c = StartSession(accounts, guard);

        for (int i = 0; i < 2; i++)
        {
            await c.WriteAsync(ReconnectChallenge(User));
            byte[] reply = await CodexNetAuthRealmTests.ReadN(c, 34);
            Assert.Equal((byte)AuthCommand.ReconnectChallenge, reply[0]);
            Assert.Equal((byte)AuthResult.Success, reply[1]);
        }

        await c.WriteAsync(ReconnectChallenge(User));
        byte[] refused = await CodexNetAuthRealmTests.ReadN(c, 2);
        Assert.Equal((byte)AuthCommand.ReconnectChallenge, refused[0]);
        Assert.Equal((byte)AuthResult.FailNoAccess, refused[1]);
        Assert.True(await CodexNetAuthRealmTests.ReadsNothingThenClosesAsync(c));
        Assert.Equal(2, accounts.Lookups);
    }

    [Fact]
    public async Task WithoutAGuard_AConnectionIsClosedAfterTheChallengeCap_WithoutAnotherLookup()
    {
        (CountingAccountStore accounts, _) = await SeedAsync();
        await using NetworkStream c = StartSession(accounts, guard: null);

        for (int i = 0; i < LogonSession.MaxChallengesPerConnection; i++)
        {
            await c.WriteAsync(CodexNetAuthRealmTests.Challenge(User));
            (byte result, _, _) = await CodexNetAuthRealmTests.ReadChallenge(c);
            Assert.Equal((byte)AuthResult.Success, result);
        }

        // One more: vmangos closes on an out-of-state command without a reply, and so does the cap.
        await c.WriteAsync(CodexNetAuthRealmTests.Challenge(User));
        Assert.True(await CodexNetAuthRealmTests.ReadsNothingThenClosesAsync(c));
        Assert.Equal(LogonSession.MaxChallengesPerConnection, accounts.Lookups);
    }

    [Fact]
    public async Task AWrongPasswordThenARetryOnTheSameConnection_IsChargedOnce_AndTheRetrySucceeds()
    {
        (CountingAccountStore accounts, _) = await SeedAsync();
        NetGuard guard = Guard(burst: 2);
        IpKey key = IpKey.From(IPAddress.Loopback);
        await using NetworkStream c = StartSession(accounts, guard);

        await c.WriteAsync(CodexNetAuthRealmTests.Challenge(User));
        (_, byte[] b, byte[] salt) = await CodexNetAuthRealmTests.ReadChallenge(c);
        await c.WriteAsync(CodexNetAuthRealmTests.ProofFor(User, User, "WRONG", salt, b));
        byte[] failed = await CodexNetAuthRealmTests.ReadN(c, 4);
        Assert.NotEqual((byte)AuthResult.Success, failed[1]);
        Assert.Equal(1.0, guard.Table.TokensOf(key, RateBucket.AuthFailures));

        // The proof consumed the challenge, so the retry abandons nothing and is not charged again.
        await c.WriteAsync(CodexNetAuthRealmTests.Challenge(User));
        (byte again, byte[] b2, byte[] salt2) = await CodexNetAuthRealmTests.ReadChallenge(c);
        Assert.Equal((byte)AuthResult.Success, again);
        Assert.Equal(1.0, guard.Table.TokensOf(key, RateBucket.AuthFailures));
        await c.WriteAsync(CodexNetAuthRealmTests.ProofFor(User, User, Password, salt2, b2));
        byte[] ok = await CodexNetAuthRealmTests.ReadN(c, 2);
        Assert.Equal((byte)AuthResult.Success, ok[1]);
        Assert.Equal(1.0, guard.Table.TokensOf(key, RateBucket.AuthFailures));
    }

    [Fact]
    public async Task OneChallengePerConnection_AsTheClientSends_NeverTouchesTheBudget()
    {
        (CountingAccountStore accounts, _) = await SeedAsync();
        NetGuard guard = Guard(burst: 1);
        IpKey key = IpKey.From(IPAddress.Loopback);

        // A client that logs in, drops, and logs in again on a fresh connection, several times over: no charge.
        for (int i = 0; i < 4; i++)
        {
            await using NetworkStream c = StartSession(accounts, guard);
            await c.WriteAsync(CodexNetAuthRealmTests.Challenge(User));
            (byte result, byte[] b, byte[] salt) = await CodexNetAuthRealmTests.ReadChallenge(c);
            Assert.Equal((byte)AuthResult.Success, result);
            await c.WriteAsync(CodexNetAuthRealmTests.ProofFor(User, User, Password, salt, b));
            byte[] ok = await CodexNetAuthRealmTests.ReadN(c, 2);
            Assert.Equal((byte)AuthResult.Success, ok[1]);
        }

        Assert.Equal(1.0, guard.Table.TokensOf(key, RateBucket.AuthFailures));
        Assert.Equal(0, guard.RefusedAuthAttempts);
    }

    /// <summary>Counts account lookups: the database read each challenge costs.</summary>
    private sealed class CountingAccountStore(IAccountStore inner) : IAccountStore
    {
        private int _lookups;

        public int Lookups => Volatile.Read(ref _lookups);

        public Task<Account?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _lookups);
            return inner.FindByUsernameAsync(username, cancellationToken);
        }

        public Task<Account> CreateAsync(Account account, CancellationToken cancellationToken = default) => inner.CreateAsync(account, cancellationToken);

        public Task UpdateCredentialsAsync(string username, byte[] salt, byte[] verifier, CancellationToken cancellationToken = default)
            => inner.UpdateCredentialsAsync(username, salt, verifier, cancellationToken);

        public Task UpdateSessionKeyAsync(string username, byte[] sessionKey, CancellationToken cancellationToken = default)
            => inner.UpdateSessionKeyAsync(username, sessionKey, cancellationToken);

        public Task<bool> UpdateSecurityAsync(string username, AccountSecurity security, CancellationToken cancellationToken = default)
            => inner.UpdateSecurityAsync(username, security, cancellationToken);
    }
}
