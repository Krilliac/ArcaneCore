namespace ArcaneCore.Protocol.Versioning;

/// <summary>
/// The world packet-header cipher of one client build (multi-version design §3.2): 1.12 keys a rolling xor/add cipher
/// with the raw session key, 2.4.3 the same cipher with an HMAC-derived key, 3.3.5 ARC4-drop1024. Outbound and inbound
/// header lengths are part of the build's framing.
/// </summary>
public interface IWorldHeaderCrypt
{
    /// <summary>Bytes of an outgoing (SMSG) header.</summary>
    int OutgoingHeaderLength { get; }

    /// <summary>Bytes of an incoming (CMSG) header.</summary>
    int IncomingHeaderLength { get; }

    bool IsInitialized { get; }

    /// <summary>Key the cipher with the SRP6 session key and engage it. Before this, headers travel in plaintext.</summary>
    void Initialize(byte[] sessionKey);

    void EncryptHeader(Span<byte> header);

    void DecryptHeader(Span<byte> header);
}
