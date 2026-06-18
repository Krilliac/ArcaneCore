using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Net;

/// <summary>
/// Handles one world connection: the M2 handshake (SMSG_AUTH_CHALLENGE → CMSG_AUTH_SESSION,
/// session-key validation, header encryption engages) and just enough post-auth traffic to
/// reach the (empty) character-select screen — char enum, ping, addon info.
///
/// Verified against vmangos src/game/Server/WorldSocket.cpp.
/// </summary>
public sealed class WorldSession(
    NetworkStream stream,
    IAccountStore accountStore,
    ILogger logger,
    string remoteEndpoint)
{
    private readonly WorldHeaderCrypt _crypt = new();
    private uint _serverSeed;
    private bool _authenticated;
    private string _username = string.Empty;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _serverSeed = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));

        // SMSG_AUTH_CHALLENGE: a single uint32 server seed, sent with a plaintext header.
        var challenge = new PacketWriter(4);
        challenge.WriteUInt32(_serverSeed);
        await SendAsync(WorldOpcode.SmsgAuthChallenge, challenge.AsMemory(), cancellationToken)
            .ConfigureAwait(false);

        byte[] header = new byte[WorldHeaderCrypt.IncomingHeaderLength];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break; // client disconnected
            }

            await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
            _crypt.DecryptHeader(header);

            ushort size = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
            var opcode = (WorldOpcode)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2, 4));
            int payloadLength = size - 4; // size counts the 4 opcode bytes plus the payload

            byte[] payload = payloadLength > 0 ? new byte[payloadLength] : [];
            if (payloadLength > 0)
            {
                await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            if (!await DispatchAsync(opcode, payload, cancellationToken).ConfigureAwait(false))
            {
                break;
            }
        }
    }

    private async Task<bool> DispatchAsync(WorldOpcode opcode, byte[] payload, CancellationToken cancellationToken)
    {
        switch (opcode)
        {
            case WorldOpcode.CmsgAuthSession when !_authenticated:
                return await HandleAuthSessionAsync(payload, cancellationToken).ConfigureAwait(false);

            case WorldOpcode.CmsgPing:
                await HandlePingAsync(payload, cancellationToken).ConfigureAwait(false);
                return true;

            case WorldOpcode.CmsgCharEnum when _authenticated:
                await HandleCharEnumAsync(cancellationToken).ConfigureAwait(false);
                return true;

            default:
                logger.LogDebug("[{Endpoint}] ignoring opcode 0x{Opcode:X3}", remoteEndpoint, (ushort)opcode);
                return true;
        }
    }

    private async Task<bool> HandleAuthSessionAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var reader = new PacketReader(payload);
        uint build = reader.ReadUInt32();
        _ = reader.ReadUInt32(); // server id (unused)
        string account = reader.ReadCString().ToUpperInvariant();
        uint clientSeed = reader.ReadUInt32();
        byte[] clientDigest = reader.ReadBytes(20).ToArray();
        byte[] addonBlock = reader.ReadToEnd().ToArray();

        if (build != ClientBuild.Vanilla1121)
        {
            logger.LogInformation("[{Endpoint}] rejected build {Build}", remoteEndpoint, build);
            await SendAuthResponseAsync(AuthResponseCode.VersionMismatch, cancellationToken).ConfigureAwait(false);
            return false;
        }

        Account? stored = await accountStore.FindByUsernameAsync(account, cancellationToken).ConfigureAwait(false);
        if (stored?.SessionKey is null)
        {
            logger.LogInformation("[{Endpoint}] no active session for '{Account}'", remoteEndpoint, account);
            await SendAuthResponseAsync(AuthResponseCode.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return false;
        }

        byte[] expected = ComputeAuthDigest(account, clientSeed, _serverSeed, stored.SessionKey);
        if (!CryptographicOperations.FixedTimeEquals(expected, clientDigest))
        {
            logger.LogInformation("[{Endpoint}] auth digest mismatch for '{Account}'", remoteEndpoint, account);
            await SendAuthResponseAsync(AuthResponseCode.Failed, cancellationToken).ConfigureAwait(false);
            return false;
        }

        // Engage header encryption (seeded with the raw session key) before any further reply.
        _crypt.Initialize(stored.SessionKey);
        _authenticated = true;
        _username = account;
        logger.LogInformation("[{Endpoint}] '{Account}' entered the world handshake", remoteEndpoint, account);

        await SendAuthResponseAsync(AuthResponseCode.Ok, cancellationToken).ConfigureAwait(false);

        byte[] addonResponse = AddonInfo.BuildResponse(addonBlock);
        await SendAsync(WorldOpcode.SmsgAddonInfo, addonResponse, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task HandlePingAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var reader = new PacketReader(payload);
        uint ping = reader.ReadUInt32(); // followed by latency (uint32), unused here

        var pong = new PacketWriter(4);
        pong.WriteUInt32(ping);
        await SendAsync(WorldOpcode.SmsgPong, pong.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleCharEnumAsync(CancellationToken cancellationToken)
    {
        // Empty character list: a single zero count (characters arrive in M3).
        var enumeration = new PacketWriter(1);
        enumeration.WriteByte(0);
        await SendAsync(WorldOpcode.SmsgCharEnum, enumeration.AsMemory(), cancellationToken).ConfigureAwait(false);
        logger.LogInformation("[{Endpoint}] sent empty character list to '{Account}'", remoteEndpoint, _username);
    }

    /// <summary>
    /// digest = SHA1( account || uint32(0) || clientSeed || serverSeed || sessionKey )
    /// (vmangos WorldSocket::_HandleAuthSession).
    /// </summary>
    private static byte[] ComputeAuthDigest(string account, uint clientSeed, uint serverSeed, byte[] sessionKey)
    {
        return Sha1.Hash(
            Encoding.ASCII.GetBytes(account),
            new byte[4],
            ToLittleEndian(clientSeed),
            ToLittleEndian(serverSeed),
            sessionKey);
    }

    private static byte[] ToLittleEndian(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private Task SendAuthResponseAsync(AuthResponseCode code, CancellationToken cancellationToken)
    {
        // Vanilla SMSG_AUTH_RESPONSE is a single result byte (no billing fields — those are TBC+).
        return SendAsync(WorldOpcode.SmsgAuthResponse, new[] { (byte)code }, cancellationToken);
    }

    private async Task SendAsync(WorldOpcode opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        // SMSG header: size(2, big-endian, counts opcode+payload) + opcode(2, little-endian).
        byte[] frame = new byte[WorldHeaderCrypt.OutgoingHeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), (ushort)(payload.Length + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), (ushort)opcode);
        _crypt.EncryptHeader(frame.AsSpan(0, WorldHeaderCrypt.OutgoingHeaderLength));
        payload.Span.CopyTo(frame.AsSpan(WorldHeaderCrypt.OutgoingHeaderLength));

        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }
}
