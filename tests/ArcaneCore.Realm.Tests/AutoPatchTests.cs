using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Realm.Net;
using ArcaneCore.Realm.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Realm.Tests;

public sealed class AutoPatchTests : IDisposable
{
    private const string Username = "TESTER";
    private readonly string _dir = Directory.CreateTempSubdirectory("ac-patch").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Catalog_IsOffByDefault_AndHonoursEntriesPatternAndFolderBounds()
    {
        File.WriteAllBytes(Path.Combine(_dir, "5464enUS.mpq"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_dir, "any.mpq"), [4, 5]);
        var options = new AutoPatchOptions { Directory = _dir };
        var catalog = new PatchCatalog(() => options);
        Assert.Null(catalog.Find(5464, "enUS"));

        options.Enabled = true; // read per call: no rebuild needed
        ClientPatch? byPattern = catalog.Find(5464, "enUS");
        Assert.NotNull(byPattern);
        Assert.Equal(3, byPattern.Size);
        Assert.Equal(MD5.HashData(new byte[] { 1, 2, 3 }), byPattern.Md5);
        Assert.Null(catalog.Find(5464, "deDE"));

        options.Patches.Add(new AutoPatchEntry { Build = 5464, File = "any.mpq" });
        Assert.Equal(2, catalog.Find(5464, "deDE")!.Size);
        options.Patches.Add(new AutoPatchEntry { Build = 5302, Locale = "enUS", File = "../escape.mpq" });
        Assert.Null(catalog.Find(5302, "enUS"));
        options.FileNamePattern = string.Empty;
        Assert.Null(catalog.Find(4000, "enUS"));
    }

