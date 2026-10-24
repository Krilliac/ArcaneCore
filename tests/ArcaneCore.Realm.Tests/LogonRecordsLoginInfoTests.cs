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
using ArcaneCore.Realm.Net;
using ArcaneCore.Realm.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Realm.Tests;

/// <summary>
/// A real <see cref="LogonSession"/> over loopback with a simulated 1.12.1 client: the client details of a proven logon
/// reach <see cref="IAccountLoginInfoStore"/>, and a store that throws never fails the logon.
/// </summary>
public sealed class LogonRecordsLoginInfoTests
{
    private const string Username = "TESTER";
    private const string Password = "SECRET";

    [Fact]
    public async Task ProvenLogon_RecordsOsPlatformLocaleBuildAndTime()
    {
        var accounts = new InMemoryAccountStore();
        byte[] salt = await SeedAsync(accounts);
        DateTime before = DateTime.UtcNow;

        await using NetworkStream client = await StartSessionAsync(accounts, accounts);
        Assert.Equal((byte)AuthResult.Success, await LogOnAsync(client, salt));

        AccountLoginInfo? info = await accounts.GetLoginInfoAsync(Username);
        Assert.NotNull(info);
        Assert.Equal("Win", info!.Os);
        Assert.Equal("x86", info.Platform);
        Assert.Equal("enUS", info.Locale);
        Assert.Equal(ClientBuild.Vanilla1121, info.Build);
        Assert.NotNull(info.LastLoginUtc);
        Assert.InRange(info.LastLoginUtc!.Value, before.AddSeconds(-5), DateTime.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task RejectedProof_RecordsNothing()
    {
        var accounts = new InMemoryAccountStore();
        byte[] salt = await SeedAsync(accounts);

        await using NetworkStream client = await StartSessionAsync(accounts, accounts);
        await client.WriteAsync(BuildChallenge(Username));
        (byte result, byte[] b) = await ReadChallengeAsync(client);
        Assert.Equal((byte)AuthResult.Success, result);
        await client.WriteAsync(BuildProof(ComputeClient(salt, b), wrongProof: true));
        byte[] reply = await ReadExactAsync(client, 2);
        Assert.NotEqual((byte)AuthResult.Success, reply[1]);

        Assert.Equal(new AccountLoginInfo(null, string.Empty, string.Empty, string.Empty, 0), await accounts.GetLoginInfoAsync(Username));
    }

    [Fact]
    public async Task RecordingFailure_StillLetsTheLogonSucceed()
    {
        var accounts = new InMemoryAccountStore();
        byte[] salt = await SeedAsync(accounts);
        var failing = new ThrowingLoginInfoStore();

        await using NetworkStream client = await StartSessionAsync(accounts, failing);
        Assert.Equal((byte)AuthResult.Success, await LogOnAsync(client, salt));
        Assert.Equal(1, failing.Calls);
    }

    private static async Task<byte[]> SeedAsync(InMemoryAccountStore accounts)
    {
        byte[] salt = WowSrp6.GenerateSalt();
        await accounts.CreateAsync(new Account
        {
            Username = Username,
            Salt = salt,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, Username, Password), WowSrp6.KeyLength),
        });
        return salt;
    }

    private static async Task<byte> LogOnAsync(NetworkStream client, byte[] salt)
    {
        await client.WriteAsync(BuildChallenge(Username));
        (byte result, byte[] b) = await ReadChallengeAsync(client);
        Assert.Equal((byte)AuthResult.Success, result);
        await client.WriteAsync(BuildProof(ComputeClient(salt, b)));
        byte[] reply = await ReadExactAsync(client, 2);
        return reply[1];
    }

