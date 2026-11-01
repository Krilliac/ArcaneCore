using System.Net;
using System.Net.Sockets;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Cluster.Transfer;
using ArcaneCore.World.Cluster;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Transfer;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>The gateway's map-to-worker table: announced maps, the static fallback, link up and down, the login worker, and the .cluster placement feed.</summary>
public sealed class GatewayRouteTableTests
{
    private const string Secret = "link-secret-0123456789";

    private static GatewayWorkerEntry Entry(string nodeId, params int[] maps) =>
        new() { NodeId = nodeId, Address = "127.0.0.1", Port = 9000, Maps = maps };

    [Fact]
    public void Table_FollowsLinkUpAndDown()
    {
        var table = new GatewayRouteTable([Entry("ek"), Entry("kal")]);

        Assert.False(table.TryResolve(0, 0, out _)); // nothing is ready yet

        Assert.True(table.LinkUp("ek", "0", allowInstanceMaps: false));
        Assert.True(table.LinkUp("kal", "1", allowInstanceMaps: false));
        Assert.True(table.TryResolve(0, 0, out string node));
        Assert.Equal("ek", node);
        Assert.True(table.TryResolve(1, 0, out node));
        Assert.Equal("kal", node);
        Assert.False(table.TryResolve(30, 0, out _)); // listed by neither

        Assert.True(table.LinkDown("ek"));
        Assert.False(table.TryResolve(0, 0, out _));
        Assert.True(table.TryResolve(1, 0, out node));
        Assert.Equal("kal", node);

        Assert.True(table.LinkUp("ek", "0,30-40", allowInstanceMaps: false)); // came back with a different list
        Assert.True(table.TryResolve(35, 0, out node));
        Assert.Equal("ek", node);

        Assert.False(table.LinkUp("nobody", "0", true));
        Assert.False(table.LinkDown("nobody"));
    }

    [Fact]
    public void AnnouncedMaps_BeatTheStaticList_AndAnEmptyAnnouncementDefersToIt()
    {
        var table = new GatewayRouteTable([Entry("a", 0), Entry("b", 1)]);

        table.LinkUp("a", "0,5-6", allowInstanceMaps: false);
        table.LinkUp("b", string.Empty, allowInstanceMaps: true);

        Assert.True(table.TryResolve(5, 0, out string node));
        Assert.Equal("a", node); // announced
        Assert.True(table.TryResolve(1, 0, out node));
        Assert.Equal("b", node); // static fallback
        Assert.False(table.TryResolve(2, 0, out _));

        GatewayRouteEntry a = Assert.Single(table.Snapshot(), row => row.NodeId == "a");
        GatewayRouteEntry b = Assert.Single(table.Snapshot(), row => row.NodeId == "b");
        Assert.True(a.FromAnnouncement);
        Assert.False(b.FromAnnouncement);

        table.LinkDown("a");
        Assert.False(Assert.Single(table.Snapshot(), row => row.NodeId == "a").FromAnnouncement);
    }

    [Fact]
    public void WorkerWithNoMaps_OwnsEverything_AndInstancesGoToAnInstanceWorker()
    {
        var table = new GatewayRouteTable([Entry("cont", 0), Entry("inst", 1), Entry("all")]);
        table.LinkUp("cont", "0", allowInstanceMaps: false);
        table.LinkUp("inst", "1", allowInstanceMaps: true);

        Assert.True(table.TryResolve(409, 7, out string node)); // a dungeon copy: the first worker that runs instances
        Assert.Equal("inst", node);
        Assert.False(table.TryResolve(409, 0, out _)); // the shared map 409 is listed by nobody

        table.LinkUp("all", string.Empty, allowInstanceMaps: true);
        Assert.True(table.TryResolve(409, 0, out node));
        Assert.Equal("all", node);
        Assert.True(table.TryResolve(0, 0, out node));
        Assert.Equal("cont", node); // a listing worker wins over an owns-everything one
    }

    [Fact]
    public void Sessions_OpenOnTheLoginWorker_WhileItIsUp()
    {
        var table = new GatewayRouteTable([Entry("a"), Entry("b")], loginWorker: "b");
        Assert.Equal("b", table.ConfiguredLoginWorker);
        Assert.False(table.TryGetLoginWorker(out _)); // down

        table.LinkUp("a", string.Empty, true);
        Assert.False(table.TryGetLoginWorker(out _)); // only the designated one counts

        table.LinkUp("b", string.Empty, true);
        Assert.True(table.TryGetLoginWorker(out string node));
        Assert.Equal("b", node);

        table.LinkDown("b");
        Assert.False(table.TryGetLoginWorker(out _));

        Assert.False(new GatewayRouteTable([Entry("a")]).TryGetLoginWorker(out _)); // none designated
        Assert.Throws<ArgumentException>(() => new GatewayRouteTable([Entry("a")], loginWorker: "missing"));
    }

