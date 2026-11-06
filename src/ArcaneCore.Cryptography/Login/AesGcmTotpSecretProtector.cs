using System.Security.Cryptography;
using ArcaneCore.Kernel.Login;

namespace ArcaneCore.Cryptography.Login;

public sealed class AesGcmTotpSecretProtector : ITotpSecretProtector, IDisposable
{
    private readonly Dictionary<byte, byte[]> _keys = new();
    public AesGcmTotpSecretProtector(IReadOnlyDictionary<byte, byte[]> keys)
    { foreach (var pair in keys) { if (pair.Value is null || pair.Value.Length != 32) throw new ArgumentException("TOTP keys must be 32 bytes."); _keys.Add(pair.Key, pair.Value.ToArray()); } }
    public byte[] Encrypt(byte keyId, byte[] secret)
    {
        if (keyId == 0 || !_keys.TryGetValue(keyId, out byte[]? key) || secret is null || secret.Length is < 1 or > 64) throw new ArgumentException("Invalid TOTP key or secret.");
        byte[] nonce = RandomNumberGenerator.GetBytes(12), cipher = new byte[secret.Length], tag = new byte[16];
        try { using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, secret, cipher, tag); return [.. nonce, .. cipher, .. tag]; }
        finally { CryptographicOperations.ZeroMemory(nonce); CryptographicOperations.ZeroMemory(cipher); CryptographicOperations.ZeroMemory(tag); }
    }
    public bool TryDecrypt(byte keyId, byte[] encrypted, out byte[] secret)
    {
        secret = []; if (keyId == 0 || !_keys.TryGetValue(keyId, out byte[]? key) || encrypted is null || encrypted.Length is < 29 or > 92) return false;
        int length = encrypted.Length - 28; byte[] plain = new byte[length];
        try { using var aes = new AesGcm(key, 16); aes.Decrypt(encrypted.AsSpan(0, 12), encrypted.AsSpan(12, length), encrypted.AsSpan(12 + length, 16), plain); secret = plain; plain = []; return true; }
        catch (CryptographicException) { return false; }
        finally { if (plain.Length > 0) CryptographicOperations.ZeroMemory(plain); }
    }
    public void Dispose() { foreach (byte[] key in _keys.Values) CryptographicOperations.ZeroMemory(key); _keys.Clear(); }
}
