using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ArcaneCore.Game;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>Connection-level behaviour: framing limits, pings, unknown opcodes, duplicate logins.</summary>
public sealed class SessionTests
{
    [Fact]
    public async Task PacketBeforeAuthentication_Disconnects()
    {
        await using var host = WorldTestHost.Start();
        await using WorldTestClient client = await host.ConnectAsync();
        Assert.Equal(WorldOpcode.SmsgAuthChallenge, (await client.ReadAsync()).Opcode);

        await client.SendAsync(WorldOpcode.CmsgCharEnum, []);

        Assert.True(await client.IsClosedByServerAsync());
    }

    [Fact]
    public async Task OversizedHeader_Disconnects()
    {
        await using var host = WorldTestHost.Start();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, host.Port);
        NetworkStream stream = tcp.GetStream();

        // Plaintext header (encryption not engaged yet) claiming 0x2801 bytes: above vmangos' 0x2800 cap.
        byte[] header = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(header, 0x2801);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2), (uint)WorldOpcode.CmsgAuthSession);
        await stream.WriteAsync(header);

        byte[] buffer = new byte[256];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, timeout.Token);
            if (read == 0)
            {
                break; // closed after the challenge
            }

            total += read;
        }

        Assert.Equal(8, total); // only SMSG_AUTH_CHALLENGE (4-byte header + 4-byte seed) arrived
    }

    [Fact]
    public async Task Ping_IsAnsweredWithTheSameSequence_AndUnknownOpcodesAreIgnored()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("PINGER");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("PINGER", key);

        // An opcode without a handler is logged and ignored, not fatal.
        await client.SendAsync(WorldOpcode.CmsgSetActiveMover, new byte[8]);

        var ping = new PacketWriter(8);
        ping.WriteUInt32(0xCAFE);
        ping.WriteUInt32(25); // latency
        await client.SendAsync(WorldOpcode.CmsgPing, ping.ToArray());

        (WorldOpcode op, byte[] payload) = await client.ReadAsync();
        Assert.Equal(WorldOpcode.SmsgPong, op);
        Assert.Equal(0xCAFEu, BinaryPrimitives.ReadUInt32LittleEndian(payload));
    }

    [Fact]
    public async Task SecondSessionForAccount_KicksTheFirst_AndCanEnterWithTheSameCharacter()
    {
        await using var host = WorldTestHost.Start();
        WorldTestClient first = await host.EnterWorldAsync("TWICE", "Twice");
        Assert.True(host.World.IsOnline(ObjectGuid.Player(1)));

        byte[] key = (await host.Accounts.FindByUsernameAsync("TWICE"))!.SessionKey!;
        await using WorldTestClient second = await host.ConnectAsync();
        await second.AuthenticateAsync("TWICE", key);

        // The replaced session is closed by the server and its player leaves the world (saved).
        Assert.True(await first.IsClosedByServerAsync());
        await first.DisposeAsync();
        await WorldTestHost.WaitForAsync(() => !host.World.IsOnline(ObjectGuid.Player(1)), "old player removed");

        byte[] self = await second.LoginAsync(1);
        CharacterLifecycleTests.AssertSelfCreateBlock(self, guid: 1);
    }
}
