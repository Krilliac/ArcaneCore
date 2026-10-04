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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Realm.Tests.Security;

/// <summary>
/// The logon daemon's transport protections end to end over loopback (docs/ops/netguard.md): the
/// per-address failure budget, the unauthenticated lifetime, the frame read deadline and the
/// listener's connection rate. Every limit closes the connection and nothing escapes the session.
/// </summary>
public sealed class LogonProtectionTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private static NetGuard Guard(NetProtectionOptions options, NetGuardTests.CapturingLogger? log = null)
        => new(options, () => 0, () => 0, log ?? new NetGuardTests.CapturingLogger());

    /// <summary>A real session over loopback, told its peer is 127.0.0.1 so the address table has a key.</summary>
    private static NetworkStream StartSession(IAccountStore accounts, AuthOptions options, NetGuard guard, string endpoint = "127.0.0.1:50000")
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
                options, NullLogger.Instance, endpoint, banStore: null, guard);
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

    [Fact]
    public async Task FailureBudgetSpent_RefusesTheNextChallenge_WithFailNoAccess_AndCloses()
    {
        var log = new NetGuardTests.CapturingLogger();
        NetGuard guard = Guard(new NetProtectionOptions { AuthFailureBurstPerIp = 2, AuthFailuresPerMinutePerIp = 1 }, log);
        await using NetworkStream c = StartSession(new InMemoryAccountStore(), new AuthOptions(), guard);

        // Two unknown accounts: answered, each charged to 127.0.0.1.
        for (int i = 0; i < 2; i++)
        {
            await c.WriteAsync(CodexNetAuthRealmTests.Challenge("NOBODY"));
            (byte result, _, _) = await CodexNetAuthRealmTests.ReadChallenge(c);
            Assert.Equal((byte)AuthResult.UnknownAccount, result);
        }

        // The third is refused before any lookup: FAIL_NOACCESS, then the connection is closed.
        await c.WriteAsync(CodexNetAuthRealmTests.Challenge("NOBODY"));
        (byte refused, _, _) = await CodexNetAuthRealmTests.ReadChallenge(c);
        Assert.Equal((byte)AuthResult.FailNoAccess, refused);
        Assert.True(await CodexNetAuthRealmTests.ReadsNothingThenClosesAsync(c));
        Assert.Equal(1, guard.RefusedAuthAttempts);
        Assert.Contains(log.Messages, m => m.Contains("too many failed attempts", StringComparison.Ordinal));

        // A second connection from the same address is refused the same way, and still only one line was logged.
        await using NetworkStream again = StartSession(new InMemoryAccountStore(), new AuthOptions(), guard);
        await again.WriteAsync(CodexNetAuthRealmTests.Challenge("NOBODY"));
        (byte refusedAgain, _, _) = await CodexNetAuthRealmTests.ReadChallenge(again);
        Assert.Equal((byte)AuthResult.FailNoAccess, refusedAgain);
        Assert.Equal(2, guard.RefusedAuthAttempts);
        Assert.Single(log.Messages, m => m.Contains("too many failed attempts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuccessfulLogin_DoesNotConsumeTheFailureBudget()
    {
        const string user = "GOODUSER";
        const string password = "PASS";
        byte[] salt = WowSrp6.GenerateSalt();
        BigInteger verifier = WowSrp6.ComputeVerifier(salt, user, password);
        var accounts = new InMemoryAccountStore();
        await accounts.CreateAsync(new Account { Username = user, Salt = salt, Verifier = WowSrp6.ToFixedLittleEndian(verifier, WowSrp6.KeyLength) });

        NetGuard guard = Guard(new NetProtectionOptions { AuthFailureBurstPerIp = 1, AuthFailuresPerMinutePerIp = 1 });
        IpKey key = IpKey.From(IPAddress.Loopback);
        await using NetworkStream c = StartSession(accounts, new AuthOptions(), guard);

        await c.WriteAsync(CodexNetAuthRealmTests.Challenge(user));
        (byte result, byte[] b, byte[] saltSent) = await CodexNetAuthRealmTests.ReadChallenge(c);
        Assert.Equal((byte)AuthResult.Success, result);
        await c.WriteAsync(CodexNetAuthRealmTests.ProofFor(user, user, password, saltSent, b));
        byte[] proofReply = await CodexNetAuthRealmTests.ReadN(c, 2);
        Assert.Equal((byte)AuthResult.Success, proofReply[1]);

        Assert.Equal(1.0, guard.Table.TokensOf(key, RateBucket.AuthFailures));
        Assert.Equal(0, guard.RefusedAuthAttempts);

        // The same address may still fail once (budget untouched by the success) and is then refused.
        await using NetworkStream other = StartSession(accounts, new AuthOptions(), guard);
        await other.WriteAsync(CodexNetAuthRealmTests.Challenge("NOBODY"));
        (byte unknown, _, _) = await CodexNetAuthRealmTests.ReadChallenge(other);
        Assert.Equal((byte)AuthResult.UnknownAccount, unknown);
        await other.WriteAsync(CodexNetAuthRealmTests.Challenge("NOBODY"));
        (byte refused, _, _) = await CodexNetAuthRealmTests.ReadChallenge(other);
        Assert.Equal((byte)AuthResult.FailNoAccess, refused);
    }

    [Fact]
    public async Task UnauthenticatedLifetime_ClosesAConnectionThatNeverProves()
    {
        var log = new NetGuardTests.CapturingLogger();
        NetGuard guard = Guard(new NetProtectionOptions { LogonUnauthenticatedLifetime = TimeSpan.FromMilliseconds(300) }, log);
        await using NetworkStream c = StartSession(new InMemoryAccountStore(), new AuthOptions(), guard);

        Assert.True(await CodexNetAuthRealmTests.ReadsNothingThenClosesAsync(c), "an idle unauthenticated connection must be closed");
        Assert.Contains(log.Messages, m => m.Contains("not authenticated within", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FrameReadTimeout_ClosesAChallengeWhoseBodyNeverArrives()
    {
        var log = new NetGuardTests.CapturingLogger();
        NetGuard guard = Guard(new NetProtectionOptions
        {
            FrameReadTimeout = TimeSpan.FromMilliseconds(300), LogonUnauthenticatedLifetime = TimeSpan.Zero,
        }, log);
        await using NetworkStream c = StartSession(new InMemoryAccountStore(), new AuthOptions { ReadTimeoutSeconds = 0 }, guard);

        // command 0x00, protocol 0x08, size 40 little-endian, then nothing.
        await c.WriteAsync(new byte[] { 0x00, 0x08, 40, 0 });
        Assert.True(await CodexNetAuthRealmTests.ReadsNothingThenClosesAsync(c), "a withheld body must not hold the connection");
        Assert.Contains(log.Messages, m => m.Contains("frame not completed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthReadTimeout_WhenSet_TakesPrecedenceOverTheFrameTimeout()
    {
        NetGuard guard = Guard(new NetProtectionOptions { FrameReadTimeout = TimeSpan.FromHours(1), LogonUnauthenticatedLifetime = TimeSpan.Zero });
        await using NetworkStream c = StartSession(new InMemoryAccountStore(), new AuthOptions { ReadTimeoutSeconds = 1 }, guard);
        await c.WriteAsync(new byte[] { 0x00, 0x08, 40, 0 });
        Assert.True(await CodexNetAuthRealmTests.ReadsNothingThenClosesAsync(c));
    }

    [Fact]
    public async Task Listener_RefusesConnectionsBeyondThePerAddressRate_AndCountsThem()
    {
        int port = FreePort();
        var services = new ServiceCollection();
        services.AddSingleton<IAccountStore>(new InMemoryAccountStore());
        services.AddSingleton<IRealmStore>(new InMemoryRealmStore([]));
        await using ServiceProvider provider = services.BuildServiceProvider();
        var server = new LogonServer(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AuthOptions { BindAddress = "127.0.0.1", Port = port }),
            NullLoggerFactory.Instance, NullLogger<LogonServer>.Instance,
            Options.Create(new NetProtectionOptions { ConnectionBurstPerIp = 2, ConnectionsPerMinutePerIp = 1, MaxConnectionsPerIp = 0 }));

        await server.StartAsync(CancellationToken.None);
        try
        {
            using var first = new TcpClient();
            using var second = new TcpClient();
            await first.ConnectAsync(IPAddress.Loopback, port);
            await second.ConnectAsync(IPAddress.Loopback, port);
            await first.GetStream().WriteAsync(CodexNetAuthRealmTests.Challenge("NOBODY"));
            Assert.True(await CodexNetAuthRealmTests.ReceivesDataAsync(first.GetStream()), "the first connection is served");

            using var third = new TcpClient();
            await third.ConnectAsync(IPAddress.Loopback, port);
            await third.GetStream().WriteAsync(CodexNetAuthRealmTests.Challenge("NOBODY"));
            Assert.True(await CodexNetAuthRealmTests.ReadsNothingThenClosesAsync(third.GetStream()), "the third connection in the burst window must be refused");

            NetGuard guard = Assert.IsType<NetGuard>(server.Guard);
            Assert.Equal(1, guard.RefusedConnections);
            Assert.Equal(new NetProtectionOptions().AuthFailureBurstPerIp, guard.Options.AuthFailureBurstPerIp); // untouched defaults still in force
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Listener_WithoutARegisteredSection_RunsWithTheDefaults()
    {
        int port = FreePort();
        var services = new ServiceCollection();
        services.AddSingleton<IAccountStore>(new InMemoryAccountStore());
        services.AddSingleton<IRealmStore>(new InMemoryRealmStore([]));
        await using ServiceProvider provider = services.BuildServiceProvider();
        var server = new LogonServer(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AuthOptions { BindAddress = "127.0.0.1", Port = port }),
            NullLoggerFactory.Instance, NullLogger<LogonServer>.Instance);
        await server.StartAsync(CancellationToken.None);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);
            await client.GetStream().WriteAsync(CodexNetAuthRealmTests.Challenge("NOBODY"));
            Assert.True(await CodexNetAuthRealmTests.ReceivesDataAsync(client.GetStream()));
            NetGuard guard = Assert.IsType<NetGuard>(server.Guard);
            var defaults = new NetProtectionOptions();
            Assert.Equal(defaults.MaxConnectionsPerIp, guard.Options.MaxConnectionsPerIp);
            Assert.Equal(defaults.AuthFailureBurstPerIp, guard.Options.AuthFailureBurstPerIp);
            Assert.True(guard.Table.IsEnabled(RateBucket.AuthFailures));
            Assert.True(guard.Table.IsEnabled(RateBucket.Connections));
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
