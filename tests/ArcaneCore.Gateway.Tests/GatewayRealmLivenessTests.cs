using System.Net;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Realms;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.RealmList;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Gateway.Tests;

/// <summary>
/// <see cref="GatewayRealmLiveness"/>: the realm-list row of a clustered realm, written by the gateway from the workers' status reports
/// (docs/ops/realm-liveness.md section 7). The clock is hand driven, so the 60 s threshold and the 10 s beat cost no real time.
/// </summary>
public sealed class GatewayRealmLivenessTests
{
    private const string Secret = "realm-liveness-secret-0123456789";

    private static WorkerStatusReport Active(long ticks, int sessions = 0, WorkerStatusFlags flags = WorkerStatusFlags.None, int limit = 100)
        => new(ticks, sessions, limit, flags);

    private static WorkerStatusReport Standby(long ticks, int sessions = 0)
        => new(ticks, sessions, 100, WorkerStatusFlags.Standby);

    [Fact]
    public async Task WhenWorkerTicksStopFor60Seconds_TheRowGoesOffline_AndComesBackWhenTheyResume()
    {
        await using Rig rig = await Rig.StartedAsync();
        long ticks = 1;
        rig.Liveness.OnWorkerStatus("w1", Active(ticks));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Online, rig.Row.State);
        Assert.False(rig.Row.Flags.HasFlag(RealmFlags.Offline));

