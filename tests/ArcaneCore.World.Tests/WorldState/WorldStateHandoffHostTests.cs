using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Cluster.Handoff;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>
/// The production path of the handoff flush for the war effort and Scourge invasion writers: the source is resolved from the container built by <c>AddWorldHandoff</c> (the two feature arguments of its
/// registration), the freeze's Park captures what the world thread has queued, and <c>FlushAsync</c> writes it through the barrier's shared flush or, without a barrier, the source's own calls. A test that calls the
/// features' capture and flush directly cannot see a dropped Park call, hosting argument or flush call; this one can, because the queued write reaches a store only through them.
/// </summary>
public sealed class WorldStateHandoffHostTests
{
    /// <summary>A war effort store whose loads after the attach-time one wait for <see cref="Gate"/>, which holds the first refresh job in flight.</summary>
    private sealed class WarStore : IWarEffortStateStore
    {
        private int _loads;
        private int _blocked;
        private int _phaseWrites;
        private int _phase = (int)WarEffortPhase.Disabled;

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Blocked => Volatile.Read(ref _blocked);
        public int PhaseWrites => Volatile.Read(ref _phaseWrites);
        public WarEffortPhase Phase => (WarEffortPhase)Volatile.Read(ref _phase);

        public async Task<WarEffortSnapshot> LoadAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _loads) > 1)
            {
                Interlocked.Increment(ref _blocked);
                await Gate.Task.ConfigureAwait(false);
            }

            return WarEffortSnapshot.Disabled;
        }

        public Task SetPhaseAsync(WarEffortPhase phase, long phaseEndsAtUnix, CancellationToken cancellationToken = default)
        {
            Interlocked.Exchange(ref _phase, (int)phase);
            Interlocked.Increment(ref _phaseWrites);
            return Task.CompletedTask;
        }

        public Task<bool> MarkBossKilledAsync(int bossIndex, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    /// <summary>The same for the Scourge invasion store; <see cref="Starts"/> counts the writes of an invasion start.</summary>
    private sealed class ScourgeStore : IScourgeInvasionStateStore
    {
        private int _loads;
        private int _blocked;
        private int _starts;

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Blocked => Volatile.Read(ref _blocked);
        public int Starts => Volatile.Read(ref _starts);

        public async Task<ScourgeInvasionSnapshot> LoadAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _loads) > 1)
            {
                Interlocked.Increment(ref _blocked);
                await Gate.Task.ConfigureAwait(false);
            }

            return ScourgeInvasionSnapshot.Disabled;
        }

        public Task<bool> StartAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _starts);
            return Task.FromResult(true);
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> NecropolisDestroyedAsync(uint zoneId, uint spawnGuid, long nowUnix, int nextAttackSeconds, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> RestartZoneAsync(uint zoneId, long nowUnix, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    /// <summary>
    /// Both features have a store job in flight (held at its reload), so a war phase start and a Scourge invasion start queued on the world thread behind it are written by nothing else: the world is parked at the
    /// freeze and never runs the job's apply step, which would launch them. The flush must reach both stores, with and without the persistence barrier (which the production hosting passes).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AHandoffFlush_WritesTheWarEffortPhaseAndTheScourgeStartQueuedBehindAJobInFlight(bool withBarrier)
    {
        var war = new WarStore();
        var scourge = new ScourgeStore();
        WorldRuntime? world = null;
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<IWarEffortStateStore>(war);
            services.AddSingleton<IScourgeInvasionStateStore>(scourge);
            // The production registration (AddWorldHandoff) builds the source from the container: a dropped feature argument there leaves the stores unwritten with a hand-built source still green.
            services.AddSingleton(_ => world ?? throw new InvalidOperationException("the world is not built yet"));
            services.AddSingleton(new ArcaneCore.World.Cluster.Gateway.WorkerAdmission());
            services.AddSingleton(new WorldWriterGuard());
            services.Configure<ArcaneCore.World.Cluster.Gateway.GatewayWorkerOptions>(o => o.NodeId = "w1");
            if (withBarrier)
                services.AddSingleton(sp => new CharacterPersistenceBarrier(sp));
            services.AddWorldHandoff(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Cluster:Worker:Enabled"] = "true" }).Build());
        });
        world = host.World;
        IWorldHandoffSource? source = null;
        try
        {
            // The first refresh of each feature runs on the first ticks and stops at its reload: a job is in flight, so what is queued now stays queued.
            await WorldTestHost.WaitForAsync(() => war.Blocked > 0 && scourge.Blocked > 0, "both refresh jobs to be held in their store");
            WarEffortFeature warEffort = host.WorldServices.GetRequiredService<WarEffortFeature>();
            ScourgeInvasionFeature invasion = host.WorldServices.GetRequiredService<ScourgeInvasionFeature>();
            await host.OnWorldAsync(() =>
            {
                warEffort.OnEventChanged(WarEffortCatalog.GatheringEvent, active: true, resume: false);
                invasion.OnEventChanged(ScourgeInvasionCatalog.MainEvent, active: true, resume: false);
            });
            Assert.Equal(0, war.PhaseWrites); // the precondition: nothing but the flush can write them now
            Assert.Equal(0, scourge.Starts);

            source = host.WorldServices.GetRequiredService<IWorldHandoffSource>();
            await source.FreezeAsync(CancellationToken.None);
            war.Gate.SetResult();
            scourge.Gate.SetResult();
            await source.FlushAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Equal(1, war.PhaseWrites);
            Assert.Equal(WarEffortPhase.Gathering, war.Phase);
            Assert.Equal(1, scourge.Starts);
        }
        finally
        {
            war.Gate.TrySetResult();
            scourge.Gate.TrySetResult();
            if (source is not null)
                await source.ResumeAsync(CancellationToken.None);
        }
    }
}
