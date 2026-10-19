using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Kernel.Diagnostics;
using Xunit;

namespace ArcaneCore.Cryptography.Tests.Fuzz;

/// <summary>
/// Deterministic seeded fuzzers and exhaustive exception/guard tests for <c>ArcaneCore.Cryptography</c>:
/// <c>WowSrp6</c>, <c>Srp6Math</c>, <c>Srp6Validation</c>, <c>Srp6Server</c>, <c>Sha1</c>,
/// <c>ClientIntegrity</c>, <c>PinHash</c>, and <c>Totp</c>.
/// </summary>
public sealed class CryptographyFuzzAndExceptionTests
{
    public static TheoryData<int> Seeds => [1, 42, 20251004];

    // -------------------------------------------------------------------------
    // 1. Srp6Server exception & guard tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Srp6Server_Constructor_ThrowsArgumentNullException_WhenSaltIsNull()
    {
        BigInteger verifier = WowSrp6.ComputeVerifier(WowSrp6.GenerateSalt(), "ALICE", "PASS");

        ArgumentNullException pubEx = Assert.Throws<ArgumentNullException>(() => new Srp6Server(null!, verifier));
        Assert.Equal("salt", pubEx.ParamName);

        ArgumentNullException intEx = Assert.Throws<ArgumentNullException>(() => new Srp6Server(null!, verifier, new BigInteger(12345)));
        Assert.Equal("salt", intEx.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(40)]
    [InlineData(64)]
    public void Srp6Server_Constructor_ThrowsArgumentException_WhenSaltLengthIsNot32(int badLength)
    {
        BigInteger verifier = WowSrp6.ComputeVerifier(WowSrp6.GenerateSalt(), "ALICE", "PASS");
        byte[] badSalt = new byte[badLength];

        ArgumentException pubEx = Assert.Throws<ArgumentException>(() => new Srp6Server(badSalt, verifier));
        Assert.Equal("salt", pubEx.ParamName);

        ArgumentException intEx = Assert.Throws<ArgumentException>(() => new Srp6Server(badSalt, verifier, new BigInteger(12345)));
        Assert.Equal("salt", intEx.ParamName);
    }

    [Fact]
    public void Srp6Server_TryAcceptProof_ThrowsArgumentNullException_OnNullArguments()
    {
        byte[] salt = WowSrp6.GenerateSalt();
        BigInteger verifier = WowSrp6.ComputeVerifier(salt, "ALICE", "PASS");
        var server = new Srp6Server(salt, verifier);
        byte[] dummyKey = new byte[WowSrp6.KeyLength];
        byte[] dummyProof = new byte[Sha1.DigestLength];

        ArgumentNullException userEx = Assert.Throws<ArgumentNullException>(() => server.TryAcceptProof(null!, dummyKey, dummyProof));
        Assert.Equal("username", userEx.ParamName);

        ArgumentNullException keyEx = Assert.Throws<ArgumentNullException>(() => server.TryAcceptProof("ALICE", null!, dummyProof));
        Assert.Equal("clientPublicKey", keyEx.ParamName);

        ArgumentNullException proofEx = Assert.Throws<ArgumentNullException>(() => server.TryAcceptProof("ALICE", dummyKey, null!));
        Assert.Equal("clientProof", proofEx.ParamName);
    }

    [Fact]
    public void Srp6Server_TryCreate_And_Srp6Validation_RejectAllBoundaryAndAdversarialInputs()
    {
        byte[] validSalt = WowSrp6.GenerateSalt();
        BigInteger validVerifier = WowSrp6.ComputeVerifier(validSalt, "USER", "PASS");

        // Verifier boundaries: negative, 0, 1, N, N+1, 2N are all unusable; 2 and N-1 are usable.
        BigInteger[] invalidVerifiers =
        [
            -WowSrp6.N,
            new BigInteger(-1),
            BigInteger.Zero,
            BigInteger.One,
            WowSrp6.N,
            WowSrp6.N + BigInteger.One,
            WowSrp6.N * 2,
            BigInteger.One << 256,
        ];

        foreach (BigInteger badV in invalidVerifiers)
        {
            Assert.False(Srp6Validation.IsUsableVerifier(badV));
            Assert.Null(Srp6Server.TryCreate(validSalt, badV));
        }

        Assert.True(Srp6Validation.IsUsableVerifier(new BigInteger(2)));
        Assert.True(Srp6Validation.IsUsableVerifier(WowSrp6.N - BigInteger.One));
        Assert.NotNull(Srp6Server.TryCreate(validSalt, new BigInteger(2)));
        Assert.NotNull(Srp6Server.TryCreate(validSalt, WowSrp6.N - BigInteger.One));

        // Salt boundaries: null, wrong length, or all-zero are unusable; any single non-zero byte is usable.
        Assert.False(Srp6Validation.IsUsableSalt(null));
        Assert.False(Srp6Validation.IsUsableSalt([]));
        Assert.False(Srp6Validation.IsUsableSalt(new byte[31]));
        Assert.False(Srp6Validation.IsUsableSalt(new byte[33]));
        Assert.False(Srp6Validation.IsUsableSalt(new byte[32]));

        for (int pos = 0; pos < WowSrp6.SaltLength; pos++)
        {
            byte[] singleBitSalt = new byte[WowSrp6.SaltLength];
            singleBitSalt[pos] = 1;
            Assert.True(Srp6Validation.IsUsableSalt(singleBitSalt));
        }

        // Client key A boundaries: null, wrong length, 0, N, N+1, all-0xFF are unusable; 1 and N-1 are usable.
        Assert.False(Srp6Validation.IsUsableClientKey(null));
        Assert.False(Srp6Validation.IsUsableClientKey([]));
        Assert.False(Srp6Validation.IsUsableClientKey(new byte[31]));
        Assert.False(Srp6Validation.IsUsableClientKey(new byte[33]));
        Assert.False(Srp6Validation.IsUsableClientKey(new byte[32]));
        Assert.False(Srp6Validation.IsUsableClientKey(WowSrp6.NLittleEndian));
        Assert.False(Srp6Validation.IsUsableClientKey(WowSrp6.ToFixedLittleEndian(WowSrp6.N + BigInteger.One, WowSrp6.KeyLength)));

        byte[] allOnes = new byte[WowSrp6.KeyLength];
        Array.Fill(allOnes, (byte)0xFF);
        Assert.False(Srp6Validation.IsUsableClientKey(allOnes));

        Assert.True(Srp6Validation.IsUsableClientKey(WowSrp6.ToFixedLittleEndian(BigInteger.One, WowSrp6.KeyLength)));
        Assert.True(Srp6Validation.IsUsableClientKey(WowSrp6.ToFixedLittleEndian(WowSrp6.N - BigInteger.One, WowSrp6.KeyLength)));
        Assert.NotNull(Srp6Server.TryCreate(validSalt, validVerifier));
    }

    [Fact]
    public void Srp6Server_WithAllZeroSalt_NeverAcceptsProofEvenWithCorrectMath()
    {
        byte[] zeroSalt = new byte[WowSrp6.SaltLength];
        BigInteger verifier = WowSrp6.ComputeVerifier(zeroSalt, "ALICE", "PASS");
        var server = new Srp6Server(zeroSalt, verifier, new BigInteger(99999));

        BigInteger aPriv = new(1234567);
        BigInteger bigA = BigInteger.ModPow(WowSrp6.G, aPriv, WowSrp6.N);
        BigInteger bigB = WowSrp6.FromLittleEndian(server.PublicEphemeral);
        BigInteger x = WowSrp6.ComputeX(zeroSalt, "ALICE", "PASS");
        BigInteger u = Srp6Math.Scrambler(bigA, bigB);
        BigInteger kgx = WowSrp6.Multiplier * BigInteger.ModPow(WowSrp6.G, x, WowSrp6.N) % WowSrp6.N;
        BigInteger num = ((bigB - kgx) % WowSrp6.N + WowSrp6.N) % WowSrp6.N;
        BigInteger s = BigInteger.ModPow(num, aPriv + u * x, WowSrp6.N);
        byte[] k = Srp6Math.Interleave(s);
        byte[] m1 = Srp6Math.ClientProof("ALICE", zeroSalt, bigA, bigB, k);

        Assert.False(server.TryAcceptProof("ALICE", WowSrp6.ToFixedLittleEndian(bigA, WowSrp6.KeyLength), m1));
        Assert.Null(server.SessionKey);
        Assert.Null(server.ServerProof);
    }

    // -------------------------------------------------------------------------
    // 2. WowSrp6 & Srp6Math big-integer serialization & exponent exception tests
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(-1)]
    [InlineData(-255)]
    [InlineData(-65536)]
    public void WowSrp6_ToMinimalLittleEndian_ThrowsOverflowException_WhenValueIsNegative(int negativeValue)
    {
        Assert.Throws<OverflowException>(() => WowSrp6.ToMinimalLittleEndian(new BigInteger(negativeValue)));
        Assert.Throws<OverflowException>(() => WowSrp6.ToMinimalLittleEndian(-WowSrp6.N));
    }

