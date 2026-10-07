using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Realm.Net;
using ArcaneCore.Realm.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Realm.Tests.Bans;

/// <summary>
/// The logon daemon enforces ban rows over a real loopback LogonSession: IP ban -> FAIL_NOACCESS 0x0D
/// before any SRP state (vmangos AuthSocket.cpp:338-352), permanent account ban -> FAIL_BANNED 0x03,
/// temporary -> FAIL_SUSPENDED 0x0C (:464-476). Retail order caveat: ArcaneCore validates the build and the
/// username charset before the IP check, so an IP-banned client with a wrong build sees VersionInvalid.
/// </summary>
public sealed class RealmBanEnforcementTests
{
    private const long Now = 1_800_000_000;
    private const int AccountId = 41;

    private readonly ManualClock _clock = new(Now);
    private readonly InMemoryBanStore _bans;

    public RealmBanEnforcementTests() => _bans = new InMemoryBanStore(_clock);

    [Fact]
    public async Task PermanentBanRow_AnswersBanned_EvenThoughTheStatusColumnIsActive()
    {
        _bans.AddAccountRow(AccountId, Now - 10, Now - 10);
        Assert.Equal((byte)AuthResult.Banned, await ChallengeResultAsync());
    }

    [Fact]
    public async Task TemporaryBanRow_AnswersSuspended()
    {
        _bans.AddAccountRow(AccountId, Now - 10, Now + 600);
        Assert.Equal((byte)AuthResult.Suspended, await ChallengeResultAsync());
    }

    [Fact]
    public async Task ExpiredTemporaryBan_AndInactiveRow_AllowTheLogon()
    {
        _bans.AddAccountRow(AccountId, Now - 700, Now - 100);
        _bans.AddAccountRow(AccountId, Now - 10, Now - 10, active: false);
        Assert.Equal((byte)AuthResult.Success, await ChallengeResultAsync());
    }

    [Fact]
    public async Task BanFollowedByUnban_AllowsTheLogon()
    {
        await _bans.BanAccountAsync(new BanRequest(AccountId, 0, "x", "GM"));
        Assert.Equal((byte)AuthResult.Banned, await ChallengeResultAsync());
        await _bans.UnbanAccountAsync(AccountId, "GM", "sorry");
        Assert.Equal((byte)AuthResult.Success, await ChallengeResultAsync());
    }

    [Fact]
    public async Task StatusColumnOverride_IsStillHonoured_WithoutAnyBanRow()
    {
        Assert.Equal((byte)AuthResult.Banned, await ChallengeResultAsync(status: AccountStatus.Banned));
        Assert.Equal((byte)AuthResult.Suspended, await ChallengeResultAsync(status: AccountStatus.Suspended));
    }

    [Fact]
    public async Task IpBan_AnswersNoAccess_BeforeAnySrpState_EvenForAnUnknownAccount()
    {
        _bans.AddIpRow("127.0.0.1", Now - 10, Now - 10);

        // The account does not exist and autocreate is off: retail checks the IP first (AuthSocket.cpp:338 precedes the account query).
        Assert.Equal((byte)AuthResult.FailNoAccess, await ChallengeResultAsync(createAccount: false, endpoint: "127.0.0.1:5555"));

        // No SRP state was kept: a proof right after the refused challenge is not accepted.
        (byte challengeResult, byte proofResult) = await ChallengeThenProofAsync("127.0.0.1:5555");
        Assert.Equal((byte)AuthResult.FailNoAccess, challengeResult);
        Assert.NotEqual((byte)AuthResult.Success, proofResult);
    }

    [Fact]
    public async Task IpBan_AppliesToAnIpv4MappedIpv6Spelling()
    {
        _bans.AddIpRow("127.0.0.1", Now - 10, Now + 3600);
        Assert.Equal((byte)AuthResult.FailNoAccess, await ChallengeResultAsync(endpoint: "[::ffff:127.0.0.1]:4000"));
    }

    [Fact]
    public async Task UnrelatedOrExpiredIpBan_AndUnparseableEndpoint_DoNotBlock()
    {
        _bans.AddIpRow("10.20.30.40", Now - 10, Now - 10);
        Assert.Equal((byte)AuthResult.Success, await ChallengeResultAsync(endpoint: "127.0.0.1:5555"));

        _bans.AddIpRow("127.0.0.1", Now - 700, Now - 100); // expired
        Assert.Equal((byte)AuthResult.Success, await ChallengeResultAsync(endpoint: "127.0.0.1:5555"));

        _bans.AddIpRow("127.0.0.1", Now - 10, Now - 10);
        Assert.Equal((byte)AuthResult.Success, await ChallengeResultAsync(endpoint: "test")); // no address, no IP check
    }

