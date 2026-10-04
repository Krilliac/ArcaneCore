using System.Numerics;
using ArcaneCore.Kernel.Diagnostics;
using Xunit;

namespace ArcaneCore.Cryptography.Tests.Security;

/// <summary>
/// The SRP6 state-machine invariants (docs/ops/invariants.md): one proof per server object, and the
/// shape of what the exchange produces.
/// </summary>
public sealed class Srp6InvariantTests
{
    [Fact]
    public void SecondProofOnTheSameServer_IsRefused_AndCountedAsAnInvariantFailure()
    {
        byte[] salt = WowSrp6.GenerateSalt();
        BigInteger verifier = WowSrp6.ComputeVerifier(salt, "ERIN", "PW");
        var server = new Srp6Server(salt, verifier);
        byte[] a = WowSrp6.ToFixedLittleEndian(BigInteger.ModPow(WowSrp6.G, 12345, WowSrp6.N), WowSrp6.KeyLength);

        // Invariant.FailureCount is process-wide and xunit runs other test classes of this assembly in parallel,
        // so the exact count is taken from a capture scoped to this test's call flow.
        using InvariantCapture capture = Invariant.Capture();

        // A failed proof leaves the server without a session key: it may be judged once more (the realm never does).
        Assert.False(server.TryAcceptProof("ERIN", a, new byte[Sha1.DigestLength]));
        Assert.Null(server.SessionKey);
        Assert.Empty(capture.Failures);

        // After a success the state is consumed: the exact same (valid) proof is refused and the failure is counted.
        SimulatedClient client = SimulatedClient.Login("ERIN", "PW", salt, server.PublicEphemeral);
        Assert.True(server.TryAcceptProof("ERIN", client.PublicKey, client.Proof));
        Assert.Empty(capture.Failures);
        Assert.False(server.TryAcceptProof("ERIN", client.PublicKey, client.Proof));
        InvariantFailureEvent failure = Assert.Single(capture.Failures);
        Assert.Equal("Check", failure.Kind);
        Assert.Equal(nameof(Srp6Server.TryAcceptProof), failure.Member);
        Assert.Equal("Srp6Server.cs", failure.File);
        Assert.Contains(Invariant.Failures(), f => f.Member == nameof(Srp6Server.TryAcceptProof) && f.File == "Srp6Server.cs");
    }

    [Fact]
    public void GeneratedSalts_AreUsable_AndSessionKeysHaveTheWireLength()
    {
        for (int i = 0; i < 64; i++)
        {
            byte[] salt = WowSrp6.GenerateSalt();
            Assert.True(Srp6Validation.IsUsableSalt(salt));
            Assert.NotEqual(0, salt[^1]);
        }

        byte[] s = WowSrp6.GenerateSalt();
        BigInteger v = WowSrp6.ComputeVerifier(s, "FAY", "PW");
        var server = new Srp6Server(s, v);
        SimulatedClient client = SimulatedClient.Login("FAY", "PW", s, server.PublicEphemeral);
        Assert.True(server.TryAcceptProof("FAY", client.PublicKey, client.Proof));
        Assert.Equal(WowSrp6.SessionKeyLength, server.SessionKey!.Length);
        Assert.Equal(Sha1.DigestLength, server.ServerProof!.Length);
    }

    /// <summary>The standard client-side math, enough to produce one valid proof.</summary>
    private sealed class SimulatedClient
    {
        public required byte[] PublicKey { get; init; }

        public required byte[] Proof { get; init; }

        public static SimulatedClient Login(string username, string password, byte[] salt, byte[] serverPublicKey)
        {
            BigInteger a = 987654321;
            BigInteger publicA = BigInteger.ModPow(WowSrp6.G, a, WowSrp6.N);
            BigInteger b = WowSrp6.FromLittleEndian(serverPublicKey);
            BigInteger x = WowSrp6.ComputeX(salt, username, password);
            BigInteger u = Srp6Math.Scrambler(publicA, b);
            BigInteger kgx = WowSrp6.Multiplier * BigInteger.ModPow(WowSrp6.G, x, WowSrp6.N) % WowSrp6.N;
            BigInteger numerator = ((b - kgx) % WowSrp6.N + WowSrp6.N) % WowSrp6.N;
            BigInteger s = BigInteger.ModPow(numerator, a + u * x, WowSrp6.N);
            byte[] sessionKey = Srp6Math.Interleave(s);
            return new SimulatedClient
            {
                PublicKey = WowSrp6.ToFixedLittleEndian(publicA, WowSrp6.KeyLength),
                Proof = Srp6Math.ClientProof(username, salt, publicA, b, sessionKey),
            };
        }
    }
}