    [Fact]
    public void LoginWorkerOption_MustNameAWorkerEntry()
    {
        var options = new GatewayOptions { LinkSecret = Secret, Workers = [Entry("a"), Entry("b")], LoginWorker = "c" };
        Assert.Contains(options.Validate(), problem => problem.Contains("LoginWorker", StringComparison.Ordinal));
        options.LoginWorker = "b";
        Assert.DoesNotContain(options.Validate(), problem => problem.Contains("LoginWorker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LinkClient_ReadsTheMapsTheWorkerAnnouncedInItsHelloAck()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var client = new GatewaySessionLinkClient(
            GatewayLinkManifest.Create(ClusterRole.Gateway, "gateway", Guid.NewGuid(), 1, "dev", 1, 1), (IPEndPoint)listener.LocalEndpoint, Secret);
        Task connect = client.ConnectAsync(cts.Token);
        using TcpClient raw = await listener.AcceptTcpClientAsync(cts.Token);
        await using var worker = new GatewayLinkChannel(raw);
        worker.TryEnqueue(GatewaySessionWire.EncodeNonceFrame(GatewayWorkerFrameType.LinkChallenge, new byte[GatewaySessionWire.NonceBytes]));
        Assert.Equal((byte)GatewayWorkerFrameType.LinkProof, (await worker.ReadFrameAsync(cts.Token))![0]);
        Assert.Equal((byte)GatewayWorkerFrameType.HelloRegister, (await worker.ReadFrameAsync(cts.Token))![0]);
        worker.TryEnqueue(LoopbackGatewayWorkerHost.BuildHelloAckFrame(true, string.Empty, "0,30-40", allowInstanceMaps: false));
        await connect;

        Assert.Equal("0,30-40", client.WorkerOwnedMaps);
        Assert.False(client.WorkerAllowsInstanceMaps);

        var table = new GatewayRouteTable([Entry("w")]);
        table.LinkUp("w", client.WorkerOwnedMaps, client.WorkerAllowsInstanceMaps);
        Assert.True(table.TryResolve(35, 0, out _));
        Assert.False(table.TryResolve(1, 0, out _));
        Assert.False(table.TryResolve(409, 3, out _)); // instances refused
    }

    // The HelloAck layout before map routing: type, accepted flag, reason (int32 LE length + UTF-8). "ok" as the reason.
    private static readonly byte[] HelloAckOriginalLayout = [(byte)GatewayWorkerFrameType.HelloAck, 0x01, 0x02, 0x00, 0x00, 0x00, 0x6F, 0x6B];

    [Fact]
    public void HelloAckGolden_ChangesOnlyByTheAppendedTail()
    {
        Assert.Equal((byte)2, (byte)GatewayWorkerFrameType.HelloAck);

        // No maps: an empty list (length 0) and the instance flag.
        byte[] without = LoopbackGatewayWorkerHost.BuildHelloAckFrame(true, "ok");
        Assert.Equal([.. HelloAckOriginalLayout, 0x00, 0x00, 0x00, 0x00, 0x01], without);
        Assert.Equal(HelloAckOriginalLayout, without[..HelloAckOriginalLayout.Length]);

        // With maps "0,30-40" (7 bytes) and instances refused.
        byte[] with = LoopbackGatewayWorkerHost.BuildHelloAckFrame(true, "ok", "0,30-40", allowInstanceMaps: false);
        Assert.Equal(
            [.. HelloAckOriginalLayout, 0x07, 0x00, 0x00, 0x00, 0x30, 0x2C, 0x33, 0x30, 0x2D, 0x34, 0x30, 0x00],
            with);
        Assert.Equal(HelloAckOriginalLayout, with[..HelloAckOriginalLayout.Length]);

        // A refusal keeps the same prefix shape.
        byte[] refused = LoopbackGatewayWorkerHost.BuildHelloAckFrame(false, "ok");
        Assert.Equal((byte)0, refused[1]);
    }

    private static async Task<GatewaySessionLinkClient> ConnectAgainstAckAsync(byte[] ack, CancellationToken token, Func<Task, Task> assertOutcome)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var client = new GatewaySessionLinkClient(
                GatewayLinkManifest.Create(ClusterRole.Gateway, "gateway", Guid.NewGuid(), 1, "dev", 1, 1), (IPEndPoint)listener.LocalEndpoint, Secret);
            Task connect = client.ConnectAsync(token);
            using TcpClient raw = await listener.AcceptTcpClientAsync(token);
            await using var worker = new GatewayLinkChannel(raw);
            worker.TryEnqueue(GatewaySessionWire.EncodeNonceFrame(GatewayWorkerFrameType.LinkChallenge, new byte[GatewaySessionWire.NonceBytes]));
            Assert.Equal((byte)GatewayWorkerFrameType.LinkProof, (await worker.ReadFrameAsync(token))![0]);
            Assert.Equal((byte)GatewayWorkerFrameType.HelloRegister, (await worker.ReadFrameAsync(token))![0]);
            worker.TryEnqueue(ack);
            await assertOutcome(connect);
            return client;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task LinkClient_AcceptsALegacyHelloAckWithoutTheTail_AsOwnsEverything()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using GatewaySessionLinkClient client = await ConnectAgainstAckAsync(HelloAckOriginalLayout, cts.Token, connect => connect);

        Assert.Equal(string.Empty, client.WorkerOwnedMaps);
        Assert.True(client.WorkerAllowsInstanceMaps);
    }

