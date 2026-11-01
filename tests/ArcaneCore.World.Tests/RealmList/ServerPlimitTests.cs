using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.RealmList;

/// <summary>
/// <c>.server plimit</c> (vmangos ServerCommands.cpp:332-393): the player limit and the realm lock level. The admission test, the
/// refusal at <c>CMSG_AUTH_SESSION</c> (WorldSocket.cpp:349-358; World.cpp:316), the write of <c>allowedSecurityLevel</c> through
/// <see cref="IRealmStatusStore"/> (World.cpp:2918-2920) and the reset. The realm list's own reading of the lock
/// (<c>RealmListProjection</c>) is tested in ArcaneCore.Realm.Tests, the EF write in ArcaneCore.Data.Tests.
/// </summary>
public sealed class ServerPlimitTests
{
    [Fact]
    public void CommandIsAdministratorOnly()
    {
        CommandTable table = ChatCommands.CreateTable();
        Assert.Null(table.Resolve("server plimit", AccountSecurity.GameMaster));
        Assert.NotNull(table.Resolve("server plimit", AccountSecurity.Administrator));
    }

    [Fact]
    public void Encoding_FollowsVmangos_AndClampsBelowTheLowestLock()
    {
        var admission = new WorldLoginAdmission(100, 1);
        Assert.Equal(100, admission.AmountLimit);
        Assert.Equal(AccountSecurity.Player, admission.SecurityLimit);

        admission.SetLimit(-2);
        Assert.Equal(0, admission.AmountLimit);
        Assert.Equal(AccountSecurity.GameMaster, admission.SecurityLimit);

        admission.SetLimit(-9);
        Assert.Equal(-3, admission.Limit);
        Assert.Equal(AccountSecurity.Administrator, admission.SecurityLimit);
    }

    [Fact]
    public void Limit1_RefusesASecondPlayer_ButNotStaffNorTheFirst()
    {
        var admission = new WorldLoginAdmission(1, 1);
        Assert.Null(admission.Check(AccountSecurity.Player, otherActiveSessions: 0));
        Assert.Equal(AuthResponseCode.Unavailable, admission.Check(AccountSecurity.Player, otherActiveSessions: 1));
        Assert.Null(admission.Check(AccountSecurity.Moderator, otherActiveSessions: 1));

        admission.SetLimit(0); // no limit
        Assert.Null(admission.Check(AccountSecurity.Player, otherActiveSessions: 500));
    }

    [Fact]
    public void Lock_RefusesAccountsBelowTheLevel()
    {
        var admission = new WorldLoginAdmission(100, 1);
        admission.SetLimit(-(int)AccountSecurity.GameMaster);
        Assert.Equal(AuthResponseCode.Unavailable, admission.Check(AccountSecurity.Player, 0));
        Assert.Equal(AuthResponseCode.Unavailable, admission.Check(AccountSecurity.Moderator, 0));
        Assert.Null(admission.Check(AccountSecurity.GameMaster, 0));
        Assert.Null(admission.Check(AccountSecurity.Administrator, 0));
    }

