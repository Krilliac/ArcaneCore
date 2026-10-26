using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Kernel.Social;
using ArcaneCore.World.Honor;
using ArcaneCore.World.Instances;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Social;
using ArcaneCore.World.WorldState;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Persistence;

/// <summary>
/// The persistence anomalies (<c>persist/*</c>) are reported to the process-wide <see cref="AnomalyLedger.Shared"/>, which every test of
/// the assembly shares. These tests run with parallelization disabled (xunit runs such a collection alone, after the parallel ones
/// finished), so a capture sees only the reports of the scenario it wraps.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PersistAnomalyCollection
{
    public const string Name = "Persist anomalies";
}

internal static class PersistAnomalies
{
    public static AnomalyCapture Capture() => AnomalyLedger.Shared.Capture(n => n.Subsystem == "persist");

    /// <summary>The captured notices of <paramref name="code"/> whose detail names <paramref name="queue"/> (<c>queue=Name</c>).</summary>
    public static AnomalyNotice[] Of(this AnomalyCapture capture, string code, string queue)
        => [.. capture.Notices.Where(n => n.Code == code && n.Detail.Contains("queue=" + queue + " ", StringComparison.Ordinal))];

    /// <summary>A snapshot of every notice as one line each (assertion messages).</summary>
    public static string Dump(this AnomalyCapture capture) => capture.Describe();
}

/// <summary>
/// Watches the persist reports and, for each one, asks another thread to run <c>query</c> (a queue call that takes the queue's gate)
/// while the report is still being made on the reporting thread. A query that cannot finish means the reporting thread holds the gate.
/// </summary>
internal sealed class GateProbe : IDisposable
{
    private readonly Func<bool> _query;
    private int _reports;
    private volatile bool _gateHeld;

    public GateProbe(Func<bool> query)
    {
        _query = query;
        AnomalyLedger.Shared.Reported += OnReported;
    }

    public int Reports => Volatile.Read(ref _reports);

    public bool GateHeld => _gateHeld;

    private void OnReported(AnomalyNotice notice)
    {
        if (notice.Subsystem != "persist")
        {
            return;
        }

        Interlocked.Increment(ref _reports);
        if (!Task.Run(_query).Wait(TimeSpan.FromSeconds(2)))
        {
            _gateHeld = true;
        }
    }

    public void Dispose() => AnomalyLedger.Shared.Reported -= OnReported;
}

/// <summary>What a fake store does on its next calls.</summary>
internal sealed class StoreSwitch
{
    public volatile bool Fail;
    public volatile bool Reject;

    /// <summary>Every call throws SQLite's "database is locked" (error 5), as a foreign writer holding the file would cause.</summary>
    public volatile bool Busy;

    /// <summary>Every call sleeps this long before its verdict, as a slow or locked store does.</summary>
    public volatile int DelayMs;
    private int _failNext;
    private int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>The next <paramref name="count"/> calls throw (a transient outage), then the store works again.</summary>
    public void FailNext(int count) => Volatile.Write(ref _failNext, count);

    public Task Step()
    {
        Interlocked.Increment(ref _attempts);
        if (DelayMs > 0)
        {
            Thread.Sleep(DelayMs);
        }

        if (Busy)
        {
            throw new SqliteException("SQLite Error 5: 'database is locked'.", 5);
        }

        if (Reject)
        {
            throw new CharacterOwnershipRejectedException("world", 1);
        }

        if (Fail)
        {
            throw new IOException("store down");
        }

        if (Interlocked.Decrement(ref _failNext) >= 0)
        {
            throw new IOException("store blip");
        }

        Interlocked.Exchange(ref _failNext, 0);
        return Task.CompletedTask;
    }
}

/// <summary>One per-character write queue driven through the same few operations, so each anomaly is proven on every queue.</summary>
internal abstract class QueueRig : IAsyncDisposable
{
    protected static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    public static readonly string[] Names =
    [
        nameof(HonorWriteQueue), nameof(ReputationWriteQueue), nameof(RestWriteQueue), nameof(ExploredZonesWriteQueue),
        nameof(TaxiFlightWriteQueue), nameof(InstanceWriteQueue), nameof(SocialWriteQueue),
    ];

    public StoreSwitch Store { get; } = new();

    public abstract string Name { get; }

    /// <summary>Queue one write for <paramref name="characterId"/>.</summary>
    public abstract void Write(int characterId);

    public abstract Task FlushAsync();

    public abstract Task StopAsync();

    /// <summary>The queue's <c>FlushCharacterAsync</c> raw: after a stop it reports the retained failure of the character without any attempt.</summary>
    public abstract Task FlushCharacterRawAsync(int characterId);

