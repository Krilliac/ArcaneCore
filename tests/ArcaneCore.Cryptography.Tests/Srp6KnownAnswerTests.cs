using System.Numerics;
using Xunit;

namespace ArcaneCore.Cryptography.Tests;

/// <summary>
/// Verifies every SRP6 step byte-for-byte against the gtker/wow_srp known-answer vectors
/// (1000 cases per step). These vectors are reverse-engineered from the real 1.12 client,
/// so passing them is strong evidence of on-the-wire conformance.
/// </summary>
public sealed class Srp6KnownAnswerTests
{
    public static IEnumerable<object[]> Verifiers() => Rows("calculate_v_values.txt");

    [Theory]
    [MemberData(nameof(Verifiers))]
    public void ComputeVerifier_MatchesVector(string username, string password, string saltHex, string verifierHex)
    {
        byte[] salt = Hex.ToBytesLittleEndian(saltHex);
        BigInteger expected = Hex.ToBigInteger(verifierHex);

        BigInteger actual = WowSrp6.ComputeVerifier(salt, username, password);

        Assert.Equal(expected, actual);
    }

    public static IEnumerable<object[]> PublicKeys() => Rows("calculate_B_values.txt");

    [Theory]
    [MemberData(nameof(PublicKeys))]
    public void ServerPublicKey_MatchesVector(string verifierHex, string privateBHex, string publicBHex)
    {
        BigInteger actual = Srp6Math.ServerPublicKey(Hex.ToBigInteger(verifierHex), Hex.ToBigInteger(privateBHex));
        Assert.Equal(Hex.ToBigInteger(publicBHex), actual);
    }

    public static IEnumerable<object[]> Scramblers() => Rows("calculate_u_values.txt");

    [Theory]
    [MemberData(nameof(Scramblers))]
    public void Scrambler_MatchesVector(string aHex, string bHex, string uHex)
    {
        BigInteger actual = Srp6Math.Scrambler(Hex.ToBigInteger(aHex), Hex.ToBigInteger(bHex));
        Assert.Equal(Hex.ToBigInteger(uHex), actual);
    }

    public static IEnumerable<object[]> SessionImplicitKeys() => Rows("calculate_S_values.txt");

    [Theory]
    [MemberData(nameof(SessionImplicitKeys))]
    public void SessionImplicitKey_MatchesVector(string aHex, string vHex, string uHex, string bHex, string sHex)
    {
        BigInteger actual = Srp6Math.SessionImplicitKey(
            Hex.ToBigInteger(aHex), Hex.ToBigInteger(vHex), Hex.ToBigInteger(uHex), Hex.ToBigInteger(bHex));
        Assert.Equal(Hex.ToBigInteger(sHex), actual);
    }

    public static IEnumerable<object[]> Interleaves() => Rows("calculate_interleaved_values.txt");

    [Theory]
    [MemberData(nameof(Interleaves))]
    public void Interleave_MatchesVector(string sHex, string sessionKeyHex)
    {
        byte[] actual = Srp6Math.Interleave(Hex.ToBigInteger(sHex));
        Assert.Equal(Hex.ToBytesLittleEndian(sessionKeyHex), actual);
    }

    public static IEnumerable<object[]> SessionKeys() => Rows("calculate_server_session_key.txt");

    [Theory]
    [MemberData(nameof(SessionKeys))]
    public void FullSessionKey_MatchesVector(string aHex, string vHex, string bHex, string sessionKeyHex)
    {
        BigInteger a = Hex.ToBigInteger(aHex);
        BigInteger v = Hex.ToBigInteger(vHex);
        BigInteger b = Hex.ToBigInteger(bHex);

        BigInteger publicB = Srp6Math.ServerPublicKey(v, b);
        BigInteger u = Srp6Math.Scrambler(a, publicB);
        BigInteger s = Srp6Math.SessionImplicitKey(a, v, u, b);
        byte[] actual = Srp6Math.Interleave(s);

        Assert.Equal(Hex.ToBytesLittleEndian(sessionKeyHex), actual);
    }

    public static IEnumerable<object[]> ClientProofs() => Rows("calculate_M1_values.txt");

    [Theory]
    [MemberData(nameof(ClientProofs))]
    public void ClientProof_MatchesVector(
        string username, string sessionKeyHex, string aHex, string bHex, string saltHex, string m1Hex)
    {
        byte[] actual = Srp6Math.ClientProof(
            username,
            Hex.ToBytesLittleEndian(saltHex),
            Hex.ToBigInteger(aHex),
            Hex.ToBigInteger(bHex),
            Hex.ToBytesLittleEndian(sessionKeyHex));

        Assert.Equal(Hex.ToBytesLittleEndian(m1Hex), actual);
    }

    public static IEnumerable<object[]> ServerProofs() => Rows("calculate_M2_values.txt");

    [Theory]
    [MemberData(nameof(ServerProofs))]
    public void ServerProof_MatchesVector(string aHex, string m1Hex, string sessionKeyHex, string m2Hex)
    {
        byte[] actual = Srp6Math.ServerProof(
            Hex.ToBigInteger(aHex),
            Hex.ToBytesLittleEndian(m1Hex),
            Hex.ToBytesLittleEndian(sessionKeyHex));

        Assert.Equal(Hex.ToBytesLittleEndian(m2Hex), actual);
    }

    private static IEnumerable<object[]> Rows(string fileName)
        => Hex.ReadColumns(fileName).Select(columns => columns.Cast<object>().ToArray());
}
