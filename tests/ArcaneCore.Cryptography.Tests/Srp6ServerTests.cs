using System.Numerics;
using System.Security.Cryptography;
using Xunit;

namespace ArcaneCore.Cryptography.Tests;

/// <summary>
/// Full-exchange tests: a simulated SRP6 client (the standard client-side math) against
/// <see cref="Srp6Server"/>, proving challenge → proof → session-key agreement and that a
/// wrong password is rejected.
/// </summary>
public sealed class Srp6ServerTests
{
    [Theory]
    [InlineData("ALICE", "HUNTER2")]
    [InlineData("BOB", "BOB")] // password == username (the auto-create case)
    [InlineData("A", "PASSWORD")]
    public void Server_AcceptsCorrectClientProof(string username, string password)
    {
        byte[] salt = WowSrp6.GenerateSalt();
        BigInteger verifier = WowSrp6.ComputeVerifier(salt, username, password);
        var server = new Srp6Server(salt, verifier);

        SimulatedClient client = SimulatedClient.Login(username, password, salt, server.PublicEphemeral);

        bool accepted = server.TryAcceptProof(username, client.PublicKey, client.Proof);

        Assert.True(accepted);
        Assert.NotNull(server.SessionKey);
        Assert.NotNull(server.ServerProof);
        Assert.Equal(client.SessionKey, server.SessionKey);
        Assert.Equal(WowSrp6.SessionKeyLength, server.SessionKey!.Length);

        // The client validates the server's M2 the same way the real client does.
        byte[] expectedM2 = client.ExpectedServerProof();
        Assert.Equal(expectedM2, server.ServerProof);
    }

    [Fact]
    public void Server_RejectsWrongPassword()
    {
        const string username = "CHARLIE";
        byte[] salt = WowSrp6.GenerateSalt();
        BigInteger verifier = WowSrp6.ComputeVerifier(salt, username, "CORRECT");
        var server = new Srp6Server(salt, verifier);

        SimulatedClient client = SimulatedClient.Login(username, "WRONG", salt, server.PublicEphemeral);

        Assert.False(server.TryAcceptProof(username, client.PublicKey, client.Proof));
        Assert.Null(server.SessionKey);
        Assert.Null(server.ServerProof);
    }

    [Fact]
    public void Server_RejectsZeroPublicKey()
    {
        byte[] salt = WowSrp6.GenerateSalt();
        BigInteger verifier = WowSrp6.ComputeVerifier(salt, "DAVE", "PW");
        var server = new Srp6Server(salt, verifier);

        Assert.False(server.TryAcceptProof("DAVE", new byte[32], new byte[20]));
    }

    /// <summary>Minimal correct SRP6 client, used only to exercise the server.</summary>
    private sealed class SimulatedClient
    {
        public required byte[] PublicKey { get; init; }    // A, 32-byte LE
        public required byte[] Proof { get; init; }         // M1
        public required byte[] SessionKey { get; init; }    // K
        private BigInteger A { get; init; }

        public static SimulatedClient Login(string username, string password, byte[] salt, byte[] serverPublicKey)
        {
            BigInteger a = RandomPrivate();
            BigInteger publicA = BigInteger.ModPow(WowSrp6.G, a, WowSrp6.N);
            BigInteger b = WowSrp6.FromLittleEndian(serverPublicKey);

            BigInteger x = WowSrp6.ComputeX(salt, username, password);
            BigInteger u = Srp6Math.Scrambler(publicA, b);

            // S = (B - k * g^x) ^ (a + u*x) mod N
            BigInteger kgx = WowSrp6.Multiplier * BigInteger.ModPow(WowSrp6.G, x, WowSrp6.N) % WowSrp6.N;
            BigInteger numerator = ((b - kgx) % WowSrp6.N + WowSrp6.N) % WowSrp6.N;
            BigInteger s = BigInteger.ModPow(numerator, a + u * x, WowSrp6.N);

            byte[] sessionKey = Srp6Math.Interleave(s);
            byte[] proof = Srp6Math.ClientProof(username, salt, publicA, b, sessionKey);

            return new SimulatedClient
            {
                A = publicA,
                PublicKey = WowSrp6.ToFixedLittleEndian(publicA, WowSrp6.KeyLength),
                Proof = proof,
                SessionKey = sessionKey,
            };
        }

        public byte[] ExpectedServerProof() => Srp6Math.ServerProof(A, Proof, SessionKey);

        private static BigInteger RandomPrivate()
        {
            Span<byte> bytes = stackalloc byte[19];
            RandomNumberGenerator.Fill(bytes);
            return new BigInteger(bytes, isUnsigned: true, isBigEndian: false);
        }
    }
}