    [Fact]
    public async Task SessionWithoutABanStore_BehavesAsBefore()
    {
        Assert.Equal((byte)AuthResult.Success, await ChallengeResultAsync(useBans: false));
        Assert.Equal((byte)AuthResult.Banned, await ChallengeResultAsync(useBans: false, status: AccountStatus.Banned));
    }

    [Fact]
    public async Task StoreFailure_ClosesTheConnection_WithoutASuccessReply()
    {
        _bans.FailWith = new InvalidOperationException("database down");
        (int read, byte first) = await RawChallengeAsync();
        Assert.Equal(0, read); // fail closed: the connection ends, no AUTH_OK-style reply
        Assert.Equal(0, first);
    }

    // --- the logon daemon's IP-ban list (RealmIpBanCache) ----------------------------

    [Fact]
    public void TheDefaultPeriod_IsMangosdsBanListReloadTimer() => Assert.Equal(60, new AuthOptions().IpBanCacheSeconds);

    [Fact]
    public async Task ManyChallenges_ThroughOneListenersCache_ReadTheIpBanTableOnce()
    {
        var cache = new RealmIpBanCache(TimeSpan.FromSeconds(60), _clock);
        _bans.AddIpRow("10.9.8.7", Now - 10, Now - 10);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal((byte)AuthResult.Success, await ChallengeResultAsync(endpoint: "127.0.0.1:5555", cache: cache));
        }