    [Fact]
    public async Task ASecondPlayerIsRefusedOverTheWire_AtLimit1()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: c => c.AddSingleton(new WorldLoginAdmission(1, 0)));
        await using WorldTestClient first = await host.EnterWorldAsync("PLIMA", "Plima");
        byte[] key = await host.AddAccountAsync("PLIMB");

        Assert.Equal(AuthResponseCode.Unavailable, await RawAuthAsync(host, "PLIMB", key));

        byte[] staffKey = await host.AddAccountAsync("PLIMS", AccountSecurity.Moderator);
        await using WorldTestClient staff = await host.ConnectAsync();
        await staff.AuthenticateAsync("PLIMS", staffKey);
    }

    [Fact]
    public async Task ManagedSessions_DoNotCountAgainstTheLimit_SoBotsNeverLockOutARealPlayer()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: c => c.AddSingleton(new WorldLoginAdmission(1, 0)));
        await using WorldTestClient first = await host.EnterWorldAsync("PLIMBA", "Plimba");
        WorldLoginAdmission admission = host.WorldServices.GetRequiredService<WorldLoginAdmission>();
        using IServiceScope scope = host.WorldServices.CreateScope();

        // Many managed (bot-like) sessions on top of the one real session: only the real one counts.
        var sessions = new List<WorldSession>(host.Registry.Sessions);
        for (int i = 0; i < 5; i++)
        {
            sessions.Add(WorldSession.CreateConsole(scope.ServiceProvider, host.Opcodes, host.World, host.Registry, new WorldSessionOptions(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
        }

        Assert.Equal(1, WorldLoginAdmission.CountOtherSessions(sessions, accountId: int.MaxValue));
        Assert.Equal(AuthResponseCode.Unavailable, admission.Check(AccountSecurity.Player, WorldLoginAdmission.CountOtherSessions(sessions, int.MaxValue)));

        // With only managed sessions online a real player gets in.
        sessions.RemoveAll(s => !s.IsManaged);
        Assert.Equal(0, WorldLoginAdmission.CountOtherSessions(sessions, int.MaxValue));
        Assert.Null(admission.Check(AccountSecurity.Player, WorldLoginAdmission.CountOtherSessions(sessions, int.MaxValue)));
    }

    [Fact]
    public async Task GamemasterLock_WritesTheRealmRow_RefusesPlayers_AndResetClearsBoth()
    {
        var row = new RealmEntry { Id = 1, Name = "Realm", Address = "127.0.0.1:8085", State = RealmRuntimeState.Online };
        var store = new RealmRowStore(row);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: c =>
        {
            c.AddSingleton<IRealmStatusStore>(store);
            c.AddSingleton(sp => new WorldLoginAdmission(100, 1, sp));
        });
        WorldLoginAdmission admission = host.WorldServices.GetRequiredService<WorldLoginAdmission>();
        await using WorldTestClient admin = await host.EnterWorldAsync("PLIMAD", "Plimad", AccountSecurity.Administrator);
        await admin.CollectAsync();

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".server plimit gamemaster");
        Assert.Equal("Player limits: amount 0, min. security level Gamemaster.", (await admin.ReadChatAsync()).Text);
        await admission.PublishLockAsync(); // the command's own write is queued ahead; both write the level in force
        Assert.Equal((byte)AccountSecurity.GameMaster, row.AllowedSecurityLevel);
        Assert.Equal(AccountSecurity.GameMaster, admission.SecurityLimit);

        byte[] playerKey = await host.AddAccountAsync("PLIMPL");
        Assert.Equal(AuthResponseCode.Unavailable, await RawAuthAsync(host, "PLIMPL", playerKey));
        byte[] gmKey = await host.AddAccountAsync("PLIMGM", AccountSecurity.GameMaster);
        await using WorldTestClient gm = await host.ConnectAsync();
        await gm.AuthenticateAsync("PLIMGM", gmKey);

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".server plimit reset");
        Assert.Equal("Player limits: amount 100, min. security level Player.", (await admin.ReadChatAsync()).Text);
        await admission.PublishLockAsync();
        Assert.Equal((byte)0, row.AllowedSecurityLevel);
        Assert.Equal(100, admission.AmountLimit);
        Assert.Equal(AccountSecurity.Player, admission.SecurityLimit);

        byte[] againKey = await host.AddAccountAsync("PLIMAG");
        await using WorldTestClient again = await host.ConnectAsync();
        await again.AuthenticateAsync("PLIMAG", againKey);
    }

    [Fact]
    public async Task Publish_SurvivesAFailedWrite_AndAMissingRow()
    {
        var row = new RealmEntry { Id = 1, Name = "Realm", Address = "127.0.0.1:8085" };
        var store = new RealmRowStore(row);
        store.FailNext["lock"] = 1;
        using ServiceProvider services = new ServiceCollection().AddSingleton<IRealmStatusStore>(store).BuildServiceProvider();
        var admission = new WorldLoginAdmission(100, 1, services);

        admission.SetLimit(-(int)AccountSecurity.Moderator); // starts one write
        await admission.PublishLockAsync();                  // three writes in all, one of them throws and is swallowed
        await admission.PublishLockAsync();
        Assert.Equal((byte)AccountSecurity.Moderator, row.AllowedSecurityLevel);

        var missing = new WorldLoginAdmission(100, 7, services);
        missing.SetLimit(-1);
        await missing.PublishLockAsync();
        Assert.Equal((byte)AccountSecurity.Moderator, row.AllowedSecurityLevel);
    }

    /// <summary>A bare CMSG_AUTH_SESSION with a valid digest; returns the result byte of the SMSG_AUTH_RESPONSE (a refusal is sent unencrypted).</summary>
    private static async Task<AuthResponseCode> RawAuthAsync(WorldTestHost host, string account, byte[] key)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, host.Port);
        await using NetworkStream client = tcp.GetStream();

        byte[] seedPacket = await ReadAsync(client);
        uint serverSeed = BinaryPrimitives.ReadUInt32LittleEndian(seedPacket.AsSpan(2));
        uint clientSeed = 0xCAFE;
        byte[] digest = Sha1.Hash(Encoding.ASCII.GetBytes(account), new byte[4], Le(clientSeed), Le(serverSeed), key);

        var w = new PacketWriter(64);
        w.WriteUInt32(ClientBuild.Vanilla1121);
        w.WriteUInt32(0);
        w.WriteCString(account);
        w.WriteUInt32(clientSeed);
        w.WriteBytes(digest);
        w.WriteUInt32(0); // empty addon block
        byte[] payload = w.AsMemory().ToArray();
        byte[] frame = new byte[WorldHeaderCrypt.IncomingHeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), (ushort)(payload.Length + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2, 4), (uint)WorldOpcode.CmsgAuthSession);
        payload.CopyTo(frame, WorldHeaderCrypt.IncomingHeaderLength);
        await client.WriteAsync(frame);

        byte[] reply = await ReadAsync(client);
        Assert.Equal(WorldOpcode.SmsgAuthResponse, (WorldOpcode)BinaryPrimitives.ReadUInt16LittleEndian(reply));
        return (AuthResponseCode)reply[2];
    }

    private static async Task<byte[]> ReadAsync(NetworkStream s)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        byte[] header = new byte[4];
        await s.ReadExactlyAsync(header, cts.Token);
        int len = BinaryPrimitives.ReadUInt16BigEndian(header) - 2;
        byte[] body = new byte[2 + len];
        header.AsSpan(2, 2).CopyTo(body);
        await s.ReadExactlyAsync(body.AsMemory(2), cts.Token);
        return body;
    }

    private static byte[] Le(uint v)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }
}
