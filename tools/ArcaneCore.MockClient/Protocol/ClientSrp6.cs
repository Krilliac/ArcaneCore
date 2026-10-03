using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace ArcaneCore.MockClient.Protocol;

/// <summary>Client-side vanilla SRP6, independently implemented using BigInteger and SHA-1.</summary>
internal static class ClientSrp6
{
    internal static readonly byte[] PrimeBytes = Convert.FromHexString("B79B3E2A87823CAB8F5EBFBF8EB10108535006298B5BADBD5B53E1895E644B89");
    private static readonly BigInteger Prime = Number(PrimeBytes);

    internal sealed record Session(byte[] PublicKey, byte[] ClientProof, byte[] ExpectedServerProof, byte[] SessionKey);

    internal static Session Create(string account, string password, byte[] salt, byte[] serverPublicKey)
    {
        byte[] privateBytes = RandomNumberGenerator.GetBytes(32);
        privateBytes[0] |= 1;
        return Calculate(account, password, salt, serverPublicKey, privateBytes);
    }

    internal static Session Calculate(string account, string password, byte[] salt, byte[] serverPublicKey, byte[] privateBytes)
    {
        if (salt.Length != 32 || serverPublicKey.Length != 32 || privateBytes.Length != 32)
        {
            throw new MockProtocolException("SRP6 requires 32-byte salt, public key and private key fields.");
        }

        BigInteger server = Number(serverPublicKey);
        BigInteger secret = Number(privateBytes);
        if (server <= 0 || server >= Prime || secret <= 0)
        {
            throw new MockProtocolException("SRP6 peer supplied an invalid public key, or the private key is zero.");
        }

        byte[] publicKey = PublicKey(privateBytes);
        byte[] x = Hash(salt, Hash(Encoding.ASCII.GetBytes($"{account}:{password}")));
        byte[] scrambler = Hash(publicKey, serverPublicKey);
        if (Number(scrambler).IsZero)
        {
            throw new MockProtocolException("SRP6 scrambling parameter is zero.");
        }

        byte[] shared = SharedSecret(serverPublicKey, privateBytes, x, scrambler);
        if (Number(shared).IsZero)
        {
            throw new MockProtocolException("SRP6 shared secret is zero.");
        }

        byte[] key = Interleave(shared);
        byte[] clientProof = Proof(account, salt, publicKey, serverPublicKey, key);
        return new Session(publicKey, clientProof, Hash(publicKey, clientProof, key), key);
    }

    internal static byte[] PublicKey(byte[] privateBytes) => Fixed(BigInteger.ModPow(7, Number(privateBytes), Prime));

    internal static byte[] SharedSecret(byte[] serverPublicKey, byte[] privateBytes, byte[] xBytes, byte[] scramblerBytes)
    {
        BigInteger x = Number(xBytes);
        BigInteger basis = (Number(serverPublicKey) - 3 * BigInteger.ModPow(7, x, Prime)) % Prime;
        if (basis.Sign < 0)
        {
            basis += Prime;
        }

        return Fixed(BigInteger.ModPow(basis, Number(privateBytes) + Number(scramblerBytes) * x, Prime));
    }

    internal static byte[] Proof(string account, byte[] salt, byte[] clientPublicKey, byte[] serverPublicKey, byte[] key)
    {
        byte[] primeHash = Hash(PrimeBytes);
        byte[] generatorHash = Hash([7]);
        for (int index = 0; index < primeHash.Length; index++)
        {
            primeHash[index] ^= generatorHash[index];
        }

        return Hash(primeHash, Hash(Encoding.ASCII.GetBytes(account)), salt, clientPublicKey, serverPublicKey, key);
    }

    internal static byte[] Interleave(byte[] sharedSecret)
    {
        if (sharedSecret.Length != 32)
        {
            throw new ArgumentException("SRP6 shared secret must be 32 bytes.", nameof(sharedSecret));
        }

        int start = 0;
        while (start < sharedSecret.Length && sharedSecret[start] == 0)
        {
            start++;
        }

        start += start % 2;
        int half = (sharedSecret.Length - start) / 2;
        byte[] even = new byte[half];
        byte[] odd = new byte[half];
        for (int index = 0; index < half; index++)
        {
            even[index] = sharedSecret[start + 2 * index];
            odd[index] = sharedSecret[start + 2 * index + 1];
        }

        byte[] first = Hash(even);
        byte[] second = Hash(odd);
        byte[] result = new byte[40];
        for (int index = 0; index < 20; index++)
        {
            result[2 * index] = first[index];
            result[2 * index + 1] = second[index];
        }

        return result;
    }

    internal static byte[] Hash(params byte[][] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        foreach (byte[] field in fields)
        {
            hash.AppendData(field);
        }

        return hash.GetHashAndReset();
    }

    private static BigInteger Number(byte[] bytes) => new(bytes, isUnsigned: true, isBigEndian: false);

    private static byte[] Fixed(BigInteger value)
    {
        byte[] result = new byte[32];
        if (!value.TryWriteBytes(result, out _, isUnsigned: true, isBigEndian: false))
        {
            throw new MockProtocolException("SRP6 integer exceeds its 32-byte wire field.");
        }

        return result;
    }
}
