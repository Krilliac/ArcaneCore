using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// A simulated 1.12.1 world client over a loopback socket: performs the auth handshake and
/// then sends/receives encrypted world packets, so character-lifecycle flows can be driven
/// against a real <see cref="WorldSession"/>.
/// </summary>
internal sealed class WorldTestClient : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly WorldHeaderCrypt _crypt = new();

    private WorldTestClient(TcpClient client, NetworkStream stream)
    {
        _client = client;
        _stream = stream;
    }

    public static async Task<WorldTestClient> StartAsync(
        IAccountStore accounts, ICharacterStore characters, IWorldDataStore worldData)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            using TcpClient server = await listener.AcceptTcpClientAsync();
            listener.Stop();
            await using NetworkStream stream = server.GetStream();
            var session = new WorldSession(stream, accounts, characters, worldData, NullLogger.Instance, "test");
            await session.RunAsync(CancellationToken.None);
        });

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return new WorldTestClient(client, client.GetStream());
    }

    /// <summary>Run the auth handshake and assert it succeeds; leaves the client ready for world traffic.</summary>
    public async Task AuthenticateAsync(string account, byte[] sessionKey)
    {
        (WorldOpcode op, byte[] payload) = await ReadAsync();
        Assert.Equal(WorldOpcode.SmsgAuthChallenge, op);
        uint serverSeed = BinaryPrimitives.ReadUInt32LittleEndian(payload);

        uint clientSeed = 0x1BADD00D;
        byte[] digest = Sha1.Hash(
            Encoding.ASCII.GetBytes(account.ToUpperInvariant()), new byte[4],
            Le(clientSeed), Le(serverSeed), sessionKey);

        var session = new PacketWriter(64);
        session.WriteUInt32(ClientBuild.Vanilla1121);
        session.WriteUInt32(0);
        session.WriteCString(account.ToUpperInvariant());
        session.WriteUInt32(clientSeed);
        session.WriteBytes(digest);
        session.WriteUInt32(0); // empty addon block (decompresses to nothing; server tolerates it)
        await SendAsync(WorldOpcode.CmsgAuthSession, session.AsMemory().ToArray());
        _crypt.Initialize(sessionKey);

        (op, payload) = await ReadAsync();
        Assert.Equal(WorldOpcode.SmsgAuthResponse, op);
        Assert.Equal((byte)AuthResponseCode.Ok, payload[0]);

        (op, _) = await ReadAsync(); // SMSG_ADDON_INFO
        Assert.Equal(WorldOpcode.SmsgAddonInfo, op);
    }

    public async Task SendAsync(WorldOpcode opcode, byte[] payload)
    {
        byte[] frame = new byte[WorldHeaderCrypt.IncomingHeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), (ushort)(payload.Length + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2, 4), (uint)opcode);
        _crypt.EncryptHeader(frame.AsSpan(0, WorldHeaderCrypt.IncomingHeaderLength));
        payload.CopyTo(frame, WorldHeaderCrypt.IncomingHeaderLength);
        await _stream.WriteAsync(frame);
    }

    public async Task<(WorldOpcode Opcode, byte[] Payload)> ReadAsync()
    {
        byte[] header = new byte[WorldHeaderCrypt.OutgoingHeaderLength];
        await _stream.ReadExactlyAsync(header);
        _crypt.DecryptHeader(header);

        ushort size = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
        var opcode = (WorldOpcode)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
        int payloadLength = size - 2;

        byte[] payload = payloadLength > 0 ? new byte[payloadLength] : [];
        if (payloadLength > 0)
        {
            await _stream.ReadExactlyAsync(payload);
        }

        return (opcode, payload);
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _client.Dispose();
    }

    private static byte[] Le(uint value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }
}
