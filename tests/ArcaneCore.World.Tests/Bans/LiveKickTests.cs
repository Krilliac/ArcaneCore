using System.Buffers.Binary;
using System.Net.Sockets;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Bans;
using ArcaneCore.World.Tests.Security;
using Xunit;

namespace ArcaneCore.World.Tests.Bans;

/// <summary>
/// A ban disconnects already-connected sessions at once (vmangos World::BanAccount, World.cpp:2469-2486:
/// LogoutPlayer(true) + KickPlayer, except the banning author). The kick goes through the session's normal
/// close path, so the character's last state is saved. The events are in-process; a ban written by another
/// process is the periodic re-check's job.
/// </summary>
public sealed class LiveKickTests
{
    private const float MarkerX = 123.5f;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task BanAccount_DisconnectsTheInWorldPlayer_SavesItsState_AndRefusesTheNextLogin()
    {
        await using var host = WorldTestHost.Start();
        WorldTestClient client = await host.EnterWorldAsync("VICTIM", "Victim");
        int id = (await host.Accounts.FindByUsernameAsync("VICTIM"))!.Id;
        await host.PlaceAsync("Victim", MarkerX, 10f, 20f);

        await host.Bans.BanAccountAsync(new BanRequest(id, 0, "cheating", "GM"));

        Assert.True(await client.IsClosedByServerAsync(), "the banned client must be disconnected");
        await WorldTestHost.WaitForAsync(() => host.Registry.Find(id) is null, "the session to unregister");
        await WorldTestHost.WaitForAsync(() => host.World.OnlinePlayerCount == 0, "the player to leave the world");
        await WorldTestHost.WaitForAsync(() => host.Characters.SaveCount > 0, "the final save");
        CharacterRecord saved = (await host.Characters.GetByIdAsync(1))!;
        Assert.Equal(MarkerX, saved.X); // LogoutPlayer(true): the state at the moment of the ban was saved

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(System.Net.IPAddress.Loopback, host.Port);
        await using NetworkStream stream = tcp.GetStream();
        byte[] key = (await host.Accounts.FindByUsernameAsync("VICTIM"))!.SessionKey!;
        Assert.Equal((byte)AuthResponseCode.Banned, await CodexNetAuthWorldTests.SendAuthSessionAsync(stream, "VICTIM", key));
        await client.DisposeAsync();
    }

    [Theory]
    [InlineData(AccountStatus.Banned)]
    [InlineData(AccountStatus.Suspended)]
    public async Task StatusColumnChange_DisconnectsTheSession(AccountStatus status)
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("COLUMNVIC", "Colvic");

        Assert.True(await host.Accounts.SetStatusAsync("COLUMNVIC", status));

