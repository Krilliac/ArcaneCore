using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.MockClient.Hosting;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.Protocol;
using ArcaneCore.World;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// S4d: a client whose character is saved on a map another worker owns logs in through the login worker, which answers with a SessionRedirect before
/// any world state exists; the gateway re-opens the session on the map owner with a SessionAdopt (account, new generation, the buffered login packet) and
/// closes it on the login worker. The wire frames are pinned with golden bytes (the existing frames must stay byte-identical), then the whole path runs
/// over loopback sockets with two real worlds on one set of databases.
/// </summary>
public sealed class LoginRedirectTests
{
    private const string Secret = "redirect-link-secret-0123456789";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);

    // Golden frames: written out by hand from the layout in GatewaySessionWire, not produced by the encoder under test.
    private const string GoldenOpen = "0B" + "0300000000000000" + "0100000000000000" + "02000000" + "F3160000" + "0100000041" + "0100000042";
    private const string GoldenClose = "0F" + "0800000000000000" + "09000000" + "06";
    private const string GoldenRedirect = "15" + "0500000000000000" + "07000000" + "01000000" + "00000000";
    private const string GoldenAdopt = "16" + "0900000000000000" + "0600000000000000" + "07000000" + "F3160000" + "0100000041" + "0100000042"
        + "3D000000" + "04030201" + "08000000" + "0102030405060708";

    [Fact]
    public void ExistingFrames_StayByteIdentical()
    {
        Assert.Equal(GoldenOpen, Convert.ToHexString(GatewaySessionWire.EncodeOpen(3, new GatewaySessionOpenRequest(1, 2, "A", 5875, "B"))));
        Assert.Equal(GoldenClose, Convert.ToHexString(GatewaySessionWire.EncodeClose(8, 9, ClusterSessionDetachReason.WorkerClosed)));
        Assert.Equal((byte)GatewayWorkerFrameType.SessionOpen, GatewaySessionWire.EncodeOpen(3, new GatewaySessionOpenRequest(1, 2, "A", 5875, "B"))[0]);
    }

    [Fact]
    public void Redirect_MatchesTheGoldenBytes_AndRoundTrips()
    {
        var redirect = new GatewaySessionRedirect(5, 7, 1, 0);
        byte[] frame = GatewaySessionWire.EncodeRedirect(redirect);
        Assert.Equal(GoldenRedirect, Convert.ToHexString(frame));
        Assert.Equal(redirect, GatewaySessionWire.DecodeRedirect(frame.AsSpan(1)));
    }

    [Fact]
    public void Adopt_MatchesTheGoldenBytes_AndRoundTrips()
    {
        byte[] payload = [1, 2, 3, 4, 5, 6, 7, 8];
        var request = new GatewaySessionAdoptRequest(new GatewaySessionOpenRequest(6, 7, "A", 5875, "B"), 0x3D, payload, 0x01020304);
        byte[] frame = GatewaySessionWire.EncodeAdopt(9, request);
        Assert.Equal(GoldenAdopt, Convert.ToHexString(frame));

        (long requestId, GatewaySessionAdoptRequest decoded) = GatewaySessionWire.DecodeAdopt(frame.AsSpan(1));
        Assert.Equal(9, requestId);
        Assert.Equal(request.Open, decoded.Open);
        Assert.Equal(0x3Du, decoded.LoginOpcode);
        Assert.Equal(0x01020304u, decoded.LoginReceivedMs);
        Assert.Equal(payload, decoded.LoginPayload.ToArray());
    }

    [Fact]
    public void Adopt_AndRedirect_RefuseTruncatedTrailingAndOversizedFrames()
    {
        byte[] adopt = GatewaySessionWire.EncodeAdopt(1, new GatewaySessionAdoptRequest(new GatewaySessionOpenRequest(2, 3, "A", 5875, "B"), 0x3D, new byte[8], 0));
        Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodeAdopt(adopt.AsSpan(1, adopt.Length - 2)));
        Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodeAdopt([.. adopt.AsSpan(1), 0]));

        // A payload length field over the bound is refused before any bytes are taken.
        byte[] oversized = [.. adopt];
        int lengthOffset = adopt.Length - 8 - 4;
        BinaryPrimitives.WriteInt32LittleEndian(oversized.AsSpan(lengthOffset, 4), GatewaySessionWire.MaxAdoptPacketBytes + 1);
        Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodeAdopt(oversized.AsSpan(1)));
        BinaryPrimitives.WriteInt32LittleEndian(oversized.AsSpan(lengthOffset, 4), -1);
        Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodeAdopt(oversized.AsSpan(1)));

        Assert.Throws<ArgumentException>(() => GatewaySessionWire.EncodeAdopt(1,
            new GatewaySessionAdoptRequest(new GatewaySessionOpenRequest(2, 3, "A", 5875, "B"), 0x3D, new byte[GatewaySessionWire.MaxAdoptPacketBytes + 1], 0)));

        byte[] redirect = GatewaySessionWire.EncodeRedirect(new GatewaySessionRedirect(1, 2, 3, 4));
        Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodeRedirect(redirect.AsSpan(1, redirect.Length - 2)));
        Assert.Throws<InvalidDataException>(() => GatewaySessionWire.DecodeRedirect([.. redirect.AsSpan(1), 0]));
    }

    [Fact]
    public async Task KalimdorCharacter_LoggingInThroughAGatewayWhoseLoginWorkerOwnsEasternKingdoms_EndsUpOnTheKalimdorWorker()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Cluster cluster = await Cluster.StartAsync(cts.Token);
        await cluster.Server.AddAccountAsync("REDIRHERO", "PASSWORD", cts.Token);
        await using WorldClient client = await cluster.ConnectAsync("REDIRHERO", cts.Token);
        int character = await cluster.CreateCharacterOnMapAsync(client, "Redirhero", mapId: 1, cts.Token);

        // The session sits on the login worker until the client asks for its character.
        await WaitUntilAsync(() => Task.FromResult(cluster.EasternWorker.Acceptor!.Sessions.Count == 1), cts.Token);
        Assert.Empty(cluster.KalimdorWorker.Acceptor!.Sessions);
        Assert.Equal(1, cluster.Gateway.Workers[0].Link!.SessionCount);

        await client.SendAsync((ushort)WorldOpcode.CmsgPlayerLogin, LoginPayload(character), cts.Token);
        WorldFrame verify = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgLoginVerifyWorld, cts.Token);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(verify.Payload));

        // Online on the Kalimdor world only; the login worker's session is closed, the Kalimdor worker holds the one session of the account.
        await WaitUntilAsync(async () => await cluster.Kalimdor.World.InvokeAsync(() => cluster.Kalimdor.World.OnlinePlayerCount) == 1, cts.Token);
        Assert.Equal(0, await cluster.Eastern.World.InvokeAsync(() => cluster.Eastern.World.OnlinePlayerCount));
        await WaitUntilAsync(() => Task.FromResult(cluster.EasternWorker.Acceptor!.Sessions.IsEmpty), cts.Token);
        RemoteWorldSession remote = Assert.Single(cluster.KalimdorWorker.Acceptor!.Sessions.Values);
        Assert.Equal(SessionState.InWorld, remote.Session.State);
        Assert.Equal((ulong)character, remote.Session.Player!.Guid.Value & 0xFFFFFFFF);
        Assert.Equal(client.LocalEndPoint!.ToString(), remote.Session.RemoteEndpoint);
        Assert.Equal(0, cluster.Gateway.Workers[0].Link!.SessionCount);
        Assert.Equal(1, cluster.Gateway.Workers[1].Link!.SessionCount);

        // The client keeps talking to the Kalimdor worker through the same connection: PING is answered by the gateway and reaches the new session.
        byte[] ping = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(ping, 0x5252);
        BinaryPrimitives.WriteUInt32LittleEndian(ping.AsSpan(4), 77);
        await client.SendAsync((ushort)WorldOpcode.CmsgPing, ping, cts.Token);
        WorldFrame pong = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgPong, cts.Token, maxPackets: 1024);
        Assert.Equal(0x5252u, BinaryPrimitives.ReadUInt32LittleEndian(pong.Payload));
        await WaitUntilAsync(() => Task.FromResult(remote.Session.LatencyMs == 77), cts.Token);

        // Leaving closes the session on the target, with nothing left on either link.
        await client.DisposeAsync();
        await WaitUntilAsync(async () => await cluster.Kalimdor.World.InvokeAsync(() => cluster.Kalimdor.World.OnlinePlayerCount) == 0, cts.Token);
        await WaitUntilAsync(() => Task.FromResult(cluster.KalimdorWorker.Acceptor!.Sessions.IsEmpty && cluster.Gateway.Workers[1].Link!.SessionCount == 0), cts.Token);
    }

    [Fact]
    public async Task EasternKingdomsCharacter_StaysOnTheLoginWorker_WithoutARedirect()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Cluster cluster = await Cluster.StartAsync(cts.Token);
        await cluster.Server.AddAccountAsync("STAYHERO", "PASSWORD", cts.Token);
        await using WorldClient client = await cluster.ConnectAsync("STAYHERO", cts.Token);
        int character = await cluster.CreateCharacterOnMapAsync(client, "Stayhero", mapId: 0, cts.Token);

        await client.SendAsync((ushort)WorldOpcode.CmsgPlayerLogin, LoginPayload(character), cts.Token);
        WorldFrame verify = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgLoginVerifyWorld, cts.Token);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(verify.Payload));
        await WaitUntilAsync(async () => await cluster.Eastern.World.InvokeAsync(() => cluster.Eastern.World.OnlinePlayerCount) == 1, cts.Token);
        Assert.Single(cluster.EasternWorker.Acceptor!.Sessions);
        Assert.Empty(cluster.KalimdorWorker.Acceptor!.Sessions);
        Assert.Equal(1, cluster.Gateway.Workers[0].Link!.SessionCount);
        Assert.Equal(0, cluster.Gateway.Workers[1].Link!.SessionCount);
    }

    [Fact]
    public async Task CharacterOnAMapNoWorkerOwns_IsDisconnected_AsALoginOnAnUnownedMapAlwaysWas()
    {
        using var cts = new CancellationTokenSource(Deadline);
        await using Cluster cluster = await Cluster.StartAsync(cts.Token);
        await cluster.Server.AddAccountAsync("NOOWNER", "PASSWORD", cts.Token);
        await using WorldClient client = await cluster.ConnectAsync("NOOWNER", cts.Token);
        int character = await cluster.CreateCharacterOnMapAsync(client, "Noowner", mapId: 530, cts.Token);

        await client.SendAsync((ushort)WorldOpcode.CmsgPlayerLogin, LoginPayload(character), cts.Token);
        await AssertDisconnectedAsync(client, cts.Token);
        await WaitUntilAsync(() => Task.FromResult(cluster.EasternWorker.Acceptor!.Sessions.IsEmpty && cluster.KalimdorWorker.Acceptor!.Sessions.IsEmpty), cts.Token);
        Assert.Equal(0, await cluster.Eastern.World.InvokeAsync(() => cluster.Eastern.World.OnlinePlayerCount));
        Assert.Equal(0, await cluster.Kalimdor.World.InvokeAsync(() => cluster.Kalimdor.World.OnlinePlayerCount));
    }

    private static byte[] LoginPayload(int character)
    {
        byte[] login = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(login, (ulong)character);
        return login;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken token)
    {
        while (!await condition())
        {
            await Task.Delay(20, token);
        }
    }

    private static async Task AssertDisconnectedAsync(WorldClient client, CancellationToken token)
    {
        for (int index = 0; index < 256; index++)
        {
            try
            {
                await client.ReadAsync(token);
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or SocketException or InvalidOperationException)
            {
                return;
            }
        }

        Assert.Fail("the client was not disconnected");
    }

    private static byte[] CharacterCreate(string name)
    {
        byte[] text = Encoding.ASCII.GetBytes(name);
        byte[] payload = new byte[text.Length + 10];
        text.CopyTo(payload, 0);
        payload[text.Length + 1] = 1; // human
        payload[text.Length + 2] = 1; // warrior
        return payload;
    }

    /// <summary>
    /// Two real worlds on one set of databases (as two world processes of a realm would run): the login worker owns map 0 (Eastern Kingdoms), the other
    /// owns map 1 (Kalimdor). A gateway in front of both names the first as its login worker.
    /// </summary>
    private sealed class Cluster : IAsyncDisposable
    {
        public required SyntheticArcaneServer Eastern { get; init; }

        public required SyntheticArcaneServer Kalimdor { get; init; }

        public required GatewayWorkerEndpoint EasternWorker { get; init; }

        public required GatewayWorkerEndpoint KalimdorWorker { get; init; }

        public required GatewayServer Gateway { get; init; }

        public SyntheticArcaneServer Server => Eastern;

        public static async Task<Cluster> StartAsync(CancellationToken token)
        {
            // Both worlds run as Cluster:Fencing:Scope=Worker workers (design _wf/writer-lock-DESIGN.md, C8): each takes its own shard lock and ownership scope, so they write
            // the one characters database concurrently, as two world processes of a split realm do. SQLite is allowed for this in-process fixture only (AllowSqliteWorkerScope).
            SyntheticArcaneServer eastern = await SyntheticArcaneServer.StartAsync(
                new SyntheticServerOptions { Configuration = WorkerScopeConfiguration("w-east", "0") }, configureServices: null, token);
            SyntheticArcaneServer? kalimdor = null;
            GatewayWorkerEndpoint? easternWorker = null;
            GatewayWorkerEndpoint? kalimdorWorker = null;
            GatewayServer? gateway = null;
            try
            {
                kalimdor = await SyntheticArcaneServer.StartAsync(
                    new SyntheticServerOptions { SharedDatabaseDirectory = eastern.DataDirectory, Configuration = WorkerScopeConfiguration("w-kalimdor", "1") },
                    configureServices: null,
                    token);
                eastern.World.Ownership = Owns("0");
                kalimdor.World.Ownership = Owns("1");
                easternWorker = await StartWorkerAsync(eastern, "0", token);
                kalimdorWorker = await StartWorkerAsync(kalimdor, "1", token);
                gateway = new GatewayServer(
                    eastern.Services.GetRequiredService<IServiceScopeFactory>(),
                    Options.Create(new GatewayOptions
                    {
                        BindAddress = IPAddress.Loopback.ToString(),
                        Port = 0,
                        LinkSecret = Secret,
                        LinkRetryDelay = TimeSpan.FromMilliseconds(100),
                        Workers =
                        [
                            new GatewayWorkerEntry { NodeId = "w-east", Address = IPAddress.Loopback.ToString(), Port = easternWorker.Host!.BoundEndPoint.Port, Maps = [0] },
                            new GatewayWorkerEntry { NodeId = "w-kalimdor", Address = IPAddress.Loopback.ToString(), Port = kalimdorWorker.Host!.BoundEndPoint.Port, Maps = [1] },
                        ],
                        LoginWorker = "w-east",
                    }),
                    eastern.Services.GetRequiredService<ILoggerFactory>());
                await gateway.StartAsync(token);
                await gateway.WaitForAllLinksAsync(token);
                return new Cluster
                {
                    Eastern = eastern, Kalimdor = kalimdor, EasternWorker = easternWorker, KalimdorWorker = kalimdorWorker, Gateway = gateway,
                };
            }
            catch
            {
                if (gateway is not null)
                {
                    await gateway.StopAsync(CancellationToken.None);
                    gateway.Dispose();
                }

                if (kalimdorWorker is not null)
                {
                    await kalimdorWorker.StopAsync(CancellationToken.None);
                }

                if (easternWorker is not null)
                {
                    await easternWorker.StopAsync(CancellationToken.None);
                }

                if (kalimdor is not null)
                {
                    await kalimdor.DisposeAsync();
                }

                await eastern.DisposeAsync();
                throw;
            }
        }

        /// <summary>Logs the account on at the realm and authenticates a client on the gateway (the session opens on the login worker).</summary>
        public async Task<WorldClient> ConnectAsync(string account, CancellationToken token)
        {
            LogonResult logon = await LogonClient.AuthenticateAsync(Eastern.RealmEndpoint, account, "PASSWORD", token);
            WorldClient client = await WorldClient.ConnectAsync(Gateway.ListenEndPoint, token);
            Assert.Equal((byte)AuthResponseCode.Ok, await client.AuthenticateAsync(account, logon.SessionKey, token));
            return client;
        }

        /// <summary>Creates a character through the character screen, then moves its saved position onto <paramref name="mapId"/> (the row the login reads).</summary>
        public async Task<int> CreateCharacterOnMapAsync(WorldClient client, string name, uint mapId, CancellationToken token)
        {
            await client.SendAsync((ushort)WorldOpcode.CmsgCharCreate, CharacterCreate(name), token);
            WorldFrame created = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharCreate, token);
            Assert.Equal(new byte[] { 0x2E }, created.Payload);
            await client.SendAsync((ushort)WorldOpcode.CmsgCharEnum, ReadOnlyMemory<byte>.Empty, token);
            WorldFrame list = await client.ReadUntilAsync((ushort)WorldOpcode.SmsgCharEnum, token);
            Assert.Equal(1, list.Payload[0]);
            int id = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(list.Payload.AsSpan(1, 8)));

            await using AsyncServiceScope scope = Eastern.Services.CreateAsyncScope();
            ICharacterStore store = scope.ServiceProvider.GetRequiredService<ICharacterStore>();
            // The worlds run on Worker scope with fencing on: a tokenless write is refused while any ownership row is active, so write under the login world's own token.
            CharacterOwnershipToken? owner = Eastern.Services.GetRequiredService<ICharacterOwnershipContext>().Current;
            await store.SaveStateAsync(new CharacterState(id, mapId, 1, 100f, 100f, 50f, 0f, 1, 0, Ownership: owner), token);
            CharacterRecord saved = Assert.IsType<CharacterRecord>(await store.GetByIdAsync(id, token));
            Assert.Equal(mapId, saved.MapId);
            return id;
        }

        public async ValueTask DisposeAsync()
        {
            await Gateway.StopAsync(CancellationToken.None);
            Gateway.Dispose();
            await KalimdorWorker.StopAsync(CancellationToken.None);
            await EasternWorker.StopAsync(CancellationToken.None);
            await Kalimdor.DisposeAsync();
            await Eastern.DisposeAsync();
        }

        private static Dictionary<string, string?> WorkerScopeConfiguration(string nodeId, string ownedMaps) => new()
        {
            ["Cluster:Fencing:Enabled"] = "true",
            ["Cluster:Fencing:Scope"] = "Worker",
            ["Cluster:Fencing:OwnerNode"] = nodeId,
            ["Cluster:Fencing:AllowSqliteWorkerScope"] = "true",
            ["Cluster:Worker:Enabled"] = "true",
            ["Cluster:Worker:NodeId"] = nodeId,
            ["Cluster:Worker:OwnedMaps"] = ownedMaps,
            ["Cluster:Worker:LinkSecret"] = Secret,
            ["Cluster:Node:Role"] = "MapWorker",
        };

        private static MapOwnership Owns(string maps)
        {
            Assert.True(MapOwnership.TryParse(maps, allowInstanceMaps: false, out MapOwnership ownership, out string? error), error);
            return ownership;
        }

        private static async Task<GatewayWorkerEndpoint> StartWorkerAsync(SyntheticArcaneServer server, string ownedMaps, CancellationToken token)
        {
            IServiceProvider services = server.Services;
            var endpoint = new GatewayWorkerEndpoint(
                services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new GatewayWorkerOptions { Enabled = true, Port = 0, LinkSecret = Secret, OwnedMaps = ownedMaps, AllowInstanceMaps = false }),
                services.GetRequiredService<IOptions<WorldSessionOptions>>(),
                services.GetRequiredService<OpcodeTable>(),
                server.World,
                services.GetRequiredService<SessionRegistry>(),
                services.GetRequiredService<ILoggerFactory>());
            await endpoint.StartAsync(token);
            return endpoint;
        }
    }
}
