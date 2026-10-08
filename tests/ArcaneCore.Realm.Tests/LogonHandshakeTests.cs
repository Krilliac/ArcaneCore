using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Realm.Net;
using ArcaneCore.Realm.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Realm.Tests;

/// <summary>
/// Drives a real <see cref="LogonSession"/> over a loopback TCP socket with a simulated
/// 1.12.1 client, validating the full M1 wire exchange (challenge → proof → realm list)
/// byte-for-byte without a database or the real game client.
/// </summary>
public sealed class LogonHandshakeTests
{
    private const string Username = "TESTER";
    private const string Password = "SECRET";

    [Fact]
    public async Task ExistingAccount_CompletesHandshakeAndReceivesRealmList()
    {
        byte[] salt = WowSrp6.GenerateSalt();
        BigInteger verifier = WowSrp6.ComputeVerifier(salt, Username, Password);

        var accounts = new InMemoryAccountStore();
        await accounts.CreateAsync(new Account
        {
            Username = Username,
            Salt = salt,
            Verifier = WowSrp6.ToFixedLittleEndian(verifier, WowSrp6.KeyLength),
        });

        var realms = new InMemoryRealmStore([
            new RealmEntry { Name = "ArcaneCore", Address = "127.0.0.1:8085" },
        ]);

        await using NetworkStream client = await StartSessionAsync(accounts, realms, autocreate: false);

        // --- challenge ---
        await SendAsync(client, BuildChallenge(Username, Password));
        ChallengeReply challenge = await ReadChallengeReplyAsync(client);
        Assert.Equal((byte)AuthResult.Success, challenge.Result);

        // --- proof ---
        ClientSession session = ClientSession.Compute(Username, Password, salt, challenge.B);
        await SendAsync(client, BuildProof(session));
        ProofReply proof = await ReadProofReplyAsync(client);
        Assert.Equal((byte)AuthResult.Success, proof.Result);
        Assert.Equal(session.ExpectedServerProof(), proof.M2);

        // --- realm list ---
        await SendAsync(client, BuildRealmListRequest());
        RealmListReply list = await ReadRealmListReplyAsync(client);
        Assert.Equal(1, list.Count);
        Assert.Equal("ArcaneCore", list.FirstRealmName);
    }

    [Fact]
    public async Task UnknownAccount_AutocreatesWhenPasswordEqualsUsername()
    {
        var accounts = new InMemoryAccountStore();
        var realms = new InMemoryRealmStore([new RealmEntry { Name = "ArcaneCore", Address = "127.0.0.1:8085" }]);

        await using NetworkStream client = await StartSessionAsync(accounts, realms, autocreate: true);

        // WCell convention: register by logging in with username == password.
        await SendAsync(client, BuildChallenge("NEWBIE", "NEWBIE"));
        ChallengeReply challenge = await ReadChallengeReplyAsync(client);
        Assert.Equal((byte)AuthResult.Success, challenge.Result);

        ClientSession session = ClientSession.Compute("NEWBIE", "NEWBIE", challenge.Salt, challenge.B);
        await SendAsync(client, BuildProof(session));
        ProofReply proof = await ReadProofReplyAsync(client);

        Assert.Equal((byte)AuthResult.Success, proof.Result);
        Account? created = await accounts.FindByUsernameAsync("NEWBIE");
        Assert.NotNull(created);
        Assert.NotNull(created!.SessionKey);
    }

    [Fact]
    public async Task UnknownAccount_WithoutAutocreate_IsRejected()
    {
        var accounts = new InMemoryAccountStore();
        var realms = new InMemoryRealmStore([]);

        await using NetworkStream client = await StartSessionAsync(accounts, realms, autocreate: false);

        await SendAsync(client, BuildChallenge("GHOST", "GHOST"));
        ChallengeReply challenge = await ReadChallengeReplyAsync(client);

        Assert.Equal((byte)AuthResult.UnknownAccount, challenge.Result);
    }

