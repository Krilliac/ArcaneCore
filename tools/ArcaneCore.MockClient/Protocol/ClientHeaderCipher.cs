using System.Security.Cryptography;

namespace ArcaneCore.MockClient.Protocol;

/// <summary>Independent rolling header cipher; send and receive each retain their own state.</summary>
internal sealed class ClientHeaderCipher
{
    private readonly byte[] _key;
    private int _sendPosition;
    private int _receivePosition;
    private byte _sent;
    private byte _received;

    internal ClientHeaderCipher(byte[] sessionKey)
    {
        if (sessionKey.Length != 40)
        {
            throw new ArgumentException("A vanilla world session key must contain exactly 40 bytes.", nameof(sessionKey));
        }

        _key = (byte[])sessionKey.Clone();
    }

    internal void Encrypt(Span<byte> header)
    {
        for (int index = 0; index < header.Length; index++)
        {
            byte encrypted = unchecked((byte)((header[index] ^ _key[_sendPosition]) + _sent));
            header[index] = encrypted;
            _sent = encrypted;
            _sendPosition = (_sendPosition + 1) % _key.Length;
        }
    }

    internal void Decrypt(Span<byte> header)
    {
        for (int index = 0; index < header.Length; index++)
        {
            byte encrypted = header[index];
            header[index] = (byte)(unchecked((byte)(encrypted - _received)) ^ _key[_receivePosition]);
            _received = encrypted;
            _receivePosition = (_receivePosition + 1) % _key.Length;
        }
    }

    internal void Clear() => CryptographicOperations.ZeroMemory(_key);
}
