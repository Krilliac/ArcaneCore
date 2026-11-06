using System.Security.Cryptography;
using Konscious.Security.Cryptography;
using Xunit;

namespace ArcaneCore.Cryptography.Tests.Login;

/// <summary>RFC 9106 section 5.3 Argon2id test vector verified against the published RFC.</summary>
public sealed class Argon2Rfc9106Tests
{
    [Fact]
    public void Argon2idSection53VectorMatchesPublishedDigest()
    {
        byte[] password = Enumerable.Repeat((byte)0x01, 32).ToArray();
        byte[] salt = Enumerable.Repeat((byte)0x02, 16).ToArray();
        byte[] secret = Enumerable.Repeat((byte)0x03, 8).ToArray();
        byte[] associatedData = Enumerable.Repeat((byte)0x04, 12).ToArray();
        byte[] expected = Convert.FromHexString("0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659");
        using var argon = new Argon2id(password)
        {
            Salt = salt,
            KnownSecret = secret,
            AssociatedData = associatedData,
            MemorySize = 32,
            Iterations = 3,
            DegreeOfParallelism = 4,
        };
        Assert.Equal(expected, argon.GetBytes(32));
        CryptographicOperations.ZeroMemory(password);
        CryptographicOperations.ZeroMemory(salt);
        CryptographicOperations.ZeroMemory(secret);
        CryptographicOperations.ZeroMemory(associatedData);
    }
}
