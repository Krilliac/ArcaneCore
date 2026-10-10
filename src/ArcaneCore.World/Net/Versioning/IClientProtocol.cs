using ArcaneCore.Protocol;
using ArcaneCore.Protocol.Versioning;

namespace ArcaneCore.World.Net.Versioning;

/// <summary>
/// Everything about the wire that depends on the client build (multi-version design §3.2): world auth, opcode map,
/// header cipher and update-field layout. A world session binds one instance when CMSG_AUTH_SESSION names the build
/// and never switches. Until then it frames with <see cref="ClientProtocols.PreAuth"/> (plaintext headers).
/// </summary>
public interface IClientProtocol
{
    /// <summary>The client build this protocol speaks.</summary>
    ushort Build { get; }

    IWorldAuthCodec Auth { get; }

    IOpcodeMap Opcodes { get; }

    IUpdateFieldLayout UpdateFields { get; }

    /// <summary>A fresh, uninitialised header cipher for one connection.</summary>
    IWorldHeaderCrypt CreateHeaderCrypt();
}

/// <summary>The build-specific CMSG_AUTH_SESSION and SMSG_AUTH_RESPONSE formats.</summary>
public interface IWorldAuthCodec
{
    bool TryParseAuthSession(ReadOnlyMemory<byte> payload, out AuthSessionRequest request);

    /// <summary>The SMSG_AUTH_RESPONSE payload for <paramref name="code"/>.</summary>
    byte[] AuthResponse(AuthResponseCode code);
}