    [Theory]
    [InlineData(-1, 32)]
    [InlineData(-256, 32)]
    [InlineData(-1, 0)]
    public void WowSrp6_ToFixedLittleEndian_ThrowsOverflowException_WhenValueIsNegative(int negativeValue, int length)
    {
        Assert.Throws<OverflowException>(() => WowSrp6.ToFixedLittleEndian(new BigInteger(negativeValue), length));
        Assert.Throws<OverflowException>(() => WowSrp6.ToFixedLittleEndian(-WowSrp6.N, WowSrp6.KeyLength));
    }

    [Fact]
    public void WowSrp6_ToFixedLittleEndian_ThrowsArgumentException_WhenValueExceedsFixedLength()
    {
        // Zero with negative length: minimal is [] (length 0 > -1), so throws ArgumentException.
        ArgumentException negLenEx = Assert.Throws<ArgumentException>(() => WowSrp6.ToFixedLittleEndian(BigInteger.Zero, -1));
        Assert.Equal("value", negLenEx.ParamName);

        // 1 requires 1 byte, exceeds length 0.
        ArgumentException zeroLenEx = Assert.Throws<ArgumentException>(() => WowSrp6.ToFixedLittleEndian(BigInteger.One, 0));
        Assert.Equal("value", zeroLenEx.ParamName);

        // 256 requires 2 bytes, exceeds length 1.
        ArgumentException oneByteEx = Assert.Throws<ArgumentException>(() => WowSrp6.ToFixedLittleEndian(new BigInteger(256), 1));
        Assert.Equal("value", oneByteEx.ParamName);

        // N requires 32 bytes, exceeds length 31.
        ArgumentException n31Ex = Assert.Throws<ArgumentException>(() => WowSrp6.ToFixedLittleEndian(WowSrp6.N, 31));
        Assert.Equal("value", n31Ex.ParamName);

        // 2^256 requires 33 bytes, exceeds length 32.
        BigInteger thirtyThreeBytes = BigInteger.One << 256;
        ArgumentException wideEx = Assert.Throws<ArgumentException>(() => WowSrp6.ToFixedLittleEndian(thirtyThreeBytes, WowSrp6.KeyLength));
        Assert.Equal("value", wideEx.ParamName);
    }