    [Fact]
    public async Task LinkClient_RejectsAHelloAckWithATruncatedMapTail()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // The reason is intact but the tail holds 3 bytes, fewer than the 5 a length and flag need: the decoder throws InvalidDataException,
        // the receive loop closes the link and the connect fails.
        byte[] truncated = [.. HelloAckOriginalLayout, 0x01, 0x00, 0x00];
        await using GatewaySessionLinkClient client = await ConnectAgainstAckAsync(
            truncated, cts.Token, async connect => await Assert.ThrowsAsync<InvalidOperationException>(() => connect));

        Assert.Equal(string.Empty, client.WorkerOwnedMaps);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void HelloAck_WithoutTheMapTail_ReadsAsNoAnnouncement_AndWithItRoundTrips()
    {
        byte[] legacy = new byte[1 + 1 + 4];
        legacy[0] = (byte)GatewayWorkerFrameType.HelloAck;
        legacy[1] = 1;
        Assert.False(LoopbackGatewayWorkerHost.TryReadHelloAckOwnedMaps(legacy.AsSpan(1), out _, out _));

        byte[] modern = LoopbackGatewayWorkerHost.BuildHelloAckFrame(true, "ok", "1,5-9", allowInstanceMaps: true);
        Assert.True(LoopbackGatewayWorkerHost.TryReadHelloAckOwnedMaps(modern.AsSpan(1), out string owned, out bool allow));
        Assert.Equal("1,5-9", owned);
        Assert.True(allow);
    }

    [Fact]
    public void ClusterPlacement_ShowsTheAnnouncedMaps_AndReplacesThemOnReannounce()
    {
        var placement = new ClusterMapPlacementTable();
        var ek = new WorkerNodeId("ek");
        var kal = new WorkerNodeId("kal");
        placement.RegisterWorker(ek);
        placement.RegisterWorker(kal);

        Assert.Equal(3, placement.ApplyAnnouncedOwnership(ek, "0,30-31"));
        Assert.Equal(1, placement.ApplyAnnouncedOwnership(kal, "1"));
        Assert.Equal(new (uint, WorkerNodeId)[] { (0u, ek), (1u, kal), (30u, ek), (31u, ek) }, placement.SnapshotStaticMaps().ToArray());
        Assert.True(placement.TryResolveTargetWorker(30, 0, out WorkerNodeId owner, out _));
        Assert.Equal(ek, owner);

        Assert.Equal(1, placement.ApplyAnnouncedOwnership(ek, "0")); // restarted with a smaller list
        Assert.Equal(new (uint, WorkerNodeId)[] { (0u, ek), (1u, kal) }, placement.SnapshotStaticMaps().ToArray());

        Assert.Equal(0, placement.ApplyAnnouncedOwnership(ek, "not-a-map"));
        Assert.Equal(new (uint, WorkerNodeId)[] { (1u, kal) }, placement.SnapshotStaticMaps().ToArray());

        Assert.Equal(1, placement.ReleaseStaticMaps(kal));
        Assert.Empty(placement.SnapshotStaticMaps());
    }
}