        Assert.Equal((byte)AuthResult.FailNoAccess, await ChallengeResultAsync(endpoint: "10.9.8.7:5555", cache: cache));
        Assert.Equal(1, _bans.ListIpBansCalls);
        Assert.Equal(0, _bans.GetActiveIpBanCalls);
    }

    [Fact]
    public async Task ABanWrittenAfterTheLoad_ReachesTheLogonScreenOnceThePeriodHasPassed()
    {
        var cache = new RealmIpBanCache(TimeSpan.FromSeconds(60), _clock);
        Assert.Equal((byte)AuthResult.Success, await ChallengeResultAsync(endpoint: "127.0.0.1:5555", cache: cache));

        _bans.AddIpRow("127.0.0.1", Now, Now); // another process bans the address
        Assert.Equal((byte)AuthResult.Success, await ChallengeResultAsync(endpoint: "127.0.0.1:5555", cache: cache));

        _clock.Advance(60);
        Assert.Equal((byte)AuthResult.FailNoAccess, await ChallengeResultAsync(endpoint: "127.0.0.1:5555", cache: cache));
        Assert.Equal(2, _bans.ListIpBansCalls);
    }

    [Fact]
    public async Task ATemporaryBanInTheList_EndsOnTime_WithoutAReload()
    {
        var cache = new RealmIpBanCache(TimeSpan.FromSeconds(600), _clock);
        _bans.AddIpRow("127.0.0.1", Now - 10, Now + 30);
        Assert.Equal((byte)AuthResult.FailNoAccess, await ChallengeResultAsync(endpoint: "127.0.0.1:5555", cache: cache));

        _clock.Advance(31);
        Assert.Equal((byte)AuthResult.Success, await ChallengeResultAsync(endpoint: "127.0.0.1:5555", cache: cache));
        Assert.Equal(1, _bans.ListIpBansCalls);
    }

    [Fact]
    public async Task APeriodOfZero_ReadsTheRowOnEveryChallenge_AsVmangosRealmdDoes()
    {
        var cache = new RealmIpBanCache(TimeSpan.Zero, _clock);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal((byte)AuthResult.Success, await ChallengeResultAsync(endpoint: "127.0.0.1:5555", cache: cache));
        }

        Assert.Equal(3, _bans.GetActiveIpBanCalls);
        Assert.Equal(0, _bans.ListIpBansCalls);
    }

    [Fact]
    public async Task AFailedReload_ClosesTheConnection_AndTheNextChallengeTriesAgain()
    {
        var cache = new RealmIpBanCache(TimeSpan.FromSeconds(60), _clock);
        _bans.FailWith = new InvalidOperationException("database down");
        (int read, _) = await ExchangeAsync(true, "127.0.0.1:5555", true, AccountStatus.Active, sendProof: false, cache);
        Assert.Equal(0, read); // fail closed, as the direct read

        _bans.FailWith = null;
        _bans.AddIpRow("127.0.0.1", Now - 10, Now - 10);
        Assert.Equal((byte)AuthResult.FailNoAccess, await ChallengeResultAsync(endpoint: "127.0.0.1:5555", cache: cache));
        Assert.Equal(1, cache.Reloads);
    }

    [Fact]
    public async Task ANonCanonicalStoredSpelling_StillMatchesTheSessionAddress()
    {
        var cache = new RealmIpBanCache(TimeSpan.FromSeconds(60), _clock);
        _bans.AddIpRow(" ::ffff:127.0.0.1 ", Now - 10, Now - 10);
        Assert.Equal((byte)AuthResult.FailNoAccess, await ChallengeResultAsync(endpoint: "127.0.0.1:5555", cache: cache));
    }

    // --- harness -------------------------------------------------------------------

    private async Task<byte> ChallengeResultAsync(
        bool createAccount = true, string endpoint = "test", bool useBans = true, AccountStatus status = AccountStatus.Active,
        RealmIpBanCache? cache = null)
    {
        (int read, byte[] reply) = await ExchangeAsync(createAccount, endpoint, useBans, status, sendProof: false, cache);
        Assert.True(read >= 3);
        return reply[2];
    }

    private async Task<(byte Challenge, byte Proof)> ChallengeThenProofAsync(string endpoint)
    {
        (_, byte[] reply) = await ExchangeAsync(false, endpoint, true, AccountStatus.Active, sendProof: true);
        return (reply[2], reply[3]);
    }

    private async Task<(int Read, byte First)> RawChallengeAsync()
    {
        (int read, byte[] reply) = await ExchangeAsync(true, "test", true, AccountStatus.Active, sendProof: false);
        return (read, read > 0 ? reply[0] : (byte)0);
    }

    private async Task<(int Read, byte[] Reply)> ExchangeAsync(
        bool createAccount, string endpoint, bool useBans, AccountStatus status, bool sendProof, RealmIpBanCache? cache = null)
    {
        var accounts = new InMemoryAccountStore();
        if (createAccount)
        {
            byte[] salt = WowSrp6.GenerateSalt();
            BigInteger verifier = WowSrp6.ComputeVerifier(salt, "BANNEE", "SECRET");
            await accounts.CreateAsync(new Account
            {
                Id = AccountId,
                Username = "BANNEE",
                Salt = salt,
                Verifier = WowSrp6.ToFixedLittleEndian(verifier, WowSrp6.KeyLength),
                Status = status,
            });
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task server = Task.Run(async () =>
        {
            using TcpClient accepted = await listener.AcceptTcpClientAsync();
            listener.Stop();
            await using NetworkStream stream = accepted.GetStream();
            var session = new LogonSession(
                stream, accounts, new InMemoryRealmStore([]), new AuthOptions { AutocreateAccounts = false },
                NullLogger.Instance, endpoint, useBans ? _bans : null, ipBanCache: cache);
            try
            {
                await session.RunAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // a store outage escapes the session (the host closes the connection): fail closed
            }
            catch (IOException)
            {
                // the test client hangs up with the rest of a long reply unread
            }
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream net = client.GetStream();
        await net.WriteAsync(BuildChallenge("BANNEE"));
        byte[] reply = new byte[4];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int read = await net.ReadAtLeastAsync(reply.AsMemory(0, 3), 1, throwOnEndOfStream: false, cts.Token);
        if (sendProof && read >= 3)
        {
            byte[] proof = new byte[1 + LogonProofRequest.BodyLength];
            proof[0] = (byte)AuthCommand.LogonProof;
            await net.WriteAsync(proof);
            byte[] proofReply = new byte[4];
            int got = await net.ReadAtLeastAsync(proofReply, 4, throwOnEndOfStream: false, cts.Token);
            reply[3] = got >= 2 ? proofReply[1] : (byte)0xFF;
        }

        client.Dispose();
        await server.WaitAsync(TimeSpan.FromSeconds(10));
        return (read, reply);
    }

    private static byte[] BuildChallenge(string username)
    {
        byte[] name = Encoding.ASCII.GetBytes(username);
        var body = new List<byte>();
        body.AddRange("WoW\0"u8.ToArray());
        body.AddRange([1, 12, 1]);
        body.AddRange(BitConverter.GetBytes(ClientBuild.Vanilla1121));
        body.AddRange("68x\0"u8.ToArray());
        body.AddRange("niW\0"u8.ToArray());
        body.AddRange("SUne"u8.ToArray());
        body.AddRange(new byte[8]);
        body.Add((byte)name.Length);
        body.AddRange(name);

        var packet = new List<byte> { (byte)AuthCommand.LogonChallenge, 0x08 };
        packet.AddRange(BitConverter.GetBytes((ushort)body.Count));
        packet.AddRange(body);
        return [.. packet];
    }

    private sealed class ManualClock(long unixSeconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

        public void Advance(long seconds) => unixSeconds += seconds;
    }
}