    [Fact]
    public void Srp6Math_KeyAndExponentMethods_ThrowOnNegativeOrOverwideValues()
    {
        BigInteger validKey = WowSrp6.G;
        BigInteger negativeKey = new(-1);
        BigInteger overwideKey = BigInteger.One << 256; // 33 bytes
        byte[] dummySalt = new byte[WowSrp6.SaltLength];
        byte[] dummyM1 = new byte[Sha1.DigestLength];
        byte[] dummyK = new byte[WowSrp6.SessionKeyLength];

        Assert.Throws<ArgumentOutOfRangeException>(() => Srp6Math.ServerPublicKey(validKey, negativeKey));
        Assert.Throws<ArgumentOutOfRangeException>(() => Srp6Math.SessionImplicitKey(validKey, validKey, negativeKey, validKey));
        Assert.Throws<ArgumentOutOfRangeException>(() => Srp6Math.SessionImplicitKey(validKey, validKey, validKey, negativeKey));

        Assert.Throws<OverflowException>(() => Srp6Math.Scrambler(negativeKey, validKey));
        Assert.Throws<OverflowException>(() => Srp6Math.Scrambler(validKey, negativeKey));
        Assert.Throws<ArgumentException>(() => Srp6Math.Scrambler(overwideKey, validKey));
        Assert.Throws<ArgumentException>(() => Srp6Math.Scrambler(validKey, overwideKey));

        Assert.Throws<OverflowException>(() => Srp6Math.ClientProof("USER", dummySalt, negativeKey, validKey, dummyK));
        Assert.Throws<OverflowException>(() => Srp6Math.ClientProof("USER", dummySalt, validKey, negativeKey, dummyK));
        Assert.Throws<ArgumentException>(() => Srp6Math.ClientProof("USER", dummySalt, overwideKey, validKey, dummyK));
        Assert.Throws<ArgumentException>(() => Srp6Math.ClientProof("USER", dummySalt, validKey, overwideKey, dummyK));

        Assert.Throws<OverflowException>(() => Srp6Math.ServerProof(negativeKey, dummyM1, dummyK));
        Assert.Throws<ArgumentException>(() => Srp6Math.ServerProof(overwideKey, dummyM1, dummyK));
    }

