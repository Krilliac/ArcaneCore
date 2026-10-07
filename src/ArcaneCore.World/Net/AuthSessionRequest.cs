using ArcaneCore.Protocol;

namespace ArcaneCore.World.Net;

/// <summary>
/// The parsed CMSG_AUTH_SESSION body (vmangos WorldSocket::HandleAuthSession, build 5875 layout):
/// u32 build, u32 server id, CString account, u32 client seed, u8[20] digest, then the addon block
/// to the end of the packet. Parsed without throwing and without copying: the digest and the addon
/// block are windows over the payload the session owns.
/// </summary>
public readonly record struct AuthSessionRequest(
    uint Build,
    uint ServerId,
    string Account,
    uint ClientSeed,
    ReadOnlyMemory<byte> ClientDigest,
    ReadOnlyMemory<byte> AddonBlock)
{
    /// <summary>The digest is a SHA-1.</summary>
    public const int DigestLength = 20;

    /// <summary>
    /// Longest account name accepted, in bytes: the auth schema stores at most 16 characters
    /// (AuthDbContext) and realmd drops a challenge whose name is longer (AuthSocket.cpp:248-262), so
    /// no longer name can hold a session key; anything longer is malformed, before any lookup.
    /// </summary>
    public const int MaxAccountNameBytes = 16;

    /// <summary>Parse the body; false (nothing allocated) when any field is missing or the account name is too long.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> payload, out AuthSessionRequest request)
    {
        request = default;
        var reader = new PacketReader(payload.Span);
        if (!reader.TryReadUInt32(out uint build)
            || !reader.TryReadUInt32(out uint serverId)
            || !reader.TryReadCStringBytes(MaxAccountNameBytes, out ReadOnlySpan<byte> accountBytes)
            || !reader.TryReadUInt32(out uint clientSeed))
        {
            return false;
        }

        int digestStart = reader.Position;
        if (!reader.TrySkip(DigestLength))
        {
            return false;
        }

        // The account name is the only allocation: the rest are slices of the payload.
        string account = accountBytes.Length == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(accountBytes).ToUpperInvariant();
        request = new AuthSessionRequest(
            build,
            serverId,
            account,
            clientSeed,
            payload.Slice(digestStart, DigestLength),
            payload.Slice(reader.Position));
        return true;
    }
}
