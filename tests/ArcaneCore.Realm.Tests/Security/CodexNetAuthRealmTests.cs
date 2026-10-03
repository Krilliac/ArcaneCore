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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Realm.Tests.Security;

/// <summary>
/// Independent cross-verification of the logon-side half of the Codex net/auth findings, replaying
/// the sequences exactly as the report words them.
///
/// Finding 2: "On port 3724, send command 0x00 and a three-byte challenge header whose little-endian
/// size is 0xFFFF, then withhold the body; repeat across connections."
///
/// Every budget is far above the (instant or one second) behaviour being asserted.
/// </summary>
public sealed class CodexNetAuthRealmTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    // --- finding 2 -------------------------------------------------------------------------

    [Fact]
    public async Task ChallengeHeaderClaiming0xFFFF_WithTheBodyWithheld_IsClosedWithoutWaitingForTheBody()
    {
        // Default options: no read timeout is configured, so only the size window can end this.
        await using NetworkStream c = StartSession(new InMemoryAccountStore(), new AuthOptions());

        // command 0x00, protocol 0x08, size 0xFFFF little-endian, then nothing.
        await c.WriteAsync(new byte[] { 0x00, 0x08, 0xFF, 0xFF });

        Assert.True(await ReadsNothingThenClosesAsync(c), "the oversized claim must be dropped at once, not buffered until the session limit");
    }

    [Fact]
    public async Task StalledConnections_CannotExceedTheCap_AndTheirSlotsComeBack()
    {
        int port = FreePort();
        var services = new ServiceCollection();
        services.AddSingleton<IAccountStore>(new InMemoryAccountStore());
        services.AddSingleton<IRealmStore>(new InMemoryRealmStore([]));
        await using ServiceProvider provider = services.BuildServiceProvider();
        var options = new AuthOptions
        {
            BindAddress = "127.0.0.1", Port = port, MaxConnections = 2, ReadTimeoutSeconds = 1,
        };
        var server = new LogonServer(
            provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(options),
            NullLoggerFactory.Instance, NullLogger<LogonServer>.Instance);

        await server.StartAsync(CancellationToken.None);
        try
        {
            // Two attackers promise a 40 byte challenge body and withhold it; they hold both slots.
            using var a = new TcpClient();
            using var b = new TcpClient();
            await a.ConnectAsync(IPAddress.Loopback, port);
            await a.GetStream().WriteAsync(new byte[] { 0x00, 0x08, 40, 0 });
            await b.ConnectAsync(IPAddress.Loopback, port);
            await b.GetStream().WriteAsync(new byte[] { 0x00, 0x08, 40, 0 });

            // A real client is refused while they sit there: the server closes without a reply.
            using var refused = new TcpClient();
            await refused.ConnectAsync(IPAddress.Loopback, port);
            await refused.GetStream().WriteAsync(Challenge("NOBODY"));
            Assert.False(await ReceivesDataAsync(refused.GetStream()), "the third connection must be refused by the cap");

            // The read timeout cuts the stalled connections off ...
            Assert.True(await ReadsNothingThenClosesAsync(a.GetStream()));
            Assert.True(await ReadsNothingThenClosesAsync(b.GetStream()));

            // ... and a real client is then served (an unknown account still gets a reply).
            DateTime until = DateTime.UtcNow + Budget;
            bool served = false;
            while (!served && DateTime.UtcNow < until)
            {
                using var d = new TcpClient();
                await d.ConnectAsync(IPAddress.Loopback, port);
                await d.GetStream().WriteAsync(Challenge("NOBODY"));
                served = await ReceivesDataAsync(d.GetStream());
                if (!served)
                {
                    await Task.Delay(50); // the lease is released just after the socket closes
                }
            }

            Assert.True(served, "slots must be returned once the stalled connections time out");
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    // --- plumbing (the same SRP client as StaleSrpTakeoverTests, kept private to each file) ---

    internal static NetworkStream StartSession(IAccountStore accounts, AuthOptions options)
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
                options, NullLogger.Instance, "test");
            try { await session.RunAsync(CancellationToken.None); } catch (Exception) { /* connection closed */ }
        });
        var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        return client.GetStream();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    internal static byte[] Challenge(string username)
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

    internal static byte[] ProofFor(string proofUser, string credentialOwner, string password, byte[] salt, byte[] serverB)
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

    internal static async Task<(byte Result, byte[] B, byte[] Salt)> ReadChallenge(NetworkStream c)
    {
        byte[] head = await ReadN(c, 3);
        if (head[2] != 0)
        {
            return (head[2], [], []);
        }

        byte[] rest = await ReadN(c, 32 + 1 + 1 + 1 + 32 + 32 + 16 + 1);
        return (0, rest[..32], rest.AsSpan(67, 32).ToArray());
    }

    internal static async Task<byte[]> ReadN(NetworkStream c, int n)
    {
        byte[] buf = new byte[n];
        using var cts = new CancellationTokenSource(Budget);
        await c.ReadExactlyAsync(buf, cts.Token);
        return buf;
    }

    /// <summary>Reads until the server closes or goes quiet, so state changes have settled.</summary>
    internal static async Task DrainAsync(NetworkStream c)
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

    /// <summary>True if the server closes (or resets) without having sent a byte.</summary>
    internal static async Task<bool> ReadsNothingThenClosesAsync(NetworkStream c)
    {
        byte[] buf = new byte[16];
        using var cts = new CancellationTokenSource(Budget);
        try
        {
            return await c.ReadAsync(buf, cts.Token) == 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>True if at least one byte arrives within <paramref name="window"/> (default: the budget); false on close or silence.</summary>
    internal static async Task<bool> ReceivesDataAsync(NetworkStream c, TimeSpan? window = null)
    {
        byte[] one = new byte[1];
        using var cts = new CancellationTokenSource(window ?? Budget);
        try
        {
            return await c.ReadAsync(one, cts.Token) > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static byte[] U16(ushort v)
    {
        byte[] b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        return b;
    }
}
