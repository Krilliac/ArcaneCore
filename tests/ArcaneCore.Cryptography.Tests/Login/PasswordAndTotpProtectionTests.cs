using System.Security.Cryptography;
using ArcaneCore.Cryptography.Login;
using Xunit;

namespace ArcaneCore.Cryptography.Tests.Login;

public sealed class PasswordAndTotpProtectionTests
{
    [Fact]
    public void Argon2idRoundTripRejectsWrongPasswordAndUsesDifferentSalts()
    {
        var hasher = new Argon2idPasswordHasher();
        string first = hasher.Hash("correct horse battery staple");
        string second = hasher.Hash("correct horse battery staple");
        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", first);
        Assert.NotEqual(first, second);
        Assert.True(hasher.Verify("correct horse battery staple", first));
        Assert.False(hasher.Verify("correct horse battery stapler", first));
        Assert.False(hasher.Verify("short", first));
    }

    [Theory]
    [InlineData("$argon2id$v=19$m=65537,t=3,p=1$YWJjZGVmZ2hpamtsbW5vcA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=1,t=3,p=1$YWJjZGVmZ2hpamtsbW5vcA$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$not-base64$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("$argon2id$v=19$m=65536,t=3,p=1$YWJjZGVmZ2hpamtsbW5vcA$AAAA")]
    public void Argon2idRejectsMalformedOrUnboundedPhcBeforeDerivation(string encoded)
        => Assert.False(new Argon2idPasswordHasher().Verify("correct horse battery staple", encoded));

    [Theory]
    [InlineData(9)]
    [InlineData(257)]
    public void Argon2idRejectsPasswordLengthOutsidePolicy(int length)
        => Assert.Throws<ArgumentException>(() => new Argon2idPasswordHasher().Hash(new string('x', length)));

    [Fact]
    public void TotpProtectorRoundTripsAndRejectsTamperingAndWrongKey()
    {
        byte[] secret = Enumerable.Repeat((byte)0x33, 20).ToArray();
        using var protector = new AesGcmTotpSecretProtector(new Dictionary<byte, byte[]> { [4] = new byte[32], [7] = new byte[32] });
        byte[] encrypted = protector.Encrypt(4, secret);
        Assert.True(protector.TryDecrypt(4, encrypted, out byte[] recovered));
        Assert.Equal(secret, recovered);
        encrypted[^1] ^= 0x01;
        Assert.False(protector.TryDecrypt(4, encrypted, out _));
        Assert.False(protector.TryDecrypt(9, encrypted, out _));
        CryptographicOperations.ZeroMemory(recovered);
    }

    [Fact]
    public void TotpProtectorRejectsInvalidSizesAndDisposal()
    {
        Assert.Throws<ArgumentException>(() => new AesGcmTotpSecretProtector(new Dictionary<byte, byte[]> { [1] = new byte[31] }));
        var protector = new AesGcmTotpSecretProtector(new Dictionary<byte, byte[]> { [1] = new byte[32] });
        Assert.Throws<ArgumentException>(() => protector.Encrypt(1, Array.Empty<byte>()));
        Assert.Throws<ArgumentException>(() => protector.Encrypt(1, new byte[65]));
        protector.Dispose();
        Assert.Throws<ArgumentException>(() => protector.Encrypt(1, new byte[20]));
    }
}
