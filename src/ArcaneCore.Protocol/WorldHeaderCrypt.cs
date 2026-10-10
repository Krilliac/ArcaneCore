using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol.Versioning;

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
public sealed class WorldHeaderCrypt : IWorldHeaderCrypt
{
    public const int OutgoingHeaderLength = 4; // SMSG: CRYPTED_SEND_LEN
    public const int IncomingHeaderLength = 6; // CMSG: CRYPTED_RECV_LEN

    /// <summary>The SRP6 session key K the cipher is keyed with: 40 bytes (two interleaved SHA-1 digests), the length vmangos stores in account.sessionkey.</summary>
    public const int SessionKeyLength = 40;

    private byte[] _key = [0];
    private byte _sendIndex;
    private byte _sendPrevious;
    private byte _recvIndex;
    private byte _recvPrevious;
    private bool _initialized;

    public bool IsInitialized => _initialized;

    int IWorldHeaderCrypt.OutgoingHeaderLength => OutgoingHeaderLength;

    int IWorldHeaderCrypt.IncomingHeaderLength => IncomingHeaderLength;

    /// <summary>Set the cipher key (the 40-byte SRP6 session key) and engage encryption.</summary>
    public void Initialize(byte[] sessionKey)
    {
        ArgumentNullException.ThrowIfNull(sessionKey);
        if (sessionKey.Length == 0)
        {
            throw new ArgumentException("session key must not be empty", nameof(sessionKey));
        }

        // Keyed once per connection, right after CMSG_AUTH_SESSION is accepted (vmangos WorldSocket::HandleAuthSession
        // calls m_Crypt.Init once); a second call resets both rolling states and desynchronises the peer. The key
        // is the 40-byte K; another length means the caller took it from the wrong field (the cipher still runs:
        // the algorithm is defined for any key length, as vmangos AuthCrypt::Init is).
        Invariant.Check(!_initialized, "the world header cipher is initialised once per connection");
        Invariant.Check(sessionKey.Length == SessionKeyLength, $"the world header cipher is keyed with the {SessionKeyLength}-byte session key, got {sessionKey.Length} bytes");

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

        // The rolling state advances one byte per header byte on both peers; a span that is not a
        // whole 6-byte CMSG header (server) or 4-byte SMSG header (client) desynchronises every later header.
        Invariant.Assert(header.Length is IncomingHeaderLength or OutgoingHeaderLength, $"a header is {IncomingHeaderLength} or {OutgoingHeaderLength} bytes, got {header.Length}");
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

        Invariant.Assert(header.Length is IncomingHeaderLength or OutgoingHeaderLength, $"a header is {IncomingHeaderLength} or {OutgoingHeaderLength} bytes, got {header.Length}");
        for (int t = 0; t < header.Length; t++)
        {
            _sendIndex = (byte)(_sendIndex % _key.Length);
            byte x = (byte)((header[t] ^ _key[_sendIndex]) + _sendPrevious);
            _sendIndex++;
            header[t] = _sendPrevious = x;
        }
    }
}
