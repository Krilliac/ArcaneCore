using System.Numerics;
using System.Security.Cryptography;

namespace ArcaneCore.Cryptography;

/// <summary>
/// Server side of one WoW-flavor SRP6 authentication exchange.
///
/// Lifecycle (one per logon attempt):
///   1. Construct with the account's stored salt + verifier.
///   2. <see cref="PublicEphemeral"/> / <see cref="Salt"/> feed the logon challenge reply.
///   3. <see cref="TryAcceptProof"/> validates the client's A + M1; on success it
///      exposes the 40-byte <see cref="SessionKey"/> and the M2 <see cref="ServerProof"/>.
///
/// The math lives in <see cref="Srp6Math"/>, verified against the gtker/wow_srp KATs and
/// cross-checked against vmangos SRP6.cpp (Charter §3/§4).
/// </summary>
public sealed class Srp6Server
{
    private readonly BigInteger _verifier;
    private readonly BigInteger _b;          // server private ephemeral
    private readonly BigInteger _bigB;       // server public ephemeral B

    public Srp6Server(byte[] salt, BigInteger verifier)
        : this(salt, verifier, GenerateServerPrivateKey())
    {
    }

    /// <summary>Test seam: inject a fixed server private key b for deterministic vectors.</summary>
    internal Srp6Server(byte[] salt, BigInteger verifier, BigInteger privateB)
    {
        ArgumentNullException.ThrowIfNull(salt);
        if (salt.Length != WowSrp6.SaltLength)
        {
            throw new ArgumentException($"salt must be {WowSrp6.SaltLength} bytes", nameof(salt));
        }

        Salt = salt;
        _verifier = verifier;
        _b = privateB;
        _bigB = Srp6Math.ServerPublicKey(_verifier, _b);
    }

    /// <summary>The account salt (32 bytes), sent in the challenge reply.</summary>
    public byte[] Salt { get; }

    /// <summary>Server public ephemeral B as a fixed 32-byte little-endian array (wire form).</summary>
    public byte[] PublicEphemeral => WowSrp6.ToFixedLittleEndian(_bigB, WowSrp6.KeyLength);

    /// <summary>40-byte session key K. Valid only after <see cref="TryAcceptProof"/> succeeds.</summary>
    public byte[]? SessionKey { get; private set; }

    /// <summary>20-byte server proof M2. Valid only after <see cref="TryAcceptProof"/> succeeds.</summary>
    public byte[]? ServerProof { get; private set; }

    /// <summary>
    /// Validate the client's public key A and proof M1 for <paramref name="username"/>
    /// (the uppercased account name). On success, computes the session key and M2.
    /// </summary>
    public bool TryAcceptProof(string username, byte[] clientPublicKey, byte[] clientProof)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(clientPublicKey);
        ArgumentNullException.ThrowIfNull(clientProof);

        BigInteger a = WowSrp6.FromLittleEndian(clientPublicKey);

        // SRP safeguard: reject A == 0 or A % N == 0 (vmangos CalculateSessionKey).
        if (a.IsZero || (a % WowSrp6.N).IsZero)
        {
            return false;
        }

        BigInteger u = Srp6Math.Scrambler(a, _bigB);
        BigInteger s = Srp6Math.SessionImplicitKey(a, _verifier, u, _b);
        byte[] sessionKey = Srp6Math.Interleave(s);

        byte[] expectedProof = Srp6Math.ClientProof(username, Salt, a, _bigB, sessionKey);

        // Constant-time comparison — the proof is a secret-derived MAC.
        if (!CryptographicOperations.FixedTimeEquals(expectedProof, clientProof))
        {
            return false;
        }

        SessionKey = sessionKey;
        ServerProof = Srp6Math.ServerProof(a, expectedProof, sessionKey);
        return true;
    }

    private static BigInteger GenerateServerPrivateKey()
    {
        // b = 19 random bytes (vmangos CalculateHostPublicEphemeral: b.SetRand(19 * 8)).
        Span<byte> bytes = stackalloc byte[19];
        RandomNumberGenerator.Fill(bytes);
        return new BigInteger(bytes, isUnsigned: true, isBigEndian: false);
    }
}
