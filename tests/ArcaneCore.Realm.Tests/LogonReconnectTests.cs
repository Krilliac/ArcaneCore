using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ArcaneCore.Cryptography;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Realm.Net;
using ArcaneCore.Realm.Protocol;
using ArcaneCore.Realm.Tests.Bans;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Realm.Tests;

public sealed class LogonReconnectTests : IAsyncLifetime
{
    private readonly List<TcpClient> _clients = [];
    private readonly List<Task> _servers = [];
    private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(20));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (TcpClient client in _clients)
        {
            client.Dispose();
        }

        _lifetime.Cancel();
        try
        {
            await Task.WhenAll(_servers).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Cancellation bounds teardown after a failed assertion or a partial client frame.
        }
        finally
        {
            _lifetime.Dispose();
        }
    }
    [Fact]
    public async Task ReconnectChallengeProof_UsesStoredSessionKey_ThenAllowsRealmList()
    {
        byte[] key = Enumerable.Range(1, 40).Select(i => (byte)i).ToArray();
        var accounts = new InMemoryAccountStore();
        await accounts.CreateAsync(new Account { Username = "RECONNECT", Salt = [1], Verifier = [2], SessionKey = key });
        var realms = new InMemoryRealmStore([new RealmEntry { Name = "ArcaneCore", Address = "127.0.0.1:8085" }]);
        await using NetworkStream client = await StartAsync(accounts, realms);

        await client.WriteAsync(BuildChallenge("RECONNECT"));
        byte[] challenge = await ReadExactAsync(client, 34);
        Assert.Equal((byte)AuthCommand.ReconnectChallenge, challenge[0]);
        Assert.Equal((byte)AuthResult.Success, challenge[1]);
        byte[] reconnectChallenge = challenge[2..18];
        byte[] r1 = RandomNumberGenerator.GetBytes(16);
        byte[] proofInput = Encoding.ASCII.GetBytes("RECONNECT")
            .Concat(r1).Concat(reconnectChallenge).Concat(key).ToArray();
        byte[] packet = [(byte)AuthCommand.ReconnectProof, .. r1, .. SHA1.HashData(proofInput), .. new byte[20], 0];
        await client.WriteAsync(packet);
        byte[] proofResponse = await ReadExactAsync(client, 2);
        Assert.Equal(new byte[] { (byte)AuthCommand.ReconnectProof, (byte)AuthResult.Success }, proofResponse);

        await client.WriteAsync(new byte[] { (byte)AuthCommand.RealmList, 0, 0, 0, 0 });
        byte[] realmHeader = await ReadExactAsync(client, 3);
        ushort size = BinaryPrimitives.ReadUInt16LittleEndian(realmHeader.AsSpan(1, 2));
        Assert.True(size >= 5);
        await ReadExactAsync(client, size);
        await client.WriteAsync(packet); // the proof is one-shot and must not replay after authentication
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await ReadExactAsync(client, 2));
    }

    [Fact]
    public async Task ReconnectProofWithWrongSessionProof_IsRejected()
    {
        var accounts = new InMemoryAccountStore();
        await accounts.CreateAsync(new Account { Username = "RECONNECT", Salt = [1], Verifier = [2], SessionKey = Enumerable.Repeat((byte)7, 40).ToArray() });
        await using NetworkStream client = await StartAsync(accounts, new InMemoryRealmStore([]));
        await client.WriteAsync(BuildChallenge("RECONNECT"));
        byte[] challenge = await ReadExactAsync(client, 34);
        byte[] packet = [(byte)AuthCommand.ReconnectProof, .. new byte[16], .. new byte[20], .. new byte[20], 0];
        await client.WriteAsync(packet);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await ReadExactAsync(client, 2));
    }

    [Fact]
    public async Task ReconnectProofBeforeChallenge_IsClosed()
    {
        await using NetworkStream client = await StartAsync(new InMemoryAccountStore(), new InMemoryRealmStore([]));
        byte[] proof = [(byte)AuthCommand.ReconnectProof, .. new byte[57]];
        await client.WriteAsync(proof);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await ReadExactAsync(client, 2));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task StrictReconnectIntegrity_UsesR1AndZeroHash(bool correct, bool configured)
    {
        // vmangos VerifyVersion(isReconnect=true) skips the build-hash lookup entirely, so a reconnect is
        // judged against 20 zero bytes whether or not the client's tuple has a configured hash.
        byte[] key = Enumerable.Range(1, 40).Select(i => (byte)i).ToArray();
        var accounts = new InMemoryAccountStore();
        await accounts.CreateAsync(new Account { Username = "RECONNECT", Salt = [1], Verifier = [2], SessionKey = key });
        var options = new AuthOptions { StrictVersionCheck = true,
            IntegrityHashes = configured
                ? [new ClientIntegrityHashOptions { Build = 5875, Os = "Win", Platform = "x86",
                    Hash = Convert.ToHexString(Enumerable.Repeat((byte)9, 20).ToArray()) }]
                : [] };
        await using NetworkStream client = await StartAsync(accounts, new InMemoryRealmStore([]), options: options);
        await client.WriteAsync(BuildChallenge("RECONNECT"));
        byte[] challenge = await ReadExactAsync(client, 34);
        byte[] r1 = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        byte[] proofInput = Encoding.ASCII.GetBytes("RECONNECT")
            .Concat(r1).Concat(challenge[2..18]).Concat(key).ToArray();
        byte[] r3 = correct ? ClientIntegrity.VersionProof(r1, new byte[20]) : new byte[20];
        byte[] packet = [(byte)AuthCommand.ReconnectProof, .. r1, .. SHA1.HashData(proofInput), .. r3, 0];
        await client.WriteAsync(packet);
        Assert.Equal(correct ? (byte)AuthResult.Success : (byte)AuthResult.VersionInvalid,
            (await ReadExactAsync(client, 2))[1]);
    }

    [Theory]
    [InlineData(AccountStatus.Suspended, 0, AuthResult.Suspended)]
    [InlineData(AccountStatus.Banned, 40, AuthResult.Banned)]
    [InlineData(AccountStatus.Active, 0, AuthResult.UnknownAccount)]
    [InlineData(AccountStatus.Active, 39, AuthResult.UnknownAccount)]
    [InlineData(AccountStatus.Active, 41, AuthResult.UnknownAccount)]
    public async Task ReconnectChallenge_RejectsInactiveOrWrongLengthSessionKey(AccountStatus status, int keyLength, AuthResult expected)
    {
        var accounts = new InMemoryAccountStore();
        await accounts.CreateAsync(new Account { Username = "RECONNECT", Salt = [1], Verifier = [2], Status = status,
            SessionKey = keyLength == 0 ? null : Enumerable.Repeat((byte)7, keyLength).ToArray() });
        await using NetworkStream client = await StartAsync(accounts, new InMemoryRealmStore([]));
        await client.WriteAsync(BuildChallenge("RECONNECT"));
        byte[] response = await ReadExactAsync(client, 2);
        Assert.Equal(new byte[] { (byte)AuthCommand.ReconnectChallenge, (byte)expected }, response);
    }

    [Fact]
    public async Task ReconnectChallenge_RejectsActiveAccountAndIpBans()
    {
        var accounts = new InMemoryAccountStore();
        Account account = await accounts.CreateAsync(new Account { Username = "RECONNECT", Salt = [1], Verifier = [2], SessionKey = new byte[40] });
        var bans = new InMemoryBanStore();
        bans.AddAccountRow(account.Id, 1, 1);
        await using NetworkStream accountClient = await StartAsync(accounts, new InMemoryRealmStore([]), bans);
        await accountClient.WriteAsync(BuildChallenge("RECONNECT"));
        Assert.Equal((byte)AuthResult.Banned, (await ReadExactAsync(accountClient, 2))[1]);

        var ipAccounts = new InMemoryAccountStore();
        await ipAccounts.CreateAsync(new Account { Id = 18, Username = "RECONNECT", Salt = [1], Verifier = [2], SessionKey = new byte[40] });
        var ipBans = new InMemoryBanStore();
        ipBans.AddIpRow("127.0.0.1", 1, 1);
        await using NetworkStream ipClient = await StartAsync(ipAccounts, new InMemoryRealmStore([]), ipBans);
        await ipClient.WriteAsync(BuildChallenge("RECONNECT"));
        Assert.Equal((byte)AuthResult.FailNoAccess, (await ReadExactAsync(ipClient, 2))[1]);
    }

    private async Task<NetworkStream> StartAsync(IAccountStore accounts, IRealmStore realms, IBanStore? bans = null,
        AuthOptions? options = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        async Task RunServerAsync()
        {
            try
            {
                using TcpClient server = await listener.AcceptTcpClientAsync(_lifetime.Token);
                await using NetworkStream stream = server.GetStream();
                await new LogonSession(stream, accounts, realms, options ?? new AuthOptions(), NullLogger.Instance, "127.0.0.1:1", bans).RunAsync(_lifetime.Token);
            }
            finally
            {
                listener.Stop();
            }
        }

        _servers.Add(RunServerAsync());
        var client = new TcpClient();
        _clients.Add(client);
        await client.ConnectAsync(IPAddress.Loopback, port, _lifetime.Token);
        return client.GetStream();
    }

    private static byte[] BuildChallenge(string username)
    {
        byte[] name = Encoding.ASCII.GetBytes(username);
        var body = new List<byte>(30 + name.Length);
        body.AddRange("WoW\0"u8.ToArray()); body.AddRange([1, 12, 1]);
        byte[] build = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(build, ArcaneCore.Kernel.ClientBuild.Vanilla1121);
        body.AddRange(build); body.AddRange("68x\0"u8.ToArray());
        body.AddRange("niW\0"u8.ToArray()); body.AddRange("SUne"u8.ToArray());
        body.AddRange(new byte[8]); body.Add((byte)name.Length); body.AddRange(name);
        var packet = new List<byte> { (byte)AuthCommand.ReconnectChallenge, 0x08, (byte)body.Count, (byte)(body.Count >> 8) };
        packet.AddRange(body);
        return [.. packet];
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
    {
        byte[] result = new byte[count];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.ReadExactlyAsync(result, deadline.Token);
        return result;
    }
}
