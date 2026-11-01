using ArcaneCore.MockClient.Protocol;
using ArcaneCore.MockClient.Scenarios;
using Xunit;

namespace ArcaneCore.Cluster.E2E.Tests;

/// <summary>
/// Scenarios that need two concurrent fenced workers. They cannot run yet: the characters writer lock is one advisory lock per realm and
/// ownership allows only scope 'world', so a second worker is refused at start (tools/ops/live/start-cluster.ps1 -Topology Split refuses a real start
/// for the same reason). They are written now against the fixture's multi-worker options and stay skipped until s1-per-worker-writer-lock lands.
/// </summary>
public sealed class TwoWorkerScenarioTests
{
    private const string SkipReason = "needs s1-per-worker-writer-lock";
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(5);

    // Kalimdor (1) on the second worker, the Eastern Kingdoms (0, where a human starts) on the first.
    private static ClusterOptions TwoWorkers() => new()
    {
        WorkerCount = 2,
        WorkerMaps = [[0], [1]],
        LoginWorkerIndex = 0,
    };

    [Fact(Skip = SkipReason)]
    public async Task TwoWorkers_OwnDistinctMaps_AndALoginIsServedByTheOwnerOfItsMap()
    {
        using var cts = new CancellationTokenSource(Deadline);
        CancellationToken ct = cts.Token;
        await using ClusterProcessFixture cluster = await ClusterProcessFixture.StartAsync(TwoWorkers(), ct);

        Assert.Equal(2, cluster.Workers.Count);
        Assert.Empty(cluster.Workers[0].OwnedMaps.Intersect(cluster.Workers[1].OwnedMaps));
        Assert.NotEqual(cluster.Workers[0].NodeId, cluster.Workers[1].NodeId);
        Assert.NotEqual(cluster.Workers[0].LinkPort, cluster.Workers[1].LinkPort);

        await using WorldClient client = await cluster.ConnectGatewayClientAsync(ct);
        var session = new ScenarioConnection(client);
        await session.CreateCharacterAsync("Twoworld", ct);
        MockCharacter character = Assert.Single(await session.EnumerateAsync(ct));
        MockLogin login = await session.LoginAsync(character.Guid, ct);
        Assert.Contains((int)login.Location.Map, cluster.Workers[0].OwnedMaps);
        Assert.True(cluster.IsWorkerRunning(0));
        Assert.True(cluster.IsWorkerRunning(1));
        await session.LogoutAsync(ct);
    }

    [Fact(Skip = SkipReason)]
    public async Task KillingTheLoginWorker_ThenReloggingIn_IsServedByTheSurvivor()
    {
        using var cts = new CancellationTokenSource(Deadline);
        CancellationToken ct = cts.Token;
        // The character list is served by whichever worker holds a ready link once the login worker is gone.
        await using ClusterProcessFixture cluster = await ClusterProcessFixture.StartAsync(TwoWorkers(), ct);

        await using (WorldClient first = await cluster.ConnectGatewayClientAsync(ct))
        {
            var session = new ScenarioConnection(first);
            await session.CreateCharacterAsync("Killworld", ct);
            MockCharacter character = Assert.Single(await session.EnumerateAsync(ct));
            await session.LoginAsync(character.Guid, ct);
        }

        await cluster.KillWorkerAsync(0);
        Assert.False(cluster.IsWorkerRunning(0));
        Assert.True(cluster.IsWorkerRunning(1));

        // The gateway moves new sessions to the ready link (Gateway:LoginWorker is down), and the account's character list is still served.
        await using WorldClient second = await cluster.ConnectGatewayClientAsync(ct);
        var relogin = new ScenarioConnection(second);
        MockCharacter again = Assert.Single(await relogin.EnumerateAsync(ct));
        Assert.Equal("Killworld", again.Name, ignoreCase: true);
    }
}