    [Fact]
    public void Sha1_And_WowSrp6_ThrowWhenPassedNullChunkArraysOrStrings()
    {
        Assert.Throws<NullReferenceException>(() => Sha1.Hash(null!));
        Assert.Throws<NullReferenceException>(() => Sha1.Hash([null!]));
        Assert.Throws<NullReferenceException>(() => WowSrp6.CredentialsHash(null!, "PASS"));
        Assert.Throws<NullReferenceException>(() => WowSrp6.CredentialsHash("USER", null!));
        Assert.Throws<NullReferenceException>(() => WowSrp6.ComputeX(null!, "USER", "PASS"));
        Assert.Throws<NullReferenceException>(() => WowSrp6.ComputeVerifier(null!, "USER", "PASS"));
        Assert.Throws<NullReferenceException>(() => WowSrp6.CreateCredentials(null!, "PASS"));
        Assert.Throws<NullReferenceException>(() => WowSrp6.CreateCredentials("USER", null!));
    }

    // -------------------------------------------------------------------------
    // 3. ClientIntegrity exception & boundary tests
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 20)]
    [InlineData(8, 20)]
    [InlineData(15, 20)]
    [InlineData(17, 20)]
    [InlineData(20, 20)]
    [InlineData(24, 20)]
    [InlineData(31, 20)]
    [InlineData(33, 20)]
    [InlineData(64, 20)]
    [InlineData(16, 0)]
    [InlineData(16, 1)]
    [InlineData(16, 16)]
    [InlineData(16, 19)]
    [InlineData(16, 21)]
    [InlineData(16, 32)]
    [InlineData(32, 0)]
    [InlineData(32, 1)]
    [InlineData(32, 16)]
    [InlineData(32, 19)]
    [InlineData(32, 21)]
    [InlineData(32, 32)]
    [InlineData(0, 0)]
    public void ClientIntegrity_VersionProof_ThrowsArgumentException_OnInvalidLengths(int publicKeyLen, int hashLen)
    {
        byte[] pubKey = new byte[publicKeyLen];
        byte[] cfgHash = new byte[hashLen];
        Assert.Throws<ArgumentException>(() => ClientIntegrity.VersionProof(pubKey, cfgHash));
    }

    [Fact]
    public void ClientIntegrity_VersionProof_AcceptsBoth16And32BytePublicKeys()
    {
        byte[] key16 = new byte[16];
        byte[] key32 = new byte[32];
        byte[] cfgHash = new byte[20];
        RandomNumberGenerator.Fill(key16);
        RandomNumberGenerator.Fill(key32);
        RandomNumberGenerator.Fill(cfgHash);

        byte[] proof16 = ClientIntegrity.VersionProof(key16, cfgHash);
        byte[] proof32 = ClientIntegrity.VersionProof(key32, cfgHash);

        Assert.Equal(Sha1.DigestLength, proof16.Length);
        Assert.Equal(Sha1.DigestLength, proof32.Length);
        Assert.Equal(Sha1.Hash(key16, cfgHash), proof16);
        Assert.Equal(Sha1.Hash(key32, cfgHash), proof32);
    }

    // -------------------------------------------------------------------------
    // 4. PinHash exception & boundary tests
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 16, 16)]
    [InlineData(1, 16, 16)]
    [InlineData(2, 16, 16)]
    [InlineData(3, 16, 16)]
    [InlineData(11, 16, 16)]
    [InlineData(12, 16, 16)]
    [InlineData(20, 16, 16)]
    [InlineData(4, 0, 16)]
    [InlineData(4, 15, 16)]
    [InlineData(4, 17, 16)]
    [InlineData(4, 32, 16)]
    [InlineData(4, 16, 0)]
    [InlineData(4, 16, 15)]
    [InlineData(4, 16, 17)]
    [InlineData(4, 16, 32)]
    [InlineData(10, 0, 0)]
    public void PinHash_Calculate_And_Verify_ThrowArgumentException_OnInvalidPinOrSaltLengths(
        int digitsLen, int serverSaltLen, int clientSaltLen)
    {
        byte[] digits = new byte[digitsLen];
        byte[] serverSalt = new byte[serverSaltLen];
        byte[] clientSalt = new byte[clientSaltLen];
        byte[] hash20 = new byte[PinHash.HashLength];

        Assert.Throws<ArgumentException>(() => PinHash.Calculate(digits, 12345u, serverSalt, clientSalt));
        Assert.Throws<ArgumentException>(() => PinHash.Verify(digits, 12345u, serverSalt, clientSalt, hash20));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 11)]
    [InlineData(2, 48)]
    [InlineData(3, 57)]
    [InlineData(0, 127)]
    [InlineData(3, 255)]
    public void PinHash_Calculate_And_Verify_ThrowArgumentException_WhenPinContainsNonDigit(int badIndex, byte badValue)
    {
        byte[] digits = [1, 2, 3, 4];
        digits[badIndex] = badValue;
        byte[] serverSalt = new byte[PinHash.SaltLength];
        byte[] clientSalt = new byte[PinHash.SaltLength];
        byte[] hash20 = new byte[PinHash.HashLength];

        ArgumentException calcEx = Assert.Throws<ArgumentException>(() => PinHash.Calculate(digits, 99u, serverSalt, clientSalt));
        Assert.Equal("digits", calcEx.ParamName);

        ArgumentException verifyEx = Assert.Throws<ArgumentException>(() => PinHash.Verify(digits, 99u, serverSalt, clientSalt, hash20));
        Assert.Equal("digits", verifyEx.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(19)]
    [InlineData(21)]
    [InlineData(32)]
    public void PinHash_Verify_ReturnsFalseWithoutThrowing_WhenHashLengthIsNot20(int badHashLen)
    {
        byte[] digits = [1, 2, 3, 4];
        byte[] serverSalt = new byte[PinHash.SaltLength];
        byte[] clientSalt = new byte[PinHash.SaltLength];
        byte[] badHash = new byte[badHashLen];

        Assert.False(PinHash.Verify(digits, 0u, serverSalt, clientSalt, badHash));
    }

    // -------------------------------------------------------------------------
    // 5. Totp exception & boundary tests
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(-1L, 0)]
    [InlineData(-30L, 0)]
    [InlineData(-1L, 100)]
    [InlineData(long.MinValue, 0)]
    [InlineData(0L, -1)]
    [InlineData(29L, -1)]
    [InlineData(30L, -2)]
    [InlineData(59L, -2)]
    [InlineData(60L, -3)]
    [InlineData(89L, -3)]
    [InlineData(300L, -11)]
    public void Totp_Generate_ThrowsArgumentOutOfRangeException_WhenUnixSecondsOrCounterIsNegative(long unixSeconds, int offset)
    {
        byte[] key = "12345678901234567890"u8.ToArray();
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => Totp.Generate(key, unixSeconds, offset));
        Assert.Equal("unixSeconds", ex.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("AB")]
    [InlineData("AD")]
    [InlineData("AF")]
    [InlineData("AAAAB")]
    [InlineData("GEZDGNBV0")]
    [InlineData("GEZDGNBV1")]
    [InlineData("GEZDGNBV8")]
    [InlineData("GEZDGNBV9")]
    [InlineData("GEZDGNBV=")]
    [InlineData("GEZD GNBV")]
    [InlineData("GEZD+GNBV")]
    public void Totp_TryDecodeSecret_RejectsMalformedOrInvalidBase32Secrets(string? badSecret)
    {
        Assert.False(Totp.TryDecodeSecret(badSecret!, out byte[] key));
        Assert.Empty(key);
    }

    [Fact]
    public void Totp_TryDecodeSecret_EnforcesMaxLength103And64DecodedBytes()
    {
        Assert.True(Totp.TryDecodeSecret("AE", out byte[] key1));
        Assert.Equal([1], key1);

        string maxValid = new('A', 103);
        Assert.True(Totp.TryDecodeSecret(maxValid, out byte[] key64));
        Assert.Equal(64, key64.Length);
        Assert.All(key64, b => Assert.Equal(0, b));

        string maxWithNonZeroLeftover = new string('A', 102) + "B";
        Assert.False(Totp.TryDecodeSecret(maxWithNonZeroLeftover, out byte[] rejectedLeftover));
        Assert.Empty(rejectedLeftover);

        string overMax = new('A', 104);
        Assert.False(Totp.TryDecodeSecret(overMax, out byte[] rejectedLen));
        Assert.Empty(rejectedLen);
    }

    // -------------------------------------------------------------------------
    // 6. Deterministic seeded fuzz tests (Seeds: 1, 42, 20251004)
    // -------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_Srp6FullHandshake_And_AdversarialMutations(int seed)
    {
        var rng = new Random(seed);

        for (int i = 0; i < 64; i++)
        {
            string username = RandomCredentialString(rng, 1, 20);
            string password = RandomCredentialString(rng, 1, 24);

            (byte[] salt, byte[] verifierBytes) = WowSrp6.CreateCredentials(username, password);
            Assert.Equal(WowSrp6.SaltLength, salt.Length);
            Assert.Equal(WowSrp6.KeyLength, verifierBytes.Length);
            Assert.True(Srp6Validation.IsUsableSalt(salt));

            BigInteger verifier = WowSrp6.FromLittleEndian(verifierBytes);
            Assert.True(Srp6Validation.IsUsableVerifier(verifier));
            Assert.Equal(WowSrp6.ComputeVerifier(salt, username.ToLowerInvariant(), password.ToLowerInvariant()), verifier);

            byte[] privBBytes = new byte[rng.Next(1, 32)];
            rng.NextBytes(privBBytes);
            privBBytes[^1] |= 1;
            BigInteger privB = WowSrp6.FromLittleEndian(privBBytes);

            byte[] privABytes = new byte[rng.Next(1, 32)];
            rng.NextBytes(privABytes);
            privABytes[^1] |= 1;
            BigInteger privA = WowSrp6.FromLittleEndian(privABytes);

            var server = new Srp6Server(salt, verifier, privB);
            byte[] serverPublicB = server.PublicEphemeral;
            Assert.Equal(WowSrp6.KeyLength, serverPublicB.Length);

            BigInteger bigA = BigInteger.ModPow(WowSrp6.G, privA, WowSrp6.N);
            byte[] clientPublicA = WowSrp6.ToFixedLittleEndian(bigA, WowSrp6.KeyLength);
            BigInteger bigB = WowSrp6.FromLittleEndian(serverPublicB);
            BigInteger x = WowSrp6.ComputeX(salt, username, password);
            BigInteger u = Srp6Math.Scrambler(bigA, bigB);
            BigInteger kgx = WowSrp6.Multiplier * BigInteger.ModPow(WowSrp6.G, x, WowSrp6.N) % WowSrp6.N;
            BigInteger baseNum = ((bigB - kgx) % WowSrp6.N + WowSrp6.N) % WowSrp6.N;
            BigInteger clientS = BigInteger.ModPow(baseNum, privA + u * x, WowSrp6.N);
            byte[] clientK = Srp6Math.Interleave(clientS);
            byte[] clientM1 = Srp6Math.ClientProof(username, salt, bigA, bigB, clientK);
            byte[] expectedM2 = Srp6Math.ServerProof(bigA, clientM1, clientK);

            byte[] badM1 = (byte[])clientM1.Clone();
            badM1[rng.Next(badM1.Length)] ^= (byte)(1 << rng.Next(8));
            Assert.False(server.TryAcceptProof(username, clientPublicA, badM1));
            Assert.Null(server.SessionKey);
            Assert.Null(server.ServerProof);

            Assert.False(server.TryAcceptProof(username + "_WRONG", clientPublicA, clientM1));
            Assert.Null(server.SessionKey);

            Assert.True(server.TryAcceptProof(username.ToLowerInvariant(), clientPublicA, clientM1));
            Assert.Equal(clientK, server.SessionKey);
            Assert.Equal(expectedM2, server.ServerProof);

            using InvariantCapture capture = Invariant.Capture();
            Assert.False(server.TryAcceptProof(username, clientPublicA, clientM1));
            Assert.Single(capture.Failures);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_Srp6Math_Interleave_LeadingZerosAndRandomSecrets(int seed)
    {
        var rng = new Random(seed);

        for (int zeroPrefixLen = 0; zeroPrefixLen <= WowSrp6.KeyLength; zeroPrefixLen++)
        {
            byte[] sBytes = new byte[WowSrp6.KeyLength];
            for (int j = zeroPrefixLen; j < WowSrp6.KeyLength; j++)
            {
                byte b = (byte)rng.Next(1, 256);
                sBytes[j] = b;
            }

            BigInteger s = WowSrp6.FromLittleEndian(sBytes);
            byte[] actualK = Srp6Math.Interleave(s);
            Assert.Equal(WowSrp6.SessionKeyLength, actualK.Length);

            int evenStripped = (zeroPrefixLen & 1) != 0 ? zeroPrefixLen + 1 : zeroPrefixLen;
            int remaining = WowSrp6.KeyLength - evenStripped;
            int half = remaining / 2;
            byte[] even = new byte[half];
            byte[] odd = new byte[half];
            for (int k = 0; k < half; k++)
            {
                even[k] = sBytes[evenStripped + k * 2];
                odd[k] = sBytes[evenStripped + k * 2 + 1];
            }

            byte[] hEven = SHA1.HashData(even);
            byte[] hOdd = SHA1.HashData(odd);
            byte[] expectedK = new byte[WowSrp6.SessionKeyLength];
            for (int k = 0; k < Sha1.DigestLength; k++)
            {
                expectedK[k * 2] = hEven[k];
                expectedK[k * 2 + 1] = hOdd[k];
            }

            Assert.Equal(expectedK, actualK);
        }

        for (int i = 0; i < 128; i++)
        {
            byte[] randomBytes = new byte[rng.Next(0, WowSrp6.KeyLength + 1)];
            rng.NextBytes(randomBytes);
            BigInteger s = WowSrp6.FromLittleEndian(randomBytes);
            byte[] key = Srp6Math.Interleave(s);
            Assert.Equal(WowSrp6.SessionKeyLength, key.Length);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_WowSrp6_BigIntegerLittleEndianRoundTrips_And_ModularArithmetic(int seed)
    {
        var rng = new Random(seed);

        Assert.Empty(WowSrp6.ToMinimalLittleEndian(BigInteger.Zero));
        Assert.Equal(BigInteger.Zero, WowSrp6.FromLittleEndian([]));
        Assert.Equal(BigInteger.Zero, WowSrp6.FromLittleEndian(new byte[64]));

        for (int i = 0; i < 200; i++)
        {
            int rawLen = rng.Next(0, 65);
            byte[] raw = new byte[rawLen];
            rng.NextBytes(raw);

            BigInteger val = WowSrp6.FromLittleEndian(raw);
            Assert.True(val.Sign >= 0);

            byte[] minimal = WowSrp6.ToMinimalLittleEndian(val);
            if (val.IsZero)
            {
                Assert.Empty(minimal);
            }
            else
            {
                Assert.NotEqual(0, minimal[^1]);
                Assert.True(minimal.Length <= rawLen);
            }

            Assert.Equal(val, WowSrp6.FromLittleEndian(minimal));

            int fixedLen = minimal.Length + rng.Next(0, 16);
            byte[] fixedBytes = WowSrp6.ToFixedLittleEndian(val, fixedLen);
            Assert.Equal(fixedLen, fixedBytes.Length);
            Assert.Equal(val, WowSrp6.FromLittleEndian(fixedBytes));

            if (minimal.Length > 0)
            {
                int tooShort = rng.Next(0, minimal.Length);
                Assert.Throws<ArgumentException>(() => WowSrp6.ToFixedLittleEndian(val, tooShort));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_Sha1_And_ClientIntegrity_EquivalenceAndDifferentialChecks(int seed)
    {
        var rng = new Random(seed);

        Assert.Equal(SHA1.HashData([]), Sha1.Hash());

        for (int i = 0; i < 128; i++)
        {
            int chunkCount = rng.Next(0, 12);
            byte[][] chunks = new byte[chunkCount][];
            int totalLen = 0;
            for (int c = 0; c < chunkCount; c++)
            {
                chunks[c] = new byte[rng.Next(0, 128)];
                rng.NextBytes(chunks[c]);
                totalLen += chunks[c].Length;
            }

            byte[] combined = new byte[totalLen];
            int offset = 0;
            for (int c = 0; c < chunkCount; c++)
            {
                Buffer.BlockCopy(chunks[c], 0, combined, offset, chunks[c].Length);
                offset += chunks[c].Length;
            }

            Assert.Equal(SHA1.HashData(combined), Sha1.Hash(chunks));

            byte[] files = new byte[rng.Next(0, 256)];
            byte[] checksumSalt = new byte[rng.Next(0, 64)];
            byte[] clientKey = new byte[rng.Next(0, 64)];
            rng.NextBytes(files);
            rng.NextBytes(checksumSalt);
            rng.NextBytes(clientKey);

            byte[] expectedHmac = HMACSHA1.HashData(checksumSalt, files);
            byte[] expectedGeneric = Sha1.Hash(clientKey, expectedHmac);
            Assert.Equal(expectedGeneric, ClientIntegrity.GenericCheck(files, checksumSalt, clientKey));
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_PinHash_CalculateAndVerify_RoundTripAndTamperRejection(int seed)
    {
        var rng = new Random(seed);

        for (int i = 0; i < 128; i++)
        {
            int pinLen = rng.Next(4, 11);
            byte[] digits = new byte[pinLen];
            for (int d = 0; d < pinLen; d++)
            {
                digits[d] = (byte)rng.Next(0, 10);
            }

            uint gridSeed = (uint)rng.NextInt64(0, (long)uint.MaxValue + 1);
            byte[] serverSalt = new byte[PinHash.SaltLength];
            byte[] clientSalt = new byte[PinHash.SaltLength];
            rng.NextBytes(serverSalt);
            rng.NextBytes(clientSalt);

            byte[] mac = PinHash.Calculate(digits, gridSeed, serverSalt, clientSalt);
            Assert.Equal(PinHash.HashLength, mac.Length);
            Assert.True(PinHash.Verify(digits, gridSeed, serverSalt, clientSalt, mac));

            byte[] tamperedMac = (byte[])mac.Clone();
            tamperedMac[rng.Next(tamperedMac.Length)] ^= (byte)(1 << rng.Next(8));
            Assert.False(PinHash.Verify(digits, gridSeed, serverSalt, clientSalt, tamperedMac));

            byte[] tamperedDigits = (byte[])digits.Clone();
            int digitIdx = rng.Next(pinLen);
            tamperedDigits[digitIdx] = (byte)((tamperedDigits[digitIdx] + rng.Next(1, 10)) % 10);
            Assert.False(PinHash.Verify(tamperedDigits, gridSeed, serverSalt, clientSalt, mac));

            byte[] tamperedServerSalt = (byte[])serverSalt.Clone();
            tamperedServerSalt[rng.Next(PinHash.SaltLength)] ^= 0x80;
            Assert.False(PinHash.Verify(digits, gridSeed, tamperedServerSalt, clientSalt, mac));

            byte[] tamperedClientSalt = (byte[])clientSalt.Clone();
            tamperedClientSalt[rng.Next(PinHash.SaltLength)] ^= 0x80;
            Assert.False(PinHash.Verify(digits, gridSeed, serverSalt, tamperedClientSalt, mac));
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fuzz_Totp_Base32SecretDecode_And_Rfc6238WindowEquivalence(int seed)
    {
        var rng = new Random(seed);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        byte[] msgBuf = new byte[8];

        for (int i = 0; i < 128; i++)
        {
            string generated = Totp.NewSecret();
            Assert.Equal(32, generated.Length);
            Assert.True(Totp.TryDecodeSecret(generated, out byte[] decoded));
            Assert.Equal(20, decoded.Length);

            Assert.True(Totp.TryDecodeSecret(generated.ToLowerInvariant(), out byte[] decodedLower));
            Assert.Equal(decoded, decodedLower);

            int keyLen = rng.Next(1, 65);
            byte[] rawKey = new byte[keyLen];
            rng.NextBytes(rawKey);
            string encoded = EncodeBase32Unpadded(rawKey, alphabet);
            Assert.True(encoded.Length <= 103);
            Assert.True(Totp.TryDecodeSecret(encoded, out byte[] roundTripKey));
            Assert.Equal(rawKey, roundTripKey);

            long unixSeconds = rng.NextInt64(60, 4_000_000_000L);
            for (int offset = -2; offset <= 2; offset++)
            {
                uint withOffset = Totp.Generate(rawKey, unixSeconds, offset);
                uint shiftedTime = Totp.Generate(rawKey, unixSeconds + offset * 30L, 0);
                Assert.InRange(withOffset, 0u, 999_999u);
                Assert.Equal(shiftedTime, withOffset);

                ulong counter = (ulong)(unixSeconds / 30 + offset);
                BinaryPrimitives.WriteUInt64BigEndian(msgBuf, counter);
                byte[] hmac = HMACSHA1.HashData(rawKey, msgBuf);
                int pos = hmac[^1] & 0x0F;
                uint expectedCode = (BinaryPrimitives.ReadUInt32BigEndian(hmac.AsSpan(pos, 4)) & 0x7FFFFFFFu) % 1_000_000u;
                Assert.Equal(expectedCode, withOffset);
            }
        }
    }

    private static string RandomCredentialString(Random rng, int minLen, int maxLen)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-!@#$%^&*()";
        int len = rng.Next(minLen, maxLen + 1);
        char[] buf = new char[len];
        for (int i = 0; i < len; i++)
        {
            buf[i] = chars[rng.Next(chars.Length)];
        }

        return new string(buf);
    }

    private static string EncodeBase32Unpadded(ReadOnlySpan<byte> data, string alphabet)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int value = 0;
        int bits = 0;
        for (int i = 0; i < data.Length; i++)
        {
            value = (value << 8) | data[i];
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(alphabet[(value >> bits) & 31]);
                value &= (1 << bits) - 1;
            }
        }

        if (bits > 0)
        {
            sb.Append(alphabet[(value << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }
}
