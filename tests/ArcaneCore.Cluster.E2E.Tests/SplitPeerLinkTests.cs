using System.Globalization;
using System.Net;
using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Cluster.E2E.Tests;

/// <summary>
/// The Split layout of <c>tools/ops/live/start-cluster.ps1 -Topology Split</c> with its <c>Cluster:Peer</c> settings: two fenced workers (<c>Cluster:Fencing:Scope=Worker</c>, role MapWorker) whose peer
/// listeners, node map and secret are configured the way the script passes them. The gate spawns its own processes (these are real child daemons over a fresh PostgreSQL), so this covers the
/// configuration path end to end: the lease holder listens on its peer port, another node finds it through the lease store and the endpoint list and is admitted, and logins still work beside it.
/// </summary>
public sealed class SplitPeerLinkTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(5);

    private static ClusterOptions SplitWithPeerLink() => new()
    {
        WorkerCount = 2,
        WorkerMaps = [[0], [1]],
        LoginWorkerIndex = 0,
        PeerLink = true,
    };

    [PostgresClusterFact]
    public async Task SplitWorkers_WithPeerSettings_HolderListens_AnotherNodeIsAdmitted_AndALoginStillWorks()
    {
        using var cts = new CancellationTokenSource(Deadline);
        CancellationToken ct = cts.Token;
        await using ClusterProcessFixture cluster = await ClusterProcessFixture.StartAsync(SplitWithPeerLink(), output, ct);

        Assert.Equal(2, cluster.Workers.Count);
        Assert.All(cluster.Workers, worker => Assert.True(worker.PeerPort > 0, "every Split world has its own peer port"));
        Assert.Equal(2, cluster.Workers.Select(worker => worker.PeerPort).Distinct().Count());
        Assert.DoesNotContain(cluster.Workers, worker => worker.PeerPort == worker.ClientPort || worker.PeerPort == worker.LinkPort);

        // Some world holds the Social lease at start and listens on its own peer port; the one that does not dials it and is admitted. Either world may be the holder, so each case is watched.
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var listening = new List<Task>();
        var admitted = new List<Task>();
        foreach (ClusterWorkerInfo worker in cluster.Workers)
        {
            listening.Add(cluster.WaitForWorkerOutputAsync(worker.Index, "Peer link: this node holds a peer scope; listening on", watch.Token));
            foreach (ClusterWorkerInfo holder in cluster.Workers.Where(other => other.Index != worker.Index))
            {
                string endpoint = string.Create(CultureInfo.InvariantCulture, $"{IPAddress.Loopback}:{holder.PeerPort}) admitted");
                admitted.Add(cluster.WaitForWorkerOutputAsync(worker.Index, "Peer link to " + holder.NodeId + " (" + endpoint, watch.Token));
            }
        }

        // A watch that ends any other way than a match (the test deadline cancelled it) must fail the test, not pass it.
        Task firstListening = await Task.WhenAny(listening);
        Assert.True(firstListening.IsCompletedSuccessfully, "no world logged that it holds a peer scope and listens");
        Task firstAdmitted = await Task.WhenAny(admitted);
        Assert.True(firstAdmitted.IsCompletedSuccessfully, "no world logged an admitted peer link to the holder");
        await watch.CancelAsync();
        // The watches that did not match end cancelled; only an unexpected failure of a watch is an error here.
        foreach (Task task in listening.Concat(admitted))
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await using WorldClient client = await cluster.ConnectGatewayClientAsync(ct);
        var session = new ScenarioConnection(client);
        await session.CreateCharacterAsync("Peerlink", ct);
        MockCharacter character = Assert.Single(await session.EnumerateAsync(ct));
        MockLogin login = await session.LoginAsync(character.Guid, ct);
        Assert.Contains((int)login.Location.Map, cluster.Workers[0].OwnedMaps);
        Assert.True(cluster.IsWorkerRunning(0));
        Assert.True(cluster.IsWorkerRunning(1));
        await session.LogoutAsync(ct);
    }

    [Fact]
    public async Task APeerLinkWithOneWorker_IsRefusedBeforeAnyProcessStarts()
    {
        ArgumentException failure = await Assert.ThrowsAsync<ArgumentException>(
            () => ClusterProcessFixture.StartAsync(new ClusterOptions { WorkerCount = 1, PeerLink = true }, output, CancellationToken.None));
        Assert.Contains("PeerLink", failure.Message, StringComparison.Ordinal);
    }
}
