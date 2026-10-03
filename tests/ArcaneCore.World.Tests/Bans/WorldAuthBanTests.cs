using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Tests.Security;
using Xunit;

namespace ArcaneCore.World.Tests.Bans;

/// <summary>
/// World authentication honours ban rows and IP bans, not only the status column. vmangos has one reply,
/// AUTH_BANNED, for either (WorldSocket.cpp:333-345), after the digest check. A store error closes the
/// connection (fail closed), and a ban committed between the first read and Register is caught by the
/// post-Register re-check.
/// </summary>
public sealed class WorldAuthBanTests
{
    private static readonly long RealNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [Theory]
    [InlineData(0L)]        // permanent: bandate == unbandate
    [InlineData(3600L)]     // temporary, still running
    public async Task ActiveBanRow_WithAnActiveStatusColumn_IsRefusedAndNeverRegistered(long duration)
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("ROWBAN");
        int id = (await host.Accounts.FindByUsernameAsync("ROWBAN"))!.Id;
        host.Bans.AddAccountRow(id, RealNow - 5, RealNow - 5 + duration);

        using TcpClient tcp = await ConnectAsync(host);
        await using NetworkStream stream = tcp.GetStream();
        byte code = await CodexNetAuthWorldTests.SendAuthSessionAsync(stream, "ROWBAN", key);

        Assert.Equal((byte)AuthResponseCode.Banned, code);
        Assert.Null(host.Registry.Find(id));
        Assert.True(await CodexNetAuthWorldTests.DrainUntilClosedAsync(stream, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ExpiredAndInactiveRows_AdmitTheAccount()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("FREED");
        int id = (await host.Accounts.FindByUsernameAsync("FREED"))!.Id;
        host.Bans.AddAccountRow(id, RealNow - 7200, RealNow - 3600);
        host.Bans.AddAccountRow(id, RealNow - 5, RealNow - 5, active: false);

        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("FREED", key); // asserts AUTH_OK
        Assert.NotNull(host.Registry.Find(id));
    }

    [Fact]
    public async Task IpBan_RefusesAnActiveAccount_FromThatAddress_ButNotAnUnrelatedBan()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("IPBANNED");
        host.Bans.AddIpRow("10.20.30.40", RealNow - 5, RealNow - 5); // somebody else's address

        await using (WorldTestClient ok = await host.ConnectAsync())
        {
            await ok.AuthenticateAsync("IPBANNED", key); // asserts AUTH_OK: an unrelated IP ban does not block
        }

        host.Bans.AddIpRow("127.0.0.1", RealNow - 5, RealNow - 5);
        using TcpClient tcp = await ConnectAsync(host);
        await using NetworkStream stream = tcp.GetStream();
        Assert.Equal((byte)AuthResponseCode.Banned, await CodexNetAuthWorldTests.SendAuthSessionAsync(stream, "IPBANNED", key));
        Assert.True(await CodexNetAuthWorldTests.DrainUntilClosedAsync(stream, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task StoreFailure_ClosesTheConnection_WithoutAuthOk_AndRegistersNothing()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("OUTAGE");
        host.ExpectSessionFaults = true; // the production host logs the fault and closes the connection
        host.Bans.FailWith = new InvalidOperationException("database down");

        using TcpClient tcp = await ConnectAsync(host);
        await using NetworkStream stream = tcp.GetStream();
        await Assert.ThrowsAnyAsync<Exception>(() => CodexNetAuthWorldTests.SendAuthSessionAsync(stream, "OUTAGE", key));
        Assert.Equal(0, host.Registry.Count);
        await WorldTestHost.WaitForAsync(() => host.SessionFaults.Count == 1, "the session fault");
        Assert.Equal("database down", host.SessionFaults[0].Message);
    }

    [Fact]
    public async Task BanCommittedBetweenTheStatusReadAndRegister_IsCaughtByThePostRegisterRecheck()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("RACER");
        int id = (await host.Accounts.FindByUsernameAsync("RACER"))!.Id;

        // The first lookup answers "not banned"; the ban lands right after it, before Register runs.
        host.Bans.AfterFirstAccountQuery = () => host.Bans.AddAccountRow(id, RealNow - 1, RealNow - 1);

        using TcpClient tcp = await ConnectAsync(host);
        await using NetworkStream stream = tcp.GetStream();
        byte code = await CodexNetAuthWorldTests.SendAuthSessionAsync(stream, "RACER", key);

        Assert.Equal((byte)AuthResponseCode.Banned, code);
        await WorldTestHost.WaitForAsync(() => host.Registry.Find(id) is null, "the raced session to be unregistered");
        Assert.True(await CodexNetAuthWorldTests.DrainUntilClosedAsync(stream, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task KickedWhileRegisteredButNotYetAuthenticated_NeverGetsAuthOk()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("KICKED");
        int id = (await host.Accounts.FindByUsernameAsync("KICKED"))!.Id;

        // The second lookup is the post-Register re-check: a live-ban event (Kick) lands there, after Register,
        // before the cipher is initialised. No ban row exists, so only the kick can stop the session.
        host.Bans.AfterAccountQuery = ordinal =>
        {
            if (ordinal == 2)
            {
                host.Registry.Find(id)!.Kick();
            }
        };

        using TcpClient tcp = await ConnectAsync(host);
        await using NetworkStream stream = tcp.GetStream();
        // The helper throws XunitException when the reply is header-encrypted, i.e. when the server answered AUTH_OK.
        Exception closed = await Assert.ThrowsAnyAsync<Exception>(() => CodexNetAuthWorldTests.SendAuthSessionAsync(stream, "KICKED", key));
        Assert.IsNotType<Xunit.Sdk.XunitException>(closed);
        await WorldTestHost.WaitForAsync(() => host.Registry.Find(id) is null, "the kicked session to be unregistered");
    }

    [Fact]
    public async Task StatusColumnOverride_StillRefuses_WithoutAnyRow()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("COLUMN");
        await host.Accounts.SetStatusAsync("COLUMN", AccountStatus.Suspended);

        using TcpClient tcp = await ConnectAsync(host);
        await using NetworkStream stream = tcp.GetStream();
        Assert.Equal((byte)AuthResponseCode.Banned, await CodexNetAuthWorldTests.SendAuthSessionAsync(stream, "COLUMN", key));
    }

    private static async Task<TcpClient> ConnectAsync(WorldTestHost host)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, host.Port);
        return tcp;
    }
}
