using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Net;

/// <summary>
/// Handles one world connection: the M2 handshake and the M3 character lifecycle
/// (enumerate / create / delete) plus world entry (player login → object update so the
/// character stands in the world). Verified against vmangos WorldSocket.cpp and
/// CharacterHandler.cpp.
/// </summary>
public sealed class WorldSession(
    NetworkStream stream,
    IAccountStore accountStore,
    ICharacterStore characterStore,
    IWorldDataStore worldDataStore,
    ILogger logger,
    string remoteEndpoint)
{
    private readonly WorldHeaderCrypt _crypt = new();
    private uint _serverSeed;
    private bool _authenticated;
    private int _accountId;
    private string _username = string.Empty;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _serverSeed = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));

        var challenge = new PacketWriter(4);
        challenge.WriteUInt32(_serverSeed);
        await SendAsync(WorldOpcode.SmsgAuthChallenge, challenge.AsMemory(), cancellationToken).ConfigureAwait(false);

        byte[] header = new byte[WorldHeaderCrypt.IncomingHeaderLength];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await stream.ReadExactlyAsync(header.AsMemory(1), cancellationToken).ConfigureAwait(false);
            _crypt.DecryptHeader(header);

            ushort size = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
            var opcode = (WorldOpcode)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2, 4));
            int payloadLength = size - 4;

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

            case WorldOpcode.CmsgCharCreate when _authenticated:
                await HandleCharCreateAsync(payload, cancellationToken).ConfigureAwait(false);
                return true;

            case WorldOpcode.CmsgCharDelete when _authenticated:
                await HandleCharDeleteAsync(payload, cancellationToken).ConfigureAwait(false);
                return true;

            case WorldOpcode.CmsgPlayerLogin when _authenticated:
                await HandlePlayerLoginAsync(payload, cancellationToken).ConfigureAwait(false);
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
        _ = reader.ReadUInt32();
        string account = reader.ReadCString().ToUpperInvariant();
        uint clientSeed = reader.ReadUInt32();
        byte[] clientDigest = reader.ReadBytes(20).ToArray();
        byte[] addonBlock = reader.ReadToEnd().ToArray();

        if (build != ClientBuild.Vanilla1121)
        {
            await SendAuthResponseAsync(AuthResponseCode.VersionMismatch, cancellationToken).ConfigureAwait(false);
            return false;
        }

        Account? stored = await accountStore.FindByUsernameAsync(account, cancellationToken).ConfigureAwait(false);
        if (stored?.SessionKey is null)
        {
            await SendAuthResponseAsync(AuthResponseCode.UnknownAccount, cancellationToken).ConfigureAwait(false);
            return false;
        }

        byte[] expected = ComputeAuthDigest(account, clientSeed, _serverSeed, stored.SessionKey);
        if (!CryptographicOperations.FixedTimeEquals(expected, clientDigest))
        {
            await SendAuthResponseAsync(AuthResponseCode.Failed, cancellationToken).ConfigureAwait(false);
            return false;
        }

        _crypt.Initialize(stored.SessionKey);
        _authenticated = true;
        _accountId = stored.Id;
        _username = account;
        logger.LogInformation("[{Endpoint}] '{Account}' entered the world handshake", remoteEndpoint, account);

        await SendAuthResponseAsync(AuthResponseCode.Ok, cancellationToken).ConfigureAwait(false);
        await SendAsync(WorldOpcode.SmsgAddonInfo, AddonInfo.BuildResponse(addonBlock), cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private async Task HandlePingAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var reader = new PacketReader(payload);
        uint ping = reader.ReadUInt32();

        var pong = new PacketWriter(4);
        pong.WriteUInt32(ping);
        await SendAsync(WorldOpcode.SmsgPong, pong.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleCharEnumAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<CharacterRecord> characters =
            await characterStore.GetByAccountAsync(_accountId, cancellationToken).ConfigureAwait(false);
        await SendAsync(WorldOpcode.SmsgCharEnum, CharacterPackets.BuildCharEnum(characters), cancellationToken)
            .ConfigureAwait(false);
        logger.LogInformation("[{Endpoint}] sent {Count} character(s) to '{Account}'",
            remoteEndpoint, characters.Count, _username);
    }

    private async Task HandleCharCreateAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var reader = new PacketReader(payload);
        string name = reader.ReadCString().Trim();
        byte race = reader.ReadByte();
        byte cls = reader.ReadByte();
        byte gender = reader.ReadByte();
        byte skin = reader.ReadByte();
        byte face = reader.ReadByte();
        byte hairStyle = reader.ReadByte();
        byte hairColor = reader.ReadByte();
        byte facialHair = reader.ReadByte();

        if (name.Length is < 1 or > 12)
        {
            await SendCharResultAsync(WorldOpcode.SmsgCharCreate, CharResult.CharNameNoName, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!await worldDataStore.IsValidRaceClassAsync(race, cls, cancellationToken).ConfigureAwait(false))
        {
            await SendCharResultAsync(WorldOpcode.SmsgCharCreate, CharResult.CharCreateFailed, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (await characterStore.IsNameTakenAsync(name, cancellationToken).ConfigureAwait(false))
        {
            await SendCharResultAsync(WorldOpcode.SmsgCharCreate, CharResult.CharCreateNameInUse, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        StartPosition? start = await worldDataStore.GetStartPositionAsync(race, cls, cancellationToken)
            .ConfigureAwait(false);
        if (start is null)
        {
            await SendCharResultAsync(WorldOpcode.SmsgCharCreate, CharResult.CharCreateError, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await characterStore.CreateAsync(new CharacterRecord
        {
            AccountId = _accountId,
            Name = name,
            Race = race,
            Class = cls,
            Gender = gender,
            Skin = skin,
            Face = face,
            HairStyle = hairStyle,
            HairColor = hairColor,
            FacialHair = facialHair,
            MapId = start.MapId,
            ZoneId = start.ZoneId,
            X = start.X,
            Y = start.Y,
            Z = start.Z,
            Orientation = start.Orientation,
        }, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("[{Endpoint}] '{Account}' created character '{Name}'", remoteEndpoint, _username, name);
        await SendCharResultAsync(WorldOpcode.SmsgCharCreate, CharResult.CharCreateSuccess, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task HandleCharDeleteAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var reader = new PacketReader(payload);
        var guid = (int)reader.ReadUInt64();

        bool deleted = await characterStore.DeleteAsync(guid, _accountId, cancellationToken).ConfigureAwait(false);
        CharResult result = deleted ? CharResult.CharDeleteSuccess : CharResult.CharDeleteFailed;
        await SendCharResultAsync(WorldOpcode.SmsgCharDelete, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandlePlayerLoginAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var reader = new PacketReader(payload);
        var guid = (int)reader.ReadUInt64();

        CharacterRecord? character = await characterStore.GetByIdAsync(guid, cancellationToken).ConfigureAwait(false);
        if (character is null || character.AccountId != _accountId)
        {
            logger.LogWarning("[{Endpoint}] login for character {Guid} not owned by '{Account}'",
                remoteEndpoint, guid, _username);
            return;
        }

        RaceInfo? raceInfo = await worldDataStore.GetRaceInfoAsync(character.Race, character.Gender, cancellationToken)
            .ConfigureAwait(false);
        ClassInfo? classInfo = await worldDataStore.GetClassInfoAsync(character.Class, cancellationToken)
            .ConfigureAwait(false);
        if (raceInfo is null || classInfo is null)
        {
            logger.LogError("[{Endpoint}] missing world data for character {Guid}", remoteEndpoint, guid);
            return;
        }

        await SendAsync(WorldOpcode.SmsgLoginVerifyWorld, CharacterPackets.BuildLoginVerifyWorld(character), cancellationToken).ConfigureAwait(false);
        await SendAsync(WorldOpcode.SmsgTutorialFlags, CharacterPackets.BuildTutorialFlags(), cancellationToken).ConfigureAwait(false);
        await SendAsync(WorldOpcode.SmsgLoginSetTimeSpeed, CharacterPackets.BuildTimeSpeed(DateTime.UtcNow), cancellationToken).ConfigureAwait(false);
        await SendAsync(WorldOpcode.SmsgInitialSpells, CharacterPackets.BuildInitialSpells(), cancellationToken).ConfigureAwait(false);

        Game.PlayerObject player = CharacterPackets.BuildPlayerObject(character, raceInfo, classInfo);
        byte[] update = Game.ObjectUpdateBuilder.BuildSelfCreate(player, (uint)Environment.TickCount);
        await SendAsync(WorldOpcode.SmsgUpdateObject, update, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("[{Endpoint}] '{Account}' entered the world as '{Name}'",
            remoteEndpoint, _username, character.Name);
    }

    // --- digest + framing --------------------------------------------------------

    private static byte[] ComputeAuthDigest(string account, uint clientSeed, uint serverSeed, byte[] sessionKey)
        => Sha1.Hash(Encoding.ASCII.GetBytes(account), new byte[4], ToLittleEndian(clientSeed), ToLittleEndian(serverSeed), sessionKey);

    private static byte[] ToLittleEndian(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private Task SendAuthResponseAsync(AuthResponseCode code, CancellationToken cancellationToken)
        => SendAsync(WorldOpcode.SmsgAuthResponse, new[] { (byte)code }, cancellationToken);

    private Task SendCharResultAsync(WorldOpcode opcode, CharResult result, CancellationToken cancellationToken)
        => SendAsync(opcode, new[] { (byte)result }, cancellationToken);

    private async Task SendAsync(WorldOpcode opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        byte[] frame = new byte[WorldHeaderCrypt.OutgoingHeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0, 2), (ushort)(payload.Length + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), (ushort)opcode);
        _crypt.EncryptHeader(frame.AsSpan(0, WorldHeaderCrypt.OutgoingHeaderLength));
        payload.Span.CopyTo(frame.AsSpan(WorldHeaderCrypt.OutgoingHeaderLength));
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }
}