    /// <summary>The failure the queue still retains for <paramref name="characterId"/> (the inner exception of the not-durable fault), or null when it is durable.</summary>
    public async Task<Exception?> RetainedFailureAsync(int characterId)
    {
        try
        {
            await FlushCharacterRawAsync(characterId).WaitAsync(Budget);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.InnerException ?? ex;
        }
    }

    /// <summary>Completes with the exception the queue raised through its <c>FatalFailure</c> event.</summary>
    public Task<Exception> Fatal => FatalSource.Task;

    protected TaskCompletionSource<Exception> FatalSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Queue one retained-write sweep now, as the retry timer would; false when none was queued.</summary>
    public abstract bool PostSweep();

    /// <summary>The queue's own thread-safe query, which takes its gate: used to prove a report is not made while the gate is held.</summary>
    public abstract bool HasRetained(int characterId);

    /// <summary>The login barrier (<c>FlushCharacterAsync</c>): retries a retained write and faults while it is still not durable; the fault is swallowed here.</summary>
    public abstract Task RetryAsync(int characterId);

    public static QueueRig Create(string name, StopDrainBudget? drain = null) => name switch
    {
        nameof(HonorWriteQueue) => new HonorRig(drain),
        nameof(ReputationWriteQueue) => new ReputationRig(drain),
        nameof(RestWriteQueue) => new RestRig(drain),
        nameof(ExploredZonesWriteQueue) => new ExploredRig(drain),
        nameof(TaxiFlightWriteQueue) => new TaxiRig(drain),
        nameof(InstanceWriteQueue) => new InstanceRig(drain),
        nameof(SocialWriteQueue) => new SocialRig(drain),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    public async ValueTask DisposeAsync()
    {
        Store.Fail = false;
        Store.Reject = false;
        try
        {
            await StopAsync().WaitAsync(Budget);
        }
        catch (InvalidOperationException)
        {
            // a scenario that ends with a retained or rejected write
        }

        await DisposeCoreAsync();
    }

    protected abstract ValueTask DisposeCoreAsync();

    protected static ServiceProvider Services(Action<IServiceCollection> add)
    {
        var services = new ServiceCollection();
        add(services);
        return services.BuildServiceProvider();
    }

    // ---- the queues ----

    private sealed class HonorRig : QueueRig
    {
        private readonly ServiceProvider _provider;
        private readonly HonorWriteQueue _queue;

        public HonorRig(StopDrainBudget? drain)
        {
            _provider = Services(s => s.AddSingleton<IHonorStore>(new FakeHonorStore(Store)));
            _queue = new HonorWriteQueue(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, drain: drain) { RetainedRetryInterval = Timeout.InfiniteTimeSpan };
            _queue.FatalFailure += ex => FatalSource.TrySetResult(ex);
            _queue.Start();
        }

        public override string Name => nameof(HonorWriteQueue);
        public override void Write(int characterId) => _queue.SaveState(characterId, CharacterHonorState.Empty with { RankPoints = 12f });
        public override Task FlushAsync() => _queue.FlushAsync();
        public override Task StopAsync() => _queue.StopAsync();
        public override Task FlushCharacterRawAsync(int characterId) => _queue.FlushCharacterAsync(characterId);
        public override bool PostSweep() => _queue.PostRetainedSweepForTest();
        public override bool HasRetained(int characterId) => _queue.HasRetainedFailure(characterId);
        public override async Task RetryAsync(int characterId)
        {
            try
            {
                await _queue.FlushCharacterAsync(characterId).WaitAsync(Budget);
            }
            catch (InvalidOperationException)
            {
                // still not durable: the barrier faults, which is the point
            }
        }
        protected override ValueTask DisposeCoreAsync() => _provider.DisposeAsync();
    }

    private sealed class ReputationRig : QueueRig
    {
        private readonly ServiceProvider _provider;
        private readonly ReputationWriteQueue _queue;

        public ReputationRig(StopDrainBudget? drain)
        {
            _provider = Services(s => s.AddSingleton<ICharacterReputationStore>(new FakeReputationStore(Store)));
            _queue = new ReputationWriteQueue(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, drain: drain) { RetainedRetryInterval = Timeout.InfiniteTimeSpan };
            _queue.FatalFailure += ex => FatalSource.TrySetResult(ex);
            _queue.Start();
        }

        public override string Name => nameof(ReputationWriteQueue);
        public override void Write(int characterId) => _queue.SaveFactions(characterId, [new CharacterReputationRow(characterId, 21, 3100, 0x03)]);
        public override Task FlushAsync() => _queue.FlushAsync();
        public override Task StopAsync() => _queue.StopAsync();
        public override Task FlushCharacterRawAsync(int characterId) => _queue.FlushCharacterAsync(characterId);
        public override bool PostSweep() => _queue.PostRetainedSweepForTest();
        public override bool HasRetained(int characterId) => _queue.HasRetainedFailure(characterId);
        public override async Task RetryAsync(int characterId)
        {
            try
            {
                await _queue.FlushCharacterAsync(characterId).WaitAsync(Budget);
            }
            catch (InvalidOperationException)
            {
                // still not durable: the barrier faults, which is the point
            }
        }
        protected override ValueTask DisposeCoreAsync() => _provider.DisposeAsync();
    }

    private sealed class RestRig : QueueRig
    {
        private readonly ServiceProvider _provider;
        private readonly RestWriteQueue _queue;

        public RestRig(StopDrainBudget? drain)
        {
            _provider = Services(s => s.AddSingleton<ICharacterRestStore>(new FakeRestStore(Store)));
            _queue = new RestWriteQueue(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, drain: drain) { RetryDelay = TimeSpan.FromMilliseconds(1), RetainedRetryInterval = Timeout.InfiniteTimeSpan };
            _queue.FatalFailure += ex => FatalSource.TrySetResult(ex);
            _queue.Start();
        }

        public override string Name => nameof(RestWriteQueue);
        public override void Write(int characterId) => _queue.Save(characterId, new CharacterRestState(50f, 1000, true));
        public override Task FlushAsync() => _queue.FlushAsync();
        public override Task StopAsync() => _queue.StopAsync();
        public override Task FlushCharacterRawAsync(int characterId) => _queue.FlushCharacterAsync(characterId);
        public override bool PostSweep() => _queue.PostRetainedSweepForTest();
        public override bool HasRetained(int characterId) => _queue.HasRetainedFailure(characterId);
        public override async Task RetryAsync(int characterId)
        {
            try
            {
                await _queue.FlushCharacterAsync(characterId).WaitAsync(Budget);
            }
            catch (InvalidOperationException)
            {
                // still not durable: the barrier faults, which is the point
            }
        }
        protected override ValueTask DisposeCoreAsync() => _provider.DisposeAsync();
    }

    private sealed class ExploredRig : QueueRig
    {
        private readonly ServiceProvider _provider;
        private readonly ExploredZonesWriteQueue _queue;

        public ExploredRig(StopDrainBudget? drain)
        {
            _provider = Services(s => s.AddSingleton<IExploredZonesStore>(new FakeExploredStore(Store)));
            _queue = new ExploredZonesWriteQueue(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, drain: drain) { RetryDelay = TimeSpan.FromMilliseconds(1), RetainedRetryInterval = Timeout.InfiniteTimeSpan };
            _queue.FatalFailure += ex => FatalSource.TrySetResult(ex);
            _queue.Start();
        }

        public override string Name => nameof(ExploredZonesWriteQueue);
        public override void Write(int characterId) => _queue.Save(characterId, new uint[ExploredZonesText.WordCount]);
        public override Task FlushAsync() => _queue.FlushAsync();
        public override Task StopAsync() => _queue.StopAsync();
        public override Task FlushCharacterRawAsync(int characterId) => _queue.FlushCharacterAsync(characterId);
        public override bool PostSweep() => _queue.PostRetainedSweepForTest();
        public override bool HasRetained(int characterId) => _queue.HasRetainedFailure(characterId);
        public override async Task RetryAsync(int characterId)
        {
            try
            {
                await _queue.FlushCharacterAsync(characterId).WaitAsync(Budget);
            }
            catch (InvalidOperationException)
            {
                // still not durable: the barrier faults, which is the point
            }
        }
        protected override ValueTask DisposeCoreAsync() => _provider.DisposeAsync();
    }

    private sealed class TaxiRig : QueueRig
    {
        private readonly ServiceProvider _provider;
        private readonly TaxiFlightWriteQueue _queue;

        public TaxiRig(StopDrainBudget? drain)
        {
            _provider = Services(s => s.AddSingleton<ICharacterTaxiFlightStore>(new FakeTaxiStore(Store)));
            _queue = new TaxiFlightWriteQueue(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, drain: drain) { RetainedRetryInterval = Timeout.InfiniteTimeSpan };
            _queue.FatalFailure += ex => FatalSource.TrySetResult(ex);
            _queue.Start();
        }

        public override string Name => nameof(TaxiFlightWriteQueue);
        public override void Write(int characterId) => _queue.Save(characterId, new TaxiFlightRoute([1, 2], [10], [50]));
        public override Task FlushAsync() => _queue.FlushAsync();
        public override Task StopAsync() => _queue.StopAsync();
        public override Task FlushCharacterRawAsync(int characterId) => _queue.FlushCharacterAsync(characterId);
        public override bool PostSweep() => _queue.PostRetainedSweepForTest();
        public override bool HasRetained(int characterId) => _queue.HasRetainedFailure(characterId);
        public override async Task RetryAsync(int characterId)
        {
            try
            {
                await _queue.FlushCharacterAsync(characterId).WaitAsync(Budget);
            }
            catch (InvalidOperationException)
            {
                // still not durable: the barrier faults, which is the point
            }
        }
        protected override ValueTask DisposeCoreAsync() => _provider.DisposeAsync();
    }

    private sealed class InstanceRig : QueueRig
    {
        private readonly ServiceProvider _provider;
        private readonly InstanceWriteQueue _queue;

        public InstanceRig(StopDrainBudget? drain)
        {
            _provider = Services(s => s.AddSingleton<IInstanceStore>(new FakeInstanceStore(Store)));
            _queue = new InstanceWriteQueue(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, drain: drain) { RetryDelay = TimeSpan.FromMilliseconds(1), RetainedRetryInterval = Timeout.InfiniteTimeSpan };
            _queue.FatalFailure += ex => FatalSource.TrySetResult(ex);
            _queue.Start();
        }

        public override string Name => nameof(InstanceWriteQueue);
        public override void Write(int characterId) => _queue.PlayerBound((uint)characterId, 900, permanent: true);
        public override Task FlushAsync() => _queue.FlushAsync();
        public override Task StopAsync() => _queue.StopAsync();
        public override Task FlushCharacterRawAsync(int characterId) => _queue.FlushCharacterAsync(characterId);
        public override bool PostSweep() => _queue.PostRetainedSweepForTest();
        public override bool HasRetained(int characterId) => _queue.HasRetainedFailure(characterId);
        public override async Task RetryAsync(int characterId)
        {
            try
            {
                await _queue.FlushCharacterAsync(characterId).WaitAsync(Budget);
            }
            catch (InvalidOperationException)
            {
                // still not durable: the barrier faults, which is the point
            }
        }
        protected override ValueTask DisposeCoreAsync() => _provider.DisposeAsync();
    }

    private sealed class SocialRig : QueueRig
    {
        private readonly ServiceProvider _provider;
        private readonly SocialWriteQueue _queue;

        public SocialRig(StopDrainBudget? drain)
        {
            _provider = Services(s => s.AddSingleton<ISocialStore>(new FakeSocialStore(Store)));
            _queue = new SocialWriteQueue(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, new SocialWriteQueueOptions { RetryDelayMs = 1 }, drain) { RetainedRetryInterval = Timeout.InfiniteTimeSpan };
            _queue.FatalFailure += ex => FatalSource.TrySetResult(ex);
            _queue.Start();
        }

        public override string Name => nameof(SocialWriteQueue);
        public override void Write(int characterId) => _queue.SetSocial(characterId, 20, SocialFlags.Friend);
        public override Task FlushAsync() => _queue.FlushAsync();
        public override Task StopAsync() => _queue.StopAsync();
        public override Task FlushCharacterRawAsync(int characterId) => _queue.FlushCharacterAsync(characterId);
        public override bool PostSweep() => _queue.PostRetainedSweepForTest();
        public override bool HasRetained(int characterId) => _queue.HasRetainedFailure(characterId);
        public override async Task RetryAsync(int characterId)
        {
            try
            {
                await _queue.FlushCharacterAsync(characterId).WaitAsync(Budget);
            }
            catch (InvalidOperationException)
            {
                // still not durable: the barrier faults, which is the point
            }
        }
        protected override ValueTask DisposeCoreAsync() => _provider.DisposeAsync();
    }

    // ---- the fake stores ----

    private sealed class FakeHonorStore(StoreSwitch sw) : IHonorStore
    {
        public Task<CharacterHonorData> LoadAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult(CharacterHonorData.Empty);
        public Task SaveStateAsync(int characterId, CharacterHonorState state, CancellationToken cancellationToken = default) => sw.Step();
        public Task AppendCpAsync(int characterId, IReadOnlyList<HonorCpRecord> rows, CancellationToken cancellationToken = default) => sw.Step();
        public Task ResetAsync(int characterId, CancellationToken cancellationToken = default) => sw.Step();
        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => sw.Step();
        public Task DeleteDeletedCharacterAsync(int characterId, CancellationToken cancellationToken = default) => sw.Step();
        public Task<HonorMaintenanceState?> GetMaintenanceAsync(CancellationToken cancellationToken = default) => Task.FromResult<HonorMaintenanceState?>(null);
        public Task SaveMaintenanceAsync(HonorMaintenanceState state, CancellationToken cancellationToken = default) => sw.Step();
        public Task<IReadOnlyList<HonorWeeklyScore>> ListWeeklyScoresAsync(uint weekBeginDay, uint weekEndDay, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<HonorWeeklyScore>>([]);
        public Task ApplyMaintenanceAsync(HonorMaintenanceBatch batch, CancellationToken cancellationToken = default) => sw.Step();
    }

    private sealed class FakeReputationStore(StoreSwitch sw) : ICharacterReputationStore
    {
        public Task<CharacterReputationData> LoadAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult(CharacterReputationData.Empty);

        public Task SaveFactionsAsync(int characterId, IReadOnlyList<CharacterReputationRow> upserts, CancellationToken cancellationToken = default, CharacterOwnershipToken? ownership = null)
            => sw.Step();

        public Task SaveWatchedFactionAsync(int characterId, int watchedFaction, CancellationToken cancellationToken = default, CharacterOwnershipToken? ownership = null)
            => sw.Step();

        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => sw.Step();
        public Task DeleteDeletedCharacterAsync(int characterId, CancellationToken cancellationToken = default) => sw.Step();
    }

    private sealed class FakeRestStore(StoreSwitch sw) : ICharacterRestStore
    {
        public Task<CharacterRestState?> LoadAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult<CharacterRestState?>(null);
        public Task SaveAsync(int characterId, CharacterRestState state, CancellationToken cancellationToken = default) => sw.Step();
        public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default) => sw.Step();
    }

    private sealed class FakeExploredStore(StoreSwitch sw) : IExploredZonesStore
    {
        public Task<uint[]?> LoadAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult<uint[]?>(null);
        public Task SaveAsync(int characterId, IReadOnlyList<uint> words, CancellationToken cancellationToken = default) => sw.Step();
        public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default) => sw.Step();
    }

