using System.Numerics;
using Xunit;

namespace ArcaneCore.Cryptography.Tests.Security;

/// <summary>
/// A degenerate stored verifier (0, 1, N, >= N) makes the SRP shared secret independent
/// of any password, so anybody can forge M1 from public data alone. vmangos refuses a zero
/// verifier/salt (src/shared/Crypto/Authentication/SRP6.cpp:188-199); the 1 / N / >= N cases
/// are an extension of that rule.
/// </summary>
public sealed class Srp6DegenerateInputTests
{
    private const string User = "VICTIM";

    public static TheoryData<string> DegenerateVerifiers => new() { "zero", "one", "n", "n_plus_1" };

    private static BigInteger Verifier(string kind) => kind switch
    {
        "zero" => BigInteger.Zero,
        "one" => BigInteger.One,
        "n" => WowSrp6.N,
        _ => WowSrp6.N + 1,
    };

    [Theory]
    [MemberData(nameof(DegenerateVerifiers))]
    public void DegenerateVerifier_CannotBeLoggedIntoWithPublicDataOnly(string kind)
    {
        byte[] salt = WowSrp6.GenerateSalt();
        var server = new Srp6Server(salt, Verifier(kind));
        BigInteger bigB = WowSrp6.FromLittleEndian(server.PublicEphemeral);

        // Attacker picks A = g^a. For v = 0 / N the shared secret S is 0; for v = 1 it is
        // (B - k)^a. v = N+1 reduces to v = 1 modulo N.
        BigInteger a = 123456789;
        BigInteger bigA = BigInteger.ModPow(WowSrp6.G, a, WowSrp6.N);
        BigInteger s = kind is "zero" or "n"
            ? BigInteger.Zero
            : BigInteger.ModPow(((bigB - WowSrp6.Multiplier) % WowSrp6.N + WowSrp6.N) % WowSrp6.N, a, WowSrp6.N);
        byte[] k = Srp6Math.Interleave(s);
        byte[] m1 = Srp6Math.ClientProof(User, salt, bigA, bigB, k);

        bool accepted = server.TryAcceptProof(User, WowSrp6.ToFixedLittleEndian(bigA, 32), m1);

        Assert.False(accepted);
        Assert.Null(server.SessionKey);
    }

    [Theory]
    [MemberData(nameof(DegenerateVerifiers))]
    public void TryCreate_RejectsDegenerateVerifier(string kind)
        => Assert.Null(Srp6Server.TryCreate(WowSrp6.GenerateSalt(), Verifier(kind)));

    [Fact]
    public void TryCreate_RejectsZeroOrWrongLengthSalt()
    {
        BigInteger v = WowSrp6.ComputeVerifier(WowSrp6.GenerateSalt(), User, "PW");
        Assert.Null(Srp6Server.TryCreate(new byte[32], v));
        Assert.Null(Srp6Server.TryCreate(new byte[31], v));
        Assert.Null(Srp6Server.TryCreate(new byte[33], v));
        Assert.Null(Srp6Server.TryCreate(null!, v));
    }

    [Fact]
    public void TryCreate_AcceptsAValidAccount()
    {
        byte[] salt = WowSrp6.GenerateSalt();
        Assert.NotNull(Srp6Server.TryCreate(salt, WowSrp6.ComputeVerifier(salt, User, "PW")));
    }

    [Fact]
    public void PublicKeyLongerThan32Bytes_ReturnsFalseInsteadOfThrowing()
    {
        byte[] salt = WowSrp6.GenerateSalt();
        var server = new Srp6Server(salt, WowSrp6.ComputeVerifier(salt, User, "PW"));
        byte[] longA = new byte[40];
        longA[39] = 1;
        Assert.False(server.TryAcceptProof(User, longA, new byte[20]));
    }

    [Fact]
    public void PublicKeyAtOrAboveN_AndWrongProofLength_AreRejected()
    {
        byte[] salt = WowSrp6.GenerateSalt();
        var server = new Srp6Server(salt, WowSrp6.ComputeVerifier(salt, User, "PW"));
        Assert.False(server.TryAcceptProof(User, WowSrp6.NLittleEndian, new byte[20]));
        Assert.False(server.TryAcceptProof(User, new byte[32], new byte[20]));
        byte[] a = WowSrp6.ToFixedLittleEndian(WowSrp6.G, 32);
        Assert.False(server.TryAcceptProof(User, a, new byte[19]));
    }
}