        Assert.True(await client.IsClosedByServerAsync());
        await WorldTestHost.WaitForAsync(() => host.World.OnlinePlayerCount == 0, "the player to leave the world");
    }

    [Fact]
    public async Task SessionAtTheCharacterScreen_IsKickedToo()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("IDLER");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("IDLER", key);
        int id = (await host.Accounts.FindByUsernameAsync("IDLER"))!.Id;

        await host.Bans.BanAccountAsync(new BanRequest(id, 600, "temp", "GM"));

        Assert.True(await client.IsClosedByServerAsync());
        await WorldTestHost.WaitForAsync(() => host.Registry.Find(id) is null, "the session to unregister");
    }

    [Fact]
    public async Task BanningOneAccount_LeavesABystanderConnected()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient victim = await host.EnterWorldAsync("TARGET", "Target");
        await using WorldTestClient bystander = await host.EnterWorldAsync("BYSTANDER", "Bystand");
        int id = (await host.Accounts.FindByUsernameAsync("TARGET"))!.Id;

        await host.Bans.BanAccountAsync(new BanRequest(id, 0, "x", "GM"));

        Assert.True(await victim.IsClosedByServerAsync());
        await PingAsync(bystander); // still served
        await WorldTestHost.WaitForAsync(() => host.World.OnlinePlayerCount == 1, "only the victim to leave the world");
        Assert.NotNull(host.Registry.Find((await host.Accounts.FindByUsernameAsync("BYSTANDER"))!.Id));
    }

    [Fact]
    public async Task TheBanningAuthor_IsNotKickedByTheirOwnBan()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("THEGM", "Thegm", AccountSecurity.Administrator);
        int id = (await host.Accounts.FindByUsernameAsync("THEGM"))!.Id;

        // World.cpp:2552-2553: the author's own account is skipped.
        await host.Bans.BanAccountAsync(new BanRequest(id, 0, "self", "Thegm", AuthorAccountId: id));

        await PingAsync(gm);
        Assert.NotNull(host.Registry.Find(id));
    }

    [Fact]
    public async Task IpBan_KicksSessionsFromThatAddress_ButNotFromAnother()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("LOOPBACKER", "Loopy");
        int id = (await host.Accounts.FindByUsernameAsync("LOOPBACKER"))!.Id;

        await host.Bans.BanIpAsync(new IpBanRequest("10.1.2.3", 0, "other address", "GM"));
        await PingAsync(client); // an unrelated address: untouched
        Assert.NotNull(host.Registry.Find(id));

        await host.Bans.BanIpAsync(new IpBanRequest("127.0.0.1", 0, "this address", "GM"));
        Assert.True(await client.IsClosedByServerAsync());
        await WorldTestHost.WaitForAsync(() => host.Registry.Find(id) is null, "the session to unregister");
    }

    [Fact]
    public async Task RevokeSessionKeyOnBan_NullsTheStoredKey_ByDefaultTheKeyIsKept()
    {
        await using (var keep = WorldTestHost.Start())
        {
            await using WorldTestClient client = await keep.EnterWorldAsync("KEEPKEY", "Keepy");
            int id = (await keep.Accounts.FindByUsernameAsync("KEEPKEY"))!.Id;
            await keep.Bans.BanAccountAsync(new BanRequest(id, 0, "x", "GM"));
            Assert.True(await client.IsClosedByServerAsync());
            Assert.NotNull((await keep.Accounts.FindByUsernameAsync("KEEPKEY"))!.SessionKey);
        }

        await using var revoke = WorldTestHost.Start(banOptions: new BanOptions { RevokeSessionKeyOnBan = true });
        await using WorldTestClient victim = await revoke.EnterWorldAsync("LOSEKEY", "Losey");
        int victimId = (await revoke.Accounts.FindByUsernameAsync("LOSEKEY"))!.Id;
        byte[] oldKey = (await revoke.Accounts.FindByUsernameAsync("LOSEKEY"))!.SessionKey!;

        await revoke.Bans.BanAccountAsync(new BanRequest(victimId, 0, "x", "GM"));
        Assert.True(await victim.IsClosedByServerAsync());
        await WorldTestHost.WaitForAsync(
            () => revoke.Accounts.FindByUsernameAsync("LOSEKEY").Result!.SessionKey is null, "the key to be revoked");

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(System.Net.IPAddress.Loopback, revoke.Port);
        await using NetworkStream stream = tcp.GetStream();
        Assert.Equal((byte)AuthResponseCode.UnknownAccount, await CodexNetAuthWorldTests.SendAuthSessionAsync(stream, "LOSEKEY", oldKey));
    }

    [Fact]
    public async Task ActiveTransitions_KickNobody()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("STAYER", "Stayer");
        int id = (await host.Accounts.FindByUsernameAsync("STAYER"))!.Id;

        host.StatusEvents.Publish(new AccountStatusChange(id, AccountStatus.Active));
        Assert.False(await host.Bans.UnbanAccountAsync(id, "GM", "nothing to lift"));

        await PingAsync(client);
        Assert.NotNull(host.Registry.Find(id));
    }

    [Fact]
    public async Task BanDuringLogin_EndsWithTheSessionClosedAndThePlayerGone()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("LOGGER");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("LOGGER", key);
        await client.CreateCharacterAsync("Logger");
        int id = (await host.Accounts.FindByUsernameAsync("LOGGER"))!.Id;

        // Fire the login and the ban back to back: whichever the session sees first, it must end closed
        // with nobody left in the world (Close posts RemovePlayer after any pending login command).
        await client.SendAsync(WorldOpcode.CmsgPlayerLogin, BitConverter.GetBytes(1UL));
        await host.Bans.BanAccountAsync(new BanRequest(id, 0, "x", "GM"));

        Assert.True(await client.IsClosedByServerAsync());
        await WorldTestHost.WaitForAsync(() => host.Registry.Find(id) is null, "the session to unregister");
        await WorldTestHost.WaitForAsync(() => host.World.OnlinePlayerCount == 0, "no player left in the world");
    }

    [Fact]
    public async Task ASubscriberFault_NeverFailsTheBan()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("FAULTY", "Faulty");
        int id = (await host.Accounts.FindByUsernameAsync("FAULTY"))!.Id;
        host.StatusEvents.StatusChanged += _ => throw new InvalidOperationException("another subscriber is broken");

        await host.Bans.BanAccountAsync(new BanRequest(id, 0, "x", "GM")); // must not throw

        Assert.True(await client.IsClosedByServerAsync());
    }

    private static async Task PingAsync(WorldTestClient client)
    {
        await client.SendAsync(WorldOpcode.CmsgPing, [9, 0, 0, 0, 0, 0, 0, 0]);
        byte[] pong = await client.ReadUntilAsync(WorldOpcode.SmsgPong);
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(pong));
    }
}