    private sealed class FakeTaxiStore(StoreSwitch sw) : ICharacterTaxiFlightStore
    {
        public Task<TaxiFlightRoute?> LoadAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult<TaxiFlightRoute?>(null);
        public Task SaveAsync(int characterId, TaxiFlightRoute route, CancellationToken cancellationToken = default) => sw.Step();
        public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default) => sw.Step();
    }

    private sealed class FakeInstanceStore(StoreSwitch sw) : IInstanceStore
    {
        public Task<InstanceStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(InstanceStoreSnapshot.Empty);
        public Task SaveInstanceAsync(InstanceRecord instance, CancellationToken cancellationToken = default) => sw.Step();
        public Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default) => sw.Step();
        public Task SaveInstanceDataAsync(uint instanceId, string data, CancellationToken cancellationToken = default) => sw.Step();
        public Task SaveBindAsync(CharacterInstanceBindRecord bind, CancellationToken cancellationToken = default) => sw.Step();
        public Task DeleteBindAsync(int characterId, uint instanceId, CancellationToken cancellationToken = default) => sw.Step();
        public Task SaveResetTimeAsync(InstanceResetRecord reset, CancellationToken cancellationToken = default) => sw.Step();
        public Task SaveLastInstanceAsync(CharacterLastInstanceRecord last, CancellationToken cancellationToken = default) => sw.Step();
        public Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default) => sw.Step();
        public Task SaveGroupBindAsync(GroupInstanceBindRecord bind, CancellationToken cancellationToken = default) => sw.Step();
        public Task DeleteGroupBindAsync(int leaderCharacterId, uint instanceId, CancellationToken cancellationToken = default) => sw.Step();
    }

    private sealed class FakeSocialStore(StoreSwitch sw) : ISocialStore
    {
        public Task<IReadOnlyList<SocialEntry>> GetSocialAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SocialEntry>>([]);
        public Task SetSocialAsync(int characterId, int otherId, SocialFlags flags, CancellationToken cancellationToken = default) => sw.Step();
        public Task<IReadOnlyList<GuildData>> GetGuildsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GuildData>>([]);
        public Task SaveGuildAsync(GuildData guild, CancellationToken cancellationToken = default) => sw.Step();
        public Task DeleteGuildAsync(int guildId, CancellationToken cancellationToken = default) => sw.Step();
        public Task PurgeCharacterAsync(int characterId, CancellationToken cancellationToken = default) => sw.Step();
    }
}
