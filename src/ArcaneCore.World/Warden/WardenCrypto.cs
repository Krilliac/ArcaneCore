using System.Security.Cryptography;

namespace ArcaneCore.World.Warden;

/// <summary>
/// One RC4 stream (MaNGOS Zero WardenCryptoContext::Rc4State, src/game/Warden/WardenCryptoContext.cpp): the PRGA indices are part of the
/// stream and carry across packets, so each direction must transform every body exactly once and in order.
/// </summary>
internal sealed class WardenRc4
{
    private readonly byte[] _s = new byte[256];
    private byte _i;
    private byte _j;

    public WardenRc4(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("an RC4 key cannot be empty", nameof(key));
        }

        for (int i = 0; i < 256; i++)
        {
            _s[i] = (byte)i;
        }

        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + _s[i] + key[i % key.Length]) & 0xFF;
            (_s[i], _s[j]) = (_s[j], _s[i]);
        }
    }

    public void Transform(Span<byte> data)
    {
        for (int k = 0; k < data.Length; k++)
        {
            _i++;
            _j += _s[_i];
            (_s[_i], _s[_j]) = (_s[_j], _s[_i]);
            data[k] ^= _s[(byte)(_s[_i] + _s[_j])];
        }
    }

    public void Clear() => Array.Clear(_s);
}

/// <summary>
/// The two Warden RC4 directions of one session (MaNGOS Zero WardenCryptoContext). The first keys come from the 40-byte login session
/// key with the classic client generator (<see cref="DeriveInitialKeys"/>); after a correct hash response both directions are replaced
/// together by the delivered module's keys (<see cref="InstallModuleKeys"/>).
/// </summary>
internal sealed class WardenCryptoContext
{
    private WardenRc4 _clientToServer;
    private WardenRc4 _serverToClient;

    public WardenCryptoContext(ReadOnlySpan<byte> sessionKey)
    {
        DeriveInitialKeys(sessionKey, out byte[] client, out byte[] server);
        _clientToServer = new WardenRc4(client);
        _serverToClient = new WardenRc4(server);
        CryptographicOperations.ZeroMemory(client);
        CryptographicOperations.ZeroMemory(server);
    }

    public void DecryptFromClient(Span<byte> body) => _clientToServer.Transform(body);

    public void EncryptToClient(Span<byte> body) => _serverToClient.Transform(body);

    public void InstallModuleKeys(ReadOnlySpan<byte> clientKey, ReadOnlySpan<byte> serverKey)
    {
        var client = new WardenRc4(clientKey);
        var server = new WardenRc4(serverKey);
        _clientToServer.Clear();
        _serverToClient.Clear();
        _clientToServer = client;
        _serverToClient = server;
    }

    /// <summary>
    /// The classic client's Warden key generator (MaNGOS Zero DeriveInitialKeys; vmangos WardenAnticheat/WardenKeyGenerator.h): SHA-1 each
    /// 20-byte half of K, then expand SHA1(left || previous || right) to 32 bytes. The first 16 decrypt client replies, the last 16
    /// encrypt server requests.
    /// </summary>
    public static void DeriveInitialKeys(ReadOnlySpan<byte> sessionKey, out byte[] clientKey, out byte[] serverKey)
    {
        if (sessionKey.Length != 40)
        {
            throw new ArgumentException("the Warden key generator takes the 40-byte session key", nameof(sessionKey));
        }

        byte[] left = SHA1.HashData(sessionKey[..20]);
        byte[] right = SHA1.HashData(sessionKey[20..]);
        byte[] current = new byte[20];
        byte[] input = new byte[60];
        byte[] generated = new byte[32];
        int offset = 0;
        while (offset < generated.Length)
        {
            left.CopyTo(input, 0);
            current.CopyTo(input, 20);
            right.CopyTo(input, 40);
            current = SHA1.HashData(input);
            int count = Math.Min(current.Length, generated.Length - offset);
            current.AsSpan(0, count).CopyTo(generated.AsSpan(offset));
            offset += count;
        }

        clientKey = generated[..16];
        serverKey = generated[16..];
        CryptographicOperations.ZeroMemory(left);
        CryptographicOperations.ZeroMemory(right);
        CryptographicOperations.ZeroMemory(current);
        CryptographicOperations.ZeroMemory(input);
        CryptographicOperations.ZeroMemory(generated);
    }
}
