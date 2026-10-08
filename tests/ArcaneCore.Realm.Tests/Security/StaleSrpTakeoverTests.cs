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

namespace ArcaneCore.Realm.Tests.Security;

/// <summary>
/// Security regression tests for the logon connection state (findings F1/F2/F3).
/// vmangos invalidates the connection status on handler entry (AuthSocket.cpp:327 and :555),
/// so a challenge/proof pair can never be mixed with another challenge's SRP state.
/// </summary>
public sealed class StaleSrpTakeoverTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    private static async Task<(InMemoryAccountStore Accounts, byte[] SaltA)> SeedAsync()
    {
        var accounts = new InMemoryAccountStore();
        byte[] saltA = WowSrp6.GenerateSalt();
        await accounts.CreateAsync(new Account
        {
            Username = "ALICE", Salt = saltA,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(saltA, "ALICE", "ALICEPW"), 32),
        });
        byte[] saltB = WowSrp6.GenerateSalt();
        await accounts.CreateAsync(new Account
        {
            Username = "BOB", Salt = saltB, Status = AccountStatus.Suspended,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(saltB, "BOB", "BOBPW"), 32),
        });
        return (accounts, saltA);
    }

    [Fact]
    public async Task SuspendedAccountSessionKeyCannotBeOverwrittenViaStaleSrp()
    {
        (InMemoryAccountStore accounts, byte[] saltA) = await SeedAsync();
        await using NetworkStream c = Start(accounts, autocreate: false);

        await c.WriteAsync(Challenge("ALICE"));
        (byte result, byte[] bA, _) = await ReadChallenge(c);
        Assert.Equal((byte)AuthResult.Success, result);

        await c.WriteAsync(Challenge("BOB"));
        (byte bobResult, _, _) = await ReadChallenge(c);
        Assert.Equal((byte)AuthResult.Suspended, bobResult);

        // Proof built from ALICE's password and salt but carrying BOB's name.
        await c.WriteAsync(ProofFor("BOB", "ALICE", "ALICEPW", saltA, bA));
        await DrainAsync(c);

        Account? bob = await accounts.FindByUsernameAsync("BOB");
        Assert.Null(bob!.SessionKey);
    }

    [Fact]
    public async Task StaleAutocreateFlagCannotCreateOrOverwriteAnExistingAccount()
    {
        (InMemoryAccountStore accounts, _) = await SeedAsync();
        await using NetworkStream c = Start(accounts, autocreate: true);

        await c.WriteAsync(Challenge("NEWBIE"));
        (byte result, byte[] b, byte[] salt) = await ReadChallenge(c);
        Assert.Equal((byte)AuthResult.Success, result);

        await c.WriteAsync(Challenge("BOB"));
        (byte bob, _, _) = await ReadChallenge(c);
        Assert.Equal((byte)AuthResult.Suspended, bob);

        await c.WriteAsync(ProofFor("BOB", "NEWBIE", "NEWBIE", salt, b));
        await DrainAsync(c);

        Account? stored = await accounts.FindByUsernameAsync("BOB");
        Assert.Null(stored!.SessionKey);
        Assert.Equal(AccountStatus.Suspended, stored.Status);
    }

    [Fact]
    public async Task OneProofPerChallenge_SecondProofDoesNotSucceed()
    {
        (InMemoryAccountStore accounts, byte[] saltA) = await SeedAsync();
        await using NetworkStream c = Start(accounts, autocreate: false);

        await c.WriteAsync(Challenge("ALICE"));
        (_, byte[] b, _) = await ReadChallenge(c);

        await c.WriteAsync(ProofFor("ALICE", "ALICE", "WRONGPW", saltA, b));
        byte[] first = await ReadN(c, 2); // build 5875 failure: command + result, no later-build padding
        Assert.Equal((byte)AuthCommand.LogonProof, first[0]);
        Assert.NotEqual((byte)AuthResult.Success, first[1]);

        // Retrying the correct password on the same challenge must not succeed.
        await c.WriteAsync(ProofFor("ALICE", "ALICE", "ALICEPW", saltA, b));
        await DrainAsync(c);
        Account? alice = await accounts.FindByUsernameAsync("ALICE");
        Assert.Null(alice!.SessionKey);
    }

    [Fact]
    public async Task ZeroVerifierAccount_IsRefusedAtChallenge()
    {
        var accounts = new InMemoryAccountStore();
        await accounts.CreateAsync(new Account
        {
            Username = "HOLLOW", Salt = WowSrp6.GenerateSalt(), Verifier = new byte[32],
        });
        await using NetworkStream c = Start(accounts, autocreate: false);

        await c.WriteAsync(Challenge("HOLLOW"));
        (byte result, _, _) = await ReadChallenge(c);
        Assert.NotEqual((byte)AuthResult.Success, result);
    }

    [Fact]
    public async Task RealmListCanBeRequestTwiceOnAnAuthenticatedConnection()
    {
        (InMemoryAccountStore accounts, byte[] saltA) = await SeedAsync();
        await using NetworkStream c = Start(accounts, autocreate: false);

        await c.WriteAsync(Challenge("ALICE"));
        (_, byte[] b, _) = await ReadChallenge(c);
        await c.WriteAsync(ProofFor("ALICE", "ALICE", "ALICEPW", saltA, b));
        byte[] head = await ReadN(c, 2);
        Assert.Equal((byte)AuthResult.Success, head[1]);
        await ReadN(c, 24);

        for (int i = 0; i < 2; i++)
        {
            await c.WriteAsync(new byte[] { (byte)AuthCommand.RealmList, 0, 0, 0, 0 });
            byte[] h = await ReadN(c, 3);
            Assert.Equal((byte)AuthCommand.RealmList, h[0]);
            await ReadN(c, BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(1)));
        }
    }

    // --- plumbing ----------------------------------------------------------------

    private static NetworkStream Start(IAccountStore accounts, bool autocreate)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using TcpClient server = await listener.AcceptTcpClientAsync();
            listener.Stop();
            await using NetworkStream s = server.GetStream();
            var session = new LogonSession(
                s, accounts,
                new InMemoryRealmStore([new RealmEntry { Name = "R", Address = "127.0.0.1:8085" }]),
                new AuthOptions { AutocreateAccounts = autocreate }, NullLogger.Instance, "test");
            try { await session.RunAsync(CancellationToken.None); } catch (Exception) { /* connection closed */ }
        });
        var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        return client.GetStream();
    }

    private static byte[] Challenge(string username)
    {
        byte[] name = Encoding.ASCII.GetBytes(username);
        var body = new List<byte>();
        body.AddRange("WoW\0"u8.ToArray());
        body.AddRange([1, 12, 1]);
        body.AddRange(U16(ClientBuild.Vanilla1121));
        body.AddRange("68x\0"u8.ToArray());
        body.AddRange("niW\0"u8.ToArray());
        body.AddRange("SUne"u8.ToArray());
        body.AddRange(new byte[8]);
        body.Add((byte)name.Length);
        body.AddRange(name);
        var packet = new List<byte> { (byte)AuthCommand.LogonChallenge, 0x08 };
        packet.AddRange(U16((ushort)body.Count));
        packet.AddRange(body);
        return [.. packet];
    }

    /// <summary>
    /// Builds a client proof. The SRP secret x is derived from <paramref name="credentialOwner"/>
    /// (the account whose salt/verifier the server really holds in its stale state) while the
    /// proof hash carries <paramref name="proofUser"/>.
    /// </summary>
    private static byte[] ProofFor(string proofUser, string credentialOwner, string password, byte[] salt, byte[] serverB)
    {
        Span<byte> aBytes = stackalloc byte[19];
        RandomNumberGenerator.Fill(aBytes);
        BigInteger a = new(aBytes, isUnsigned: true, isBigEndian: false);
        BigInteger publicA = BigInteger.ModPow(WowSrp6.G, a, WowSrp6.N);
        BigInteger b = WowSrp6.FromLittleEndian(serverB);
        BigInteger x = WowSrp6.ComputeX(salt, credentialOwner, password);
        BigInteger u = Srp6Math.Scrambler(publicA, b);
        BigInteger kgx = WowSrp6.Multiplier * BigInteger.ModPow(WowSrp6.G, x, WowSrp6.N) % WowSrp6.N;
        BigInteger s = BigInteger.ModPow(((b - kgx) % WowSrp6.N + WowSrp6.N) % WowSrp6.N, a + u * x, WowSrp6.N);
        byte[] k = Srp6Math.Interleave(s);
        byte[] m1 = Srp6Math.ClientProof(proofUser, salt, publicA, b, k);
        var p = new List<byte> { (byte)AuthCommand.LogonProof };
        p.AddRange(WowSrp6.ToFixedLittleEndian(publicA, 32));
        p.AddRange(m1);
        p.AddRange(new byte[22]);
        return [.. p];
    }

    private static async Task<(byte Result, byte[] B, byte[] Salt)> ReadChallenge(NetworkStream c)
    {
        byte[] head = await ReadN(c, 3);
        if (head[2] != 0)
        {
            return (head[2], [], []);
        }

        byte[] rest = await ReadN(c, 32 + 1 + 1 + 1 + 32 + 32 + 16 + 1);
        return (0, rest[..32], rest.AsSpan(67, 32).ToArray());
    }

    private static async Task<byte[]> ReadN(NetworkStream c, int n)
    {
        byte[] buf = new byte[n];
        using var cts = new CancellationTokenSource(Budget);
        await c.ReadExactlyAsync(buf, cts.Token);
        return buf;
    }

    /// <summary>Reads until the server closes or goes quiet, so state changes have settled.</summary>
    private static async Task DrainAsync(NetworkStream c)
    {
        byte[] buf = new byte[256];
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        try
        {
            while (await c.ReadAsync(buf, cts.Token) > 0)
            {
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static byte[] U16(ushort v)
    {
        byte[] b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        return b;
    }
}