    [Fact]
    public async Task WrongBuild_WithPatch_GetsVersionUpdateXferInitAndResumableData()
    {
        byte[] data = RandomNumberGenerator.GetBytes(LogonSession.XferChunkSize + 100);
        File.WriteAllBytes(Path.Combine(_dir, "5464enUS.mpq"), data);
        var catalog = new PatchCatalog(() => new AutoPatchOptions { Enabled = true, Directory = _dir });
        var accounts = new InMemoryAccountStore();
        byte[] salt = WowSrp6.GenerateSalt();
        await accounts.CreateAsync(new Account
        {
            Username = Username, Salt = salt,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, Username, "X"), WowSrp6.KeyLength),
        });

        await using NetworkStream client = await StartAsync(accounts, catalog);
        await client.WriteAsync(Challenge(5464));
        byte[] head = await Read(client, 3);
        Assert.Equal(0, head[2]); // the challenge succeeds so the client sends a proof
        await Read(client, 32 + 1 + 1 + 1 + 32 + 32 + 16 + 1);

        byte[] proof = new byte[1 + 32 + 20 + 20 + 1 + 1];
        proof[0] = (byte)AuthCommand.LogonProof;
        proof[1] = 1;
        await client.WriteAsync(proof);
        byte[] offer = await Read(client, 2 + 1 + 1 + 5 + 8 + 16);
        Assert.Equal([0x01, 0x0A, 0x30, 5], offer[..4]);
        Assert.Equal("Patch", Encoding.ASCII.GetString(offer, 4, 5));
        Assert.Equal((ulong)data.Length, BinaryPrimitives.ReadUInt64LittleEndian(offer.AsSpan(9)));
        Assert.Equal(MD5.HashData(data), offer[17..]);

        // Resume after the first 4000 bytes and read until the rest has arrived.
        byte[] resume = new byte[9];
        resume[0] = (byte)AuthCommand.XferResume;
        BinaryPrimitives.WriteUInt64LittleEndian(resume.AsSpan(1), 4000);
        await client.WriteAsync(resume);
        var received = new List<byte>();
        while (received.Count < data.Length - 4000)
        {
            byte[] chunkHead = await Read(client, 3);
            Assert.Equal(0x31, chunkHead[0]);
            received.AddRange(await Read(client, BinaryPrimitives.ReadUInt16LittleEndian(chunkHead.AsSpan(1))));
        }

        Assert.Equal(data[4000..], received.ToArray());

        await client.WriteAsync(new byte[] { (byte)AuthCommand.XferCancel });
        Assert.Equal(0, await client.ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task WrongBuild_WithoutPatch_IsStillVersionInvalid()
    {
        var catalog = new PatchCatalog(() => new AutoPatchOptions { Enabled = true, Directory = _dir });
        await using NetworkStream client = await StartAsync(new InMemoryAccountStore(), catalog);
        await client.WriteAsync(Challenge(5464));
        byte[] head = await Read(client, 3);
        Assert.Equal((byte)AuthResult.VersionInvalid, head[2]);
    }

    [Fact]
    public async Task ActivePatchDownload_ExtendsTheSessionCap_ButAnIdleConnectionStillCloses()
    {
        byte[] data = RandomNumberGenerator.GetBytes(100);
        File.WriteAllBytes(Path.Combine(_dir, "5464enUS.mpq"), data);
        var catalog = new PatchCatalog(() => new AutoPatchOptions { Enabled = true, Directory = _dir });
        var accounts = new InMemoryAccountStore();
        byte[] salt = WowSrp6.GenerateSalt();
        await accounts.CreateAsync(new Account
        {
            Username = Username, Salt = salt,
            Verifier = WowSrp6.ToFixedLittleEndian(WowSrp6.ComputeVerifier(salt, Username, "X"), WowSrp6.KeyLength),
        });

        await using NetworkStream client = await StartAsync(accounts, catalog, new AuthOptions { MaxSessionDurationSeconds = 2 });
        await client.WriteAsync(Challenge(5464));
        await Read(client, 3 + 32 + 1 + 1 + 1 + 32 + 32 + 16 + 1);
        byte[] proof = new byte[1 + 32 + 20 + 20 + 1 + 1];
        proof[0] = (byte)AuthCommand.LogonProof;
        await client.WriteAsync(proof);
        await Read(client, 2 + 1 + 1 + 5 + 8 + 16);

        await Task.Delay(1500); // most of the 2 s cap is used before the download starts
        await client.WriteAsync(new byte[] { (byte)AuthCommand.XferAccept });
        byte[] chunkHead = await Read(client, 3);
        Assert.Equal(data, await Read(client, BinaryPrimitives.ReadUInt16LittleEndian(chunkHead.AsSpan(1))));

        await Task.Delay(1000); // past the original cap: the chunk restarted it, so the connection is still open
        await client.WriteAsync(new byte[] { (byte)AuthCommand.XferResume, 0, 0, 0, 0, 0, 0, 0, 0 });
        Assert.Equal(0x31, (await Read(client, 3))[0]);
        await Read(client, data.Length);

        // Idle afterwards: the cap closes the connection.
        Assert.Equal(0, await client.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static async Task<NetworkStream> StartAsync(IAccountStore accounts, PatchCatalog catalog, AuthOptions? options = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using TcpClient server = await listener.AcceptTcpClientAsync();
            listener.Stop();
            await using NetworkStream stream = server.GetStream();
            var session = new LogonSession(stream, accounts, new InMemoryRealmStore([]), options ?? new AuthOptions(),
                NullLogger.Instance, "test", patches: catalog);
            await session.RunAsync(CancellationToken.None);
        });
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client.GetStream();
    }

    private static byte[] Challenge(ushort build)
    {
        byte[] name = Encoding.ASCII.GetBytes(Username);
        var body = new List<byte>();
        body.AddRange("WoW\0"u8.ToArray());
        body.AddRange([1, 11, 2]);
        body.AddRange(BitConverter.GetBytes(build));
        body.AddRange("68x\0"u8.ToArray());
        body.AddRange("niW\0"u8.ToArray());
        body.AddRange("SUne"u8.ToArray());
        body.AddRange(new byte[8]);
        body.Add((byte)name.Length);
        body.AddRange(name);
        var packet = new List<byte> { (byte)AuthCommand.LogonChallenge, 0x08 };
        packet.AddRange(BitConverter.GetBytes((ushort)body.Count));
        packet.AddRange(body);
        return [.. packet];
    }

    private static async Task<byte[]> Read(NetworkStream stream, int count)
    {
        byte[] buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return buffer;
    }
}