        // Thirty seconds of a ticking worker, then the loop hangs: reports keep arriving with the same counter.
        for (int i = 0; i < 6; i++)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(5));
            rig.Liveness.OnWorkerStatus("w1", Active(++ticks));
            await rig.WakeAsync();
        }

        DateTime lastAdvance = rig.Clock.UtcNow;
        DateTime lastBeat = rig.Row.HeartbeatUtc!.Value;
        for (int second = 5; second < 60; second += 5)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(5));
            rig.Liveness.OnWorkerStatus("w1", Active(ticks));
            await rig.WakeAsync();
            Assert.Equal(RealmRuntimeState.Online, rig.Row.State); // 55 s without a tick: still online
            Assert.Equal(lastBeat, rig.Row.HeartbeatUtc);          // but the beat is withheld, so the logon daemon's own check also runs out
        }

        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(60), rig.Clock.UtcNow - lastAdvance);
        rig.Liveness.OnWorkerStatus("w1", Active(ticks));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Stopped, rig.Row.State);
        Assert.True(rig.Row.Flags.HasFlag(RealmFlags.Offline));

        // The loop ticks again: the realm returns by itself.
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Liveness.OnWorkerStatus("w1", Active(++ticks));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Online, rig.Row.State);
        Assert.False(rig.Row.Flags.HasFlag(RealmFlags.Offline));
    }

    [Fact]
    public async Task AWorkerThatGoesSilent_LikeALostLink_TakesTheRowOfflineAfter60Seconds()
    {
        await using Rig rig = await Rig.StartedAsync();
        rig.Liveness.OnWorkerStatus("w1", Active(1));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Online, rig.Row.State);

        rig.Clock.Advance(TimeSpan.FromSeconds(59));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Online, rig.Row.State);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Stopped, rig.Row.State);
        Assert.True(rig.Row.Flags.HasFlag(RealmFlags.Offline));
    }

    [Fact]
    public async Task ARestartCountdown_ShowsRestarting_AndACancelReturnsToOnline()
    {
        await using Rig rig = await Rig.StartedAsync();
        rig.Liveness.OnWorkerStatus("w1", Active(1));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Online, rig.Row.State);

        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        rig.Liveness.OnWorkerStatus("w1", Active(2, flags: WorkerStatusFlags.Restarting));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Restarting, rig.Row.State);
        Assert.False(rig.Row.Flags.HasFlag(RealmFlags.Offline)); // the logon daemon turns Restarting into red or offline by its own setting

        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        rig.Liveness.OnWorkerStatus("w1", Active(3, flags: WorkerStatusFlags.ShuttingDown));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.ShuttingDown, rig.Row.State);

        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        rig.Liveness.OnWorkerStatus("w1", Active(4));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Online, rig.Row.State);
    }

    [Fact]
    public async Task OneWorkersRestartCountdown_WinsOverAnotherWorkerThatIsOnline()
    {
        await using Rig rig = await Rig.StartedAsync();
        rig.Liveness.OnWorkerStatus("w1", Active(1));
        rig.Liveness.OnWorkerStatus("w2", Active(1, flags: WorkerStatusFlags.Restarting));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Restarting, rig.Row.State);
    }

    [Fact]
    public async Task StandbyTicksAlone_LeaveTheRowOffline_AndNeverGoOnline()
    {
        await using Rig rig = await Rig.StartedAsync();
        long ticks = 1000;
        for (int second = 0; second < 120; second += 5)
        {
            rig.Liveness.OnWorkerStatus("standby", Standby(++ticks, sessions: 50));
            await rig.WakeAsync();
            Assert.False(rig.Store.OnlineWrites > 0, "a standby's ticks must not bring the realm online");
            Assert.True(rig.Row.Flags.HasFlag(RealmFlags.Offline));
            rig.Clock.Advance(TimeSpan.FromSeconds(5));
        }

        await rig.WakeAsync();
        Assert.Equal(0, rig.Store.OnlineWrites);
        Assert.Equal(RealmRuntimeState.Stopped, rig.Row.State);
        Assert.True(rig.Row.Flags.HasFlag(RealmFlags.Offline));
    }

    [Fact]
    public async Task APromotedStandby_CountsFromItsPromotion_AndTheOldActiveWorkersStaleTicksDoNot()
    {
        await using Rig rig = await Rig.StartedAsync();
        rig.Liveness.OnWorkerStatus("old", Active(500));
        rig.Liveness.OnWorkerStatus("new", Standby(10));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Online, rig.Row.State);

        // The old active world stops (its link stays up but it reports nothing new); the standby is promoted 70 s later.
        rig.Clock.Advance(TimeSpan.FromSeconds(70));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Stopped, rig.Row.State);

        rig.Liveness.OnWorkerStatus("new", Active(11));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Online, rig.Row.State);
        Assert.False(rig.Row.Flags.HasFlag(RealmFlags.Offline));
    }

    [Fact]
    public async Task Population_IsTheSumOverTheActiveWorkers_NotTheStandbysNorADeadOne()
    {
        await using Rig rig = await Rig.StartedAsync();
        rig.Liveness.OnWorkerStatus("w1", Active(1, sessions: 10));
        rig.Liveness.OnWorkerStatus("w2", Active(1, sessions: 30));
        rig.Liveness.OnWorkerStatus("standby", Standby(1, sessions: 500));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Online, rig.Row.State);
        Assert.Equal(RealmStatusFeature.Population(40, 100), rig.Row.Population);
        Assert.Equal(0.8f, rig.Row.Population, 3);

        // w2 gains players; the row follows within one pass.
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        rig.Liveness.OnWorkerStatus("w1", Active(2, sessions: 10));
        rig.Liveness.OnWorkerStatus("w2", Active(2, sessions: 50));
        await rig.WakeAsync();
        Assert.Equal(RealmStatusFeature.Population(60, 100), rig.Row.Population);

        // w2 stops ticking: after 60 s only w1's sessions count.
        for (int i = 0; i < 12; i++)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(5));
            rig.Liveness.OnWorkerStatus("w1", Active(3 + i, sessions: 10));
            rig.Liveness.OnWorkerStatus("w2", Active(2, sessions: 50));
            await rig.WakeAsync();
        }

        Assert.Equal(RealmRuntimeState.Online, rig.Row.State);
        Assert.Equal(RealmStatusFeature.Population(10, 100), rig.Row.Population);
    }

    [Fact]
    public async Task APlayerLimitOfZero_WritesNoPopulation()
    {
        await using Rig rig = await Rig.StartedAsync();
        rig.Liveness.OnWorkerStatus("w1", Active(1, sessions: 10, limit: 0));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Online, rig.Row.State);
        Assert.Equal(0, rig.Store.PopulationWrites);
    }

    [Fact]
    public async Task AWorkerThatHasNotTicked_ShowsStarting_UntilItDoes()
    {
        await using Rig rig = await Rig.StartedAsync();
        rig.Liveness.OnWorkerStatus("w1", Active(0));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Starting, rig.Row.State);
        Assert.True(rig.Row.Flags.HasFlag(RealmFlags.Offline));

        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        rig.Liveness.OnWorkerStatus("w1", Active(0));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Starting, rig.Row.State);
        Assert.Equal(rig.Clock.UtcNow, rig.Row.HeartbeatUtc); // a starting realm beats on process liveness

        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Liveness.OnWorkerStatus("w1", Active(1));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Online, rig.Row.State);
        Assert.False(rig.Row.Flags.HasFlag(RealmFlags.Offline));
    }

    [Fact]
    public async Task ARestartedWorker_WithALowerCounter_CountsAsTicking()
    {
        await using Rig rig = await Rig.StartedAsync();
        rig.Liveness.OnWorkerStatus("w1", Active(100_000));
        await rig.WakeAsync();
        rig.Clock.Advance(TimeSpan.FromSeconds(50));
        rig.Liveness.OnWorkerStatus("w1", Active(3)); // a new process: the counter starts again
        rig.Clock.Advance(TimeSpan.FromSeconds(50));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Online, rig.Row.State); // 100 s after the first report, 50 s after the restart's
    }

    [Fact]
    public async Task BeforeAnyWorkerReports_TheRowSaysStarting_ThenOfflineAfter60Seconds()
    {
        await using Rig rig = await Rig.StartedAsync();
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Starting, rig.Row.State);
        Assert.True(rig.Row.Flags.HasFlag(RealmFlags.Offline));

        rig.Clock.Advance(TimeSpan.FromSeconds(60));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Stopped, rig.Row.State);
    }

    [Fact]
    public async Task ACleanStop_WritesOfflineAndStopped()
    {
        await using Rig rig = await Rig.StartedAsync();
        rig.Liveness.OnWorkerStatus("w1", Active(1));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Online, rig.Row.State);

        await rig.Liveness.StopAsync(CancellationToken.None);
        Assert.Equal(RealmRuntimeState.Stopped, rig.Row.State);
        Assert.True(rig.Row.Flags.HasFlag(RealmFlags.Offline));
    }

    [Fact]
    public async Task AFailedWrite_IsRetriedOnTheNextPass_WithoutThrowing()
    {
        await using Rig rig = await Rig.StartedAsync();
        rig.Store.FailNext = 1;
        rig.Liveness.OnWorkerStatus("w1", Active(1));
        await rig.WakeAsync();
        Assert.NotEqual(RealmRuntimeState.Online, rig.Row.State);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await rig.WakeAsync();
        Assert.Equal(RealmRuntimeState.Online, rig.Row.State);
    }

    [Fact]
    public async Task RealmIdZero_WritesNothing()
    {
        var row = new RealmEntry { Id = 1, Name = "Realm", Address = "127.0.0.1:8085" };
        var store = new RowStore(row);
        using ServiceProvider services = new ServiceCollection().AddSingleton<IRealmStatusStore>(store).BuildServiceProvider();
        using var liveness = new GatewayRealmLiveness(services.GetRequiredService<IServiceScopeFactory>(), 0, NullLogger.Instance, new HandClock()) { StartLoop = false };
        await liveness.StartAsync(CancellationToken.None);
        liveness.OnWorkerStatus("w1", Active(1));
        await liveness.WakeAsync(CancellationToken.None);
        Assert.False(liveness.Enabled);
        Assert.Equal(RealmRuntimeState.Unknown, row.State);
    }

    [Fact]
    public async Task WorkerStatusFrames_ReachTheWriter_OverARealLink()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        long ticks = 41;
        int sessions = 3;
        var host = new GatewaySessionLinkHost(
            GatewayLinkManifest.Create(ClusterRole.Worker, "worker", Guid.NewGuid(), 1, "dev", 1, 1),
            new RefuseAll(), () => 1, Secret, new IPEndPoint(IPAddress.Loopback, 0), workerStatus: () => Active(Volatile.Read(ref ticks), Volatile.Read(ref sessions)));
        host.Start();
        await using (host)
        {
            await using Rig rig = await Rig.StartedAsync();
            var client = new GatewaySessionLinkClient(GatewayLinkManifest.Create(ClusterRole.Gateway, "gateway", Guid.NewGuid(), 1, "dev", 1, 1), host.BoundEndPoint, Secret)
            {
                WorkerStatusReceived = report => rig.Liveness.OnWorkerStatus("worker", report),
            };
            await using (client)
            {
                await client.ConnectAsync(cts.Token);
                await WaitUntilAsync(async () =>
                {
                    await rig.WakeAsync(); // the report sent on admission makes the realm online
                    return rig.Row.State == RealmRuntimeState.Online;
                }, cts.Token);
                Assert.Equal(RealmStatusFeature.Population(3, 100), rig.Row.Population);

                Volatile.Write(ref sessions, 20);
                Volatile.Write(ref ticks, 42);
                Assert.Equal(1, host.BroadcastWorkerStatus());
                await WaitUntilAsync(async () =>
                {
                    await rig.WakeAsync();
                    return rig.Row.Population == RealmStatusFeature.Population(20, 100);
                }, cts.Token);
            }
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken token)
    {
        while (!await condition().ConfigureAwait(false))
        {
            await Task.Delay(20, token).ConfigureAwait(false);
        }
    }

    private sealed class RefuseAll : IGatewaySessionAcceptor
    {
        public ValueTask<GatewaySessionAcceptResult> OpenAsync(GatewaySessionOpenRequest request, IGatewaySessionOutput output, CancellationToken cancellationToken)
            => ValueTask.FromResult(GatewaySessionAcceptResult.Refused(AuthResponseCode.Unavailable, "no sessions in this test"));
    }

    private sealed class HandClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        public DateTime UtcNow => _now.UtcDateTime;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    /// <summary>An in-memory realm row with the semantics of the EF store (flags, state, beat, population) and counters for what the tests assert on.</summary>
    private sealed class RowStore(RealmEntry row) : IRealmStatusStore
    {
        public int OnlineWrites;
        public int PopulationWrites;
        public int FailNext;

        private Task<bool> Run(Action change)
        {
            if (Interlocked.Decrement(ref FailNext) >= 0)
            {
                throw new InvalidOperationException("injected write failure");
            }

            Interlocked.Exchange(ref FailNext, 0);
            lock (row)
            {
                change();
            }

            return Task.FromResult(true);
        }

        public Task<bool> SetStartingAsync(int realmId, DateTime heartbeatUtc, CancellationToken cancellationToken = default)
            => Run(() =>
            {
                row.Flags |= RealmFlags.Offline;
                row.State = RealmRuntimeState.Starting;
                row.HeartbeatUtc = heartbeatUtc;
            });

        public Task<bool> SetOnlineAsync(int realmId, DateTime heartbeatUtc, CancellationToken cancellationToken = default)
            => Run(() =>
            {
                OnlineWrites++;
                row.Flags &= ~RealmFlags.Offline;
                row.Population = 0f;
                row.State = RealmRuntimeState.Online;
                row.HeartbeatUtc = heartbeatUtc;
            });

        public Task<bool> SetStateAsync(int realmId, RealmRuntimeState state, DateTime heartbeatUtc, CancellationToken cancellationToken = default)
            => Run(() =>
            {
                row.State = state;
                row.HeartbeatUtc = heartbeatUtc;
            });

        public Task<bool> BeatAsync(int realmId, DateTime heartbeatUtc, CancellationToken cancellationToken = default)
            => Run(() => row.HeartbeatUtc = heartbeatUtc);

        public Task<bool> SetStoppedAsync(int realmId, CancellationToken cancellationToken = default)
            => Run(() =>
            {
                row.Flags |= RealmFlags.Offline;
                row.State = RealmRuntimeState.Stopped;
            });

        public Task<bool> SetPopulationAsync(int realmId, float population, CancellationToken cancellationToken = default)
            => Run(() =>
            {
                PopulationWrites++;
                row.Population = population;
            });

        public Task<bool> SetAllowedSecurityLevelAsync(int realmId, byte level, CancellationToken cancellationToken = default)
            => Run(() => row.AllowedSecurityLevel = level);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private Rig()
        {
            Row = new RealmEntry { Id = 1, Name = "Realm", Address = "127.0.0.1:8085", Flags = RealmFlags.NewPlayers | RealmFlags.Offline, Population = 0.5f };
            Store = new RowStore(Row);
            _services = new ServiceCollection().AddSingleton<IRealmStatusStore>(Store).BuildServiceProvider();
            Liveness = new GatewayRealmLiveness(_services.GetRequiredService<IServiceScopeFactory>(), 1, NullLogger.Instance, Clock) { StartLoop = false };
        }

        public RealmEntry Row { get; }

        public RowStore Store { get; }

        public HandClock Clock { get; } = new();

        public GatewayRealmLiveness Liveness { get; }

        public Task WakeAsync() => Liveness.WakeAsync(CancellationToken.None);

        public static async Task<Rig> StartedAsync()
        {
            var rig = new Rig();
            await rig.Liveness.StartAsync(CancellationToken.None);
            Assert.True(rig.Liveness.Enabled);
            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            await Liveness.StopAsync(CancellationToken.None);
            Liveness.Dispose();
            await _services.DisposeAsync();
        }
    }
}
