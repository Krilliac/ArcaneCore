namespace ArcaneCore.Protocol;

/// <summary>
/// Vanilla 1.12.1 packet-header cipher — the rolling byte cipher seeded directly from the
/// SRP6 session key (Charter §3). **NOT ARC4**, and not the HMAC-derived key TBC+ uses:
/// vanilla feeds the raw 40-byte session key as the cipher key.
///
/// Only the packet *header* is enciphered: 4 bytes outgoing (SMSG: size + opcode), 6 bytes
/// incoming (CMSG: size + opcode). Encryption engages only after CMSG_AUTH_SESSION — before
/// <see cref="Initialize"/> the operations are no-ops, so SMSG_AUTH_CHALLENGE and the
/// inbound CMSG_AUTH_SESSION travel with plaintext headers.
///
/// Algorithm verified byte-for-byte against vmangos src/shared/Auth/AuthCrypt.cpp.
/// </summary>
public sealed class WorldHeaderCrypt
{
    public const int OutgoingHeaderLength = 4; // SMSG: CRYPTED_SEND_LEN
    public const int IncomingHeaderLength = 6; // CMSG: CRYPTED_RECV_LEN

    private byte[] _key = [0];
    private byte _sendIndex;
    private byte _sendPrevious;
    private byte _recvIndex;
    private byte _recvPrevious;
    private bool _initialized;

    public bool IsInitialized => _initialized;

    /// <summary>Set the cipher key (the 40-byte SRP6 session key) and engage encryption.</summary>
    public void Initialize(byte[] sessionKey)
    {
        ArgumentNullException.ThrowIfNull(sessionKey);
        if (sessionKey.Length == 0)
        {
            throw new ArgumentException("session key must not be empty", nameof(sessionKey));
        }

        _key = sessionKey;
        _sendIndex = _sendPrevious = _recvIndex = _recvPrevious = 0;
        _initialized = true;
    }

    /// <summary>
    /// Decrypt a header in place using the "recv" rolling state. The whole span is
    /// processed (the caller passes an exactly-sized header — 6 bytes for the server's
    /// inbound CMSG, 4 bytes for a client decrypting an SMSG).
    /// </summary>
    public void DecryptHeader(Span<byte> header)
    {
        if (!_initialized)
        {
            return;
        }

        for (int t = 0; t < header.Length; t++)
        {
            _recvIndex = (byte)(_recvIndex % _key.Length);
            byte x = (byte)((header[t] - _recvPrevious) ^ _key[_recvIndex]);
            _recvIndex++;
            _recvPrevious = header[t];
            header[t] = x;
        }
    }

    /// <summary>
    /// Encrypt a header in place using the "send" rolling state. The whole span is
    /// processed (4 bytes for the server's outbound SMSG, 6 bytes for a client encrypting
    /// a CMSG).
    /// </summary>
    public void EncryptHeader(Span<byte> header)
    {
        if (!_initialized)
        {
            return;
        }

        for (int t = 0; t < header.Length; t++)
        {
            _sendIndex = (byte)(_sendIndex % _key.Length);
            byte x = (byte)((header[t] ^ _key[_sendIndex]) + _sendPrevious);
            _sendIndex++;
            header[t] = _sendPrevious = x;
        }
    }
}