    private static async Task<NetworkStream> StartSessionAsync(IAccountStore accounts, IAccountLoginInfoStore loginInfo)
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
                stream, accounts, new InMemoryRealmStore([]), new AuthOptions(), NullLogger.Instance, "test",
                loginInfoStore: loginInfo);
            await session.RunAsync(CancellationToken.None);
        });

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client.GetStream();
    }

    private static byte[] BuildChallenge(string username)
    {
        byte[] name = Encoding.ASCII.GetBytes(username);
        var body = new List<byte>();
        body.AddRange("WoW\0"u8.ToArray());
        body.AddRange([1, 12, 1]);
        body.AddRange(LeU16(ClientBuild.Vanilla1121));
        body.AddRange("68x\0"u8.ToArray());  // platform "x86", byte-reversed on the wire
        body.AddRange("niW\0"u8.ToArray());  // os "Win"
        body.AddRange("SUne"u8.ToArray());   // country "enUS"
        body.AddRange(LeU32(0));
        body.AddRange(LeU32(0));
        body.Add((byte)name.Length);
        body.AddRange(name);

        var packet = new List<byte> { (byte)AuthCommand.LogonChallenge, 0x08 };
        packet.AddRange(LeU16((ushort)body.Count));
        packet.AddRange(body);
        return [.. packet];
    }

    private static byte[] BuildProof(ClientProof client, bool wrongProof = false)
    {
        var packet = new List<byte> { (byte)AuthCommand.LogonProof };
        packet.AddRange(client.PublicKey);
        packet.AddRange(wrongProof ? new byte[20] : client.Proof);
        packet.AddRange(new byte[20]); // crc_hash
        packet.Add(0);                 // number_of_keys
        packet.Add(0);                 // security flags
        return [.. packet];
    }

    private static async Task<(byte Result, byte[] B)> ReadChallengeAsync(NetworkStream stream)
    {
        byte[] head = await ReadExactAsync(stream, 3);
        if (head[2] != (byte)AuthResult.Success)
        {
            return (head[2], []);
        }

        // B[32], g_len, g, N_len, N[32], salt[32], crc[16], securityFlag
        byte[] rest = await ReadExactAsync(stream, 32 + 1 + 1 + 1 + 32 + 32 + 16 + 1);
        return (head[2], rest[..32]);
    }

    private static ClientProof ComputeClient(byte[] salt, byte[] serverB)
    {
        Span<byte> aBytes = stackalloc byte[19];
        RandomNumberGenerator.Fill(aBytes);
        BigInteger a = new(aBytes, isUnsigned: true, isBigEndian: false);
        BigInteger publicA = BigInteger.ModPow(WowSrp6.G, a, WowSrp6.N);
        BigInteger b = WowSrp6.FromLittleEndian(serverB);

        BigInteger x = WowSrp6.ComputeX(salt, Username, Password);
        BigInteger u = Srp6Math.Scrambler(publicA, b);
        BigInteger kgx = WowSrp6.Multiplier * BigInteger.ModPow(WowSrp6.G, x, WowSrp6.N) % WowSrp6.N;
        BigInteger numerator = ((b - kgx) % WowSrp6.N + WowSrp6.N) % WowSrp6.N;
        BigInteger s = BigInteger.ModPow(numerator, a + u * x, WowSrp6.N);

        byte[] sessionKey = Srp6Math.Interleave(s);
        return new ClientProof(
            WowSrp6.ToFixedLittleEndian(publicA, WowSrp6.KeyLength),
            Srp6Math.ClientProof(Username, salt, publicA, b, sessionKey));
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    private static byte[] LeU16(ushort v) { byte[] b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); return b; }

    private static byte[] LeU32(uint v) { byte[] b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }

    private sealed record ClientProof(byte[] PublicKey, byte[] Proof);

    private sealed class ThrowingLoginInfoStore : IAccountLoginInfoStore
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task RecordLoginAsync(string username, AccountLoginInfo info, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("login info store is down");
        }

        public Task<AccountLoginInfo?> GetLoginInfoAsync(string username, CancellationToken cancellationToken = default)
            => Task.FromResult<AccountLoginInfo?>(null);
    }
}
