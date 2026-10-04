using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Characters;

/// <summary>CMSG_CHAR_RENAME: the character and the raw bytes of the name it asks for (validated as UTF-8 by the rename rules).</summary>
public readonly record struct CharacterRenameRequest(ulong Guid, byte[] RawName);

/// <summary>
/// Character rename packets, after the mangos reference (WorldSession::HandleCharRenameOpcode and its callback,
/// CharacterHandlerCustomize.cpp:86-190). Opcodes CMSG_CHAR_RENAME (711) and SMSG_CHAR_RENAME (712) are in
/// <c>WorldOpcode</c>; the 1.12.1 client's own handling of the packet is UNVERIFIED (docs/areas/character-rename.md).
/// </summary>
public static class CharacterRenamePackets
{
    /// <summary>The character-enum flag that tells the client the character must be renamed (mangos Player.cpp:179, CHARACTER_FLAG_RENAME).</summary>
    public const uint CharacterFlagRename = 0x00004000;

    /// <summary>SMSG_CHAR_RENAME result of a rename that happened: mangos RESPONSE_SUCCESS (SharedDefines.h:2283), the first of the ResponseCodes.</summary>
    public const byte ResponseSuccess = 0x00;

    /// <summary>
    /// CMSG_CHAR_RENAME: u64 character guid, CString new name. Null when the payload is shorter than a guid and a terminator
    /// (the reference would read garbage; the handler answers CHAR_NAME_NO_NAME instead).
    /// </summary>
    public static CharacterRenameRequest? ReadRequest(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < sizeof(ulong))
        {
            return null;
        }

        var reader = new PacketReader(payload);
        ulong guid = reader.ReadUInt64();
        return new CharacterRenameRequest(guid, reader.ReadCStringBytes().ToArray());
    }

    /// <summary>SMSG_INVALIDATE_PLAYER (796): the guid whose cached name data the clients must drop (mangos World::InvalidatePlayerDataToAllClient, World.cpp:2700).</summary>
    public static byte[] BuildInvalidatePlayer(ulong guid)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(guid);
        return writer.ToArray();
    }

    /// <summary>SMSG_CHAR_RENAME for a refusal: just the result byte (a CHAR_NAME_* or CHAR_CREATE_ERROR code).</summary>
    public static byte[] BuildFailure(CharResult result) => [(byte)result];

    /// <summary>SMSG_CHAR_RENAME for a rename that happened: <see cref="ResponseSuccess"/>, the character guid, the new name (CString).</summary>
    public static byte[] BuildSuccess(ulong guid, string newName)
    {
        ArgumentException.ThrowIfNullOrEmpty(newName);
        var writer = new PacketWriter(1 + 8 + newName.Length + 1);
        writer.WriteByte(ResponseSuccess);
        writer.WriteUInt64(guid);
        writer.WriteCString(newName);
        return writer.ToArray();
    }
}