    [Theory]
    [InlineData("123456", true)]
    [InlineData("654321", false)]
    [InlineData(null, false)]
    public async Task FixedPin_GridChallengeAndProof_AdmitOnlyCorrectPin(string? submitted, bool succeeds)
    {
        var accounts = new InMemoryAccountStore();
        byte[] salt = WowSrp6.GenerateSalt();
        await accounts.CreateAsync(new Account
        {
            Username = Username, Salt = salt,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, Username, Password), WowSrp6.KeyLength),
            LockFlags = AccountLockFlags.FixedPin | AccountLockFlags.AlwaysEnforce,
            SecurityInfo = "123456",
        });
        await using NetworkStream client = await StartSessionAsync(accounts, new InMemoryRealmStore([]), autocreate: false);
        await SendAsync(client, BuildChallenge(Username, Password));
        ChallengeReply challenge = await ReadChallengeReplyAsync(client);
        Assert.Equal((byte)1, challenge.SecurityFlag);
        Assert.Equal(16, challenge.PinSalt.Length);
        ClientSession session = ClientSession.Compute(Username, Password, salt, challenge.B);
        byte[]? pin = submitted is null ? null : BuildPinData(submitted, challenge);
        await SendAsync(client, BuildProof(session, pinData: pin));
        ProofReply proof = await ReadProofReplyAsync(client);
        Assert.Equal(succeeds ? (byte)AuthResult.Success : (byte)AuthResult.UnknownAccount, proof.Result);
        Assert.Equal(succeeds, (await accounts.FindByUsernameAsync(Username))!.SessionKey is not null);
    }

    [Fact]
    public async Task IpLock_MatchingAddressSkipsPin_ChangedAddressRequiresIt()
    {
        var accounts = new InMemoryAccountStore();
        byte[] salt = WowSrp6.GenerateSalt();
        await accounts.CreateAsync(new Account
        {
            Username = Username, Salt = salt,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, Username, Password), WowSrp6.KeyLength),
            LockFlags = AccountLockFlags.IpLock | AccountLockFlags.FixedPin,
            SecurityInfo = "123456", LastIp = "127.0.0.1",
        });
        await using NetworkStream same = await StartSessionAsync(accounts, new InMemoryRealmStore([]), false,
            endpoint: "127.0.0.1:50000");
        await SendAsync(same, BuildChallenge(Username, Password));
        Assert.Equal((byte)0, (await ReadChallengeReplyAsync(same)).SecurityFlag);
        await using NetworkStream moved = await StartSessionAsync(accounts, new InMemoryRealmStore([]), false,
            endpoint: "127.0.0.2:50000");
        await SendAsync(moved, BuildChallenge(Username, Password));
        Assert.Equal((byte)1, (await ReadChallengeReplyAsync(moved)).SecurityFlag);
    }

    [Fact]
    public async Task IpLock_PinLoginRecordsAddress_ThenThatAddressSkipsThePin()
    {
        // vmangos stores last_ip on every successful proof (AuthSocket.cpp UPDATE account SET ... last_ip)
        // and IP_LOCK compares against it at the next challenge. A leading-zero PIN keeps its digits.
        var accounts = new InMemoryAccountStore();
        byte[] salt = WowSrp6.GenerateSalt();
        await accounts.CreateAsync(new Account
        {
            Username = Username, Salt = salt,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, Username, Password), WowSrp6.KeyLength),
            LockFlags = AccountLockFlags.IpLock | AccountLockFlags.FixedPin, SecurityInfo = "0042",
        });
        await using (NetworkStream first = await StartSessionAsync(accounts, new InMemoryRealmStore([]), false,
            endpoint: "127.0.0.9:50000"))
        {
            await SendAsync(first, BuildChallenge(Username, Password));
            ChallengeReply challenge = await ReadChallengeReplyAsync(first);
            Assert.Equal((byte)1, challenge.SecurityFlag); // no trusted address yet
            ClientSession session = ClientSession.Compute(Username, Password, salt, challenge.B);
            await SendAsync(first, BuildProof(session, pinData: BuildPinData("0042", challenge)));
            Assert.Equal((byte)AuthResult.Success, (await ReadProofReplyAsync(first)).Result);
        }

        Assert.Equal("127.0.0.9", (await accounts.FindByUsernameAsync(Username))!.LastIp);
        await using NetworkStream again = await StartSessionAsync(accounts, new InMemoryRealmStore([]), false,
            endpoint: "127.0.0.9:50001");
        await SendAsync(again, BuildChallenge(Username, Password));
        Assert.Equal((byte)0, (await ReadChallengeReplyAsync(again)).SecurityFlag);
    }

    [Fact]
    public async Task IpLock_WithoutFactor_RefusesAMovedAddressAtChallenge()
    {
        // vmangos AuthSocket.cpp:416-424: IP-locked, address differs, no TOTP/FIXED_PIN -> WOW_FAIL_SUSPENDED.
        var accounts = new InMemoryAccountStore();
        byte[] salt = WowSrp6.GenerateSalt();
        await accounts.CreateAsync(new Account
        {
            Username = Username, Salt = salt,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, Username, Password), WowSrp6.KeyLength),
            LockFlags = AccountLockFlags.IpLock, LastIp = "127.0.0.1",
        });
        await using NetworkStream moved = await StartSessionAsync(accounts, new InMemoryRealmStore([]), false,
            endpoint: "127.0.0.2:50000");
        await SendAsync(moved, BuildChallenge(Username, Password));
        Assert.Equal((byte)AuthResult.Suspended, (await ReadChallengeReplyAsync(moved)).Result);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ClientIntegrity_DefaultOffOrCorrectHashSucceeds_WrongHashFails(bool strict, bool correct)
    {
        var accounts = new InMemoryAccountStore();
        byte[] salt = WowSrp6.GenerateSalt();
        await accounts.CreateAsync(new Account
        {
            Username = Username, Salt = salt,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, Username, Password), WowSrp6.KeyLength),
        });
        byte[] versionHash = Enumerable.Range(1, 20).Select(i => (byte)i).ToArray();
        var options = new AuthOptions
        {
            StrictVersionCheck = strict,
            IntegrityHashes = [new ClientIntegrityHashOptions
            {
                Build = ClientBuild.Vanilla1121, Os = "Win", Platform = "x86", Hash = Convert.ToHexString(versionHash),
            }],
        };
        await using NetworkStream client = await StartSessionAsync(accounts, new InMemoryRealmStore([]), false, options);
        await SendAsync(client, BuildChallenge(Username, Password));
        ChallengeReply challenge = await ReadChallengeReplyAsync(client);
        ClientSession session = ClientSession.Compute(Username, Password, salt, challenge.B);
        byte[] crc = correct ? ClientIntegrity.VersionProof(session.PublicKey, versionHash) : new byte[20];
        await SendAsync(client, BuildProof(session, crc: crc));
        Assert.Equal(!strict || correct ? (byte)AuthResult.Success : (byte)AuthResult.VersionInvalid,
            (await ReadProofReplyAsync(client)).Result);
    }

    [Fact]
    public async Task StrictIntegrity_ZeroHashDisablesComparison()
    {
        var accounts = new InMemoryAccountStore();
        byte[] salt = WowSrp6.GenerateSalt();
        await accounts.CreateAsync(new Account
        {
            Username = Username, Salt = salt,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, Username, Password), WowSrp6.KeyLength),
        });
        var options = new AuthOptions { StrictVersionCheck = true,
            IntegrityHashes = [new ClientIntegrityHashOptions { Build = ClientBuild.Vanilla1121,
                Os = "Win", Platform = "x86", Hash = new string('0', 40) }] };
        await using NetworkStream client = await StartSessionAsync(accounts, new InMemoryRealmStore([]), false, options);
        await SendAsync(client, BuildChallenge(Username, Password));
        ChallengeReply challenge = await ReadChallengeReplyAsync(client);
        ClientSession session = ClientSession.Compute(Username, Password, salt, challenge.B);
        await SendAsync(client, BuildProof(session));
        Assert.Equal((byte)AuthResult.Success, (await ReadProofReplyAsync(client)).Result);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, false)]
    public async Task Totp_PinGridUsesManualClockAndVmangosWindow(int offset, bool succeeds)
    {
        const string secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        Assert.True(Totp.TryDecodeSecret(secret, out byte[] key));
        var accounts = new InMemoryAccountStore();
        byte[] salt = WowSrp6.GenerateSalt();
        await accounts.CreateAsync(new Account
        {
            Username = Username, Salt = salt,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, Username, Password), WowSrp6.KeyLength),
            LockFlags = AccountLockFlags.Totp | AccountLockFlags.AlwaysEnforce, SecurityInfo = secret,
        });
        await using NetworkStream client = await StartSessionAsync(accounts, new InMemoryRealmStore([]), false,
            clock: new FrozenClock(59));
        await SendAsync(client, BuildChallenge(Username, Password));
        ChallengeReply challenge = await ReadChallengeReplyAsync(client);
        ClientSession session = ClientSession.Compute(Username, Password, salt, challenge.B);
        string code = Totp.Generate(key, 59, offset).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        await SendAsync(client, BuildProof(session, pinData: BuildPinData(code, challenge)));
        Assert.Equal(succeeds ? (byte)AuthResult.Success : (byte)AuthResult.UnknownAccount,
            (await ReadProofReplyAsync(client)).Result);
    }

    // --- session host ------------------------------------------------------------

    private static async Task<NetworkStream> StartSessionAsync(
        IAccountStore accounts, IRealmStore realms, bool autocreate, AuthOptions? options = null,
        string endpoint = "test", TimeProvider? clock = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            using TcpClient server = await listener.AcceptTcpClientAsync();
            listener.Stop();
            await using NetworkStream stream = server.GetStream();
            var session = new LogonSession(
                stream, accounts, realms,
                options ?? new AuthOptions { AutocreateAccounts = autocreate },
                NullLogger.Instance, endpoint, timeProvider: clock);
            await session.RunAsync(CancellationToken.None);
        });

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client.GetStream();
    }

    // --- request builders --------------------------------------------------------

    private static byte[] BuildChallenge(string username, string _)
    {
        byte[] name = Encoding.ASCII.GetBytes(username.ToUpperInvariant());
        var body = new List<byte>();
        body.AddRange("WoW\0"u8.ToArray());           // gamename[4]
        body.AddRange([1, 12, 1]);                     // version 1.12.1
        body.AddRange(LeU16(ClientBuild.Vanilla1121)); // build 5875
        body.AddRange("68x\0"u8.ToArray());            // platform[4]
        body.AddRange("niW\0"u8.ToArray());            // os[4]
        body.AddRange("SUne"u8.ToArray());             // country[4]
        body.AddRange(LeU32(0));                        // timezone
        body.AddRange(LeU32(0));                        // ip
        body.Add((byte)name.Length);                   // username length
        body.AddRange(name);

        var packet = new List<byte> { (byte)AuthCommand.LogonChallenge, 0x08 };
        packet.AddRange(LeU16((ushort)body.Count));
        packet.AddRange(body);
        return [.. packet];
    }

    private static byte[] BuildProof(ClientSession session, byte[]? crc = null, byte[]? pinData = null)
    {
        var packet = new List<byte> { (byte)AuthCommand.LogonProof };
        packet.AddRange(session.PublicKey);  // A[32]
        packet.AddRange(session.Proof);      // M1[20]
        packet.AddRange(crc ?? new byte[20]); // crc_hash[20]
        packet.Add(0);                       // number_of_keys
        packet.Add(pinData is null ? (byte)0 : (byte)1);
        if (pinData is not null) packet.AddRange(pinData);
        return [.. packet];
    }

    private static byte[] BuildPinData(string pin, ChallengeReply challenge)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = PinHash.Calculate(pin.Select(c => (byte)(c - '0')).ToArray(),
            challenge.GridSeed, challenge.PinSalt, salt);
        return [.. salt, .. hash];
    }

    private static byte[] BuildRealmListRequest()
    {
        var packet = new List<byte> { (byte)AuthCommand.RealmList };
        packet.AddRange(LeU32(0));
        return [.. packet];
    }

    // --- reply readers -----------------------------------------------------------

    private static async Task<ChallengeReply> ReadChallengeReplyAsync(NetworkStream stream)
    {
        byte[] head = await ReadExactAsync(stream, 3);
        if (head[2] != (byte)AuthResult.Success)
        {
            return new ChallengeReply { Result = head[2], B = [], Salt = [] };
        }

        // B[32], g_len(1), g(1), N_len(1), N[32], salt[32], crc[16], securityFlag(1)
        byte[] rest = await ReadExactAsync(stream, 32 + 1 + 1 + 1 + 32 + 32 + 16 + 1);
        byte[] b = rest[..32];
        byte[] salt = rest.AsSpan(32 + 1 + 1 + 1 + 32, 32).ToArray();
        byte flag = rest[^1];
        byte[] pin = flag == 1 ? await ReadExactAsync(stream, 20) : [];
        return new ChallengeReply { Result = head[2], B = b, Salt = salt,
            SecurityFlag = flag, GridSeed = pin.Length == 20 ? BinaryPrimitives.ReadUInt32LittleEndian(pin) : 0,
            PinSalt = pin.Length == 20 ? pin[4..] : [] };
    }

    private static async Task<ProofReply> ReadProofReplyAsync(NetworkStream stream)
    {
        byte[] head = await ReadExactAsync(stream, 2); // cmd, result
        if (head[1] != (byte)AuthResult.Success)
        {
            return new ProofReply { Result = head[1], M2 = [] };
        }

        byte[] rest = await ReadExactAsync(stream, 20 + 4); // M2[20], surveyId(4)
        return new ProofReply { Result = head[1], M2 = rest[..20] };
    }

    private static async Task<RealmListReply> ReadRealmListReplyAsync(NetworkStream stream)
    {
        byte[] head = await ReadExactAsync(stream, 3); // cmd, size(u16)
        ushort size = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(1, 2));
        byte[] body = await ReadExactAsync(stream, size);

        byte count = body[4]; // after uint32 unused
        int offset = 5;
        offset += 4 + 1; // realm type(4) + flags(1)
        int nameEnd = Array.IndexOf(body, (byte)0, offset);
        string name = Encoding.ASCII.GetString(body, offset, nameEnd - offset);
        return new RealmListReply { Count = count, FirstRealmName = name };
    }

    // --- helpers -----------------------------------------------------------------

    private static Task SendAsync(NetworkStream stream, byte[] data) => stream.WriteAsync(data).AsTask();

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    private static byte[] LeU16(ushort v) { byte[] b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); return b; }

    private static byte[] LeU32(uint v) { byte[] b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }

    private sealed class ChallengeReply
    {
        public required byte Result { get; init; }
        public required byte[] B { get; init; }
        public required byte[] Salt { get; init; }
        public byte SecurityFlag { get; init; }
        public uint GridSeed { get; init; }
        public byte[] PinSalt { get; init; } = [];
    }

    private sealed class ProofReply
    {
        public required byte Result { get; init; }
        public required byte[] M2 { get; init; }
    }

    private sealed class RealmListReply
    {
        public required byte Count { get; init; }
        public required string FirstRealmName { get; init; }
    }

    /// <summary>Standard SRP6 client math, mirroring the real client, for driving the server.</summary>
    private sealed class ClientSession
    {
        public required byte[] PublicKey { get; init; }
        public required byte[] Proof { get; init; }
        private BigInteger A { get; init; }
        private byte[] SessionKey { get; init; } = [];

        public static ClientSession Compute(string username, string password, byte[] salt, byte[] serverB)
        {
            Span<byte> aBytes = stackalloc byte[19];
            RandomNumberGenerator.Fill(aBytes);
            BigInteger a = new(aBytes, isUnsigned: true, isBigEndian: false);
            BigInteger publicA = BigInteger.ModPow(WowSrp6.G, a, WowSrp6.N);
            BigInteger b = WowSrp6.FromLittleEndian(serverB);

            BigInteger x = WowSrp6.ComputeX(salt, username, password);
            BigInteger u = Srp6Math.Scrambler(publicA, b);
            BigInteger kgx = WowSrp6.Multiplier * BigInteger.ModPow(WowSrp6.G, x, WowSrp6.N) % WowSrp6.N;
            BigInteger numerator = ((b - kgx) % WowSrp6.N + WowSrp6.N) % WowSrp6.N;
            BigInteger s = BigInteger.ModPow(numerator, a + u * x, WowSrp6.N);

            byte[] sessionKey = Srp6Math.Interleave(s);
            return new ClientSession
            {
                A = publicA,
                SessionKey = sessionKey,
                PublicKey = WowSrp6.ToFixedLittleEndian(publicA, WowSrp6.KeyLength),
                Proof = Srp6Math.ClientProof(username, salt, publicA, b, sessionKey),
            };
        }

        public byte[] ExpectedServerProof() => Srp6Math.ServerProof(A, Proof, SessionKey);
    }

    private sealed class FrozenClock(long unixSeconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }
}
