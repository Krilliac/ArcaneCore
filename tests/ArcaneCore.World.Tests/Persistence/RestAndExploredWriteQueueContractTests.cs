using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Progression;
using ArcaneCore.World.WorldState;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Persistence;

/// <summary>
/// Characterisation of what <see cref="RestWriteQueue"/> and <see cref="ExploredZonesWriteQueue"/> expose beyond the retained-write rules
/// (those are pinned by RestWriteQueueTests, ExploredZonesWriteQueueTests and WriteQueuePersistAnomalyTests): the exact fault-ledger sites,
/// the busy-database skip of the per-attempt fault, the constant pause between attempts, the exact anomaly subject/detail text with the literal
/// queue names, the exception message texts, and the terminal ownership-rejection path (host stop and its own fault site).
/// Runs in the non-parallel persist collection because the fault and anomaly ledgers are process-wide.
/// </summary>
[Collection(PersistAnomalyCollection.Name)]
public sealed class RestAndExploredWriteQueueContractTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    // The full World suite records more distinct faults than the shared ledger keeps by default (512); past the cap a new
    // signature is counted into the overflow bucket, so this class lifts the cap to read its own sites whatever ran before it.
    public RestAndExploredWriteQueueContractTests()
        => FaultLedger.Shared.Configure(new DiagnosticsOptions { FaultMaxSignatures = 1_000_000 });

    private sealed class SeamBoom(string message) : Exception(message);

    /// <summary>What differs per queue: the literal names and message texts.</summary>
    private sealed record Spec(
        string Kind,
        string QueueName,
        string SitePrefix,
        string DrainMessageStem,
        string NotDurableStem,
        string StoppedMessage,
        string TerminalLogName,
        bool SkipsBusy);

    public static TheoryData<string> Kinds => new() { "rest", "explored" };

    private static Spec SpecOf(string kind) => kind switch
    {
        "rest" => new Spec(
            "rest", "RestWriteQueue", "writequeue.rest-",
            "rested-state writes did not drain for characters ", "rested-state writes for character ",
            "the rest write queue is stopped; the change was not persisted", "rest", true),
        _ => new Spec(
            "explored", "ExploredZonesWriteQueue", "writequeue.exploredzones-",
            "explored-zones writes did not drain for characters ", "explored-zones writes for character ",
            "the explored-zones write queue is stopped; the change was not persisted", "explored", false),
    };

    /// <summary>Counts every store call; <c>Script</c> decides, per call number (1-based), what the store throws (null: succeeds).</summary>
    private sealed class Seam : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private int _calls;

        public Func<int, Exception?> Script { get; set; } = _ => null;

        public int Calls => Volatile.Read(ref _calls);

        public FakeLifetime Lifetime { get; } = new();

        public Action<int> Write { get; }
        public Func<Task> Flush { get; }
        public Func<Task> Stop { get; }
        public Func<int, Task> FlushCharacter { get; }
        public Task<Exception> Fatal => _fatal.Task;

        private readonly TaskCompletionSource<Exception> _fatal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Seam(string kind, TimeSpan retryDelay)
        {
            var store = new Store(this);
            _provider = new ServiceCollection()
                .AddSingleton<ICharacterRestStore>(store)
                .AddSingleton<IExploredZonesStore>(store)
                .AddSingleton<IHostApplicationLifetime>(Lifetime)
                .BuildServiceProvider();
            IServiceScopeFactory scopes = _provider.GetRequiredService<IServiceScopeFactory>();
            if (kind == "rest")
            {
                var queue = new RestWriteQueue(scopes, NullLogger.Instance) { RetryDelay = retryDelay, RetainedRetryInterval = Timeout.InfiniteTimeSpan };
                queue.FatalFailure += ex => _fatal.TrySetResult(ex);
                queue.Start();
                Write = id => queue.Save(id, new CharacterRestState(50f, 1000, true));
                Flush = queue.FlushAsync;
                Stop = queue.StopAsync;
                FlushCharacter = queue.FlushCharacterAsync;
            }
            else
            {
                var queue = new ExploredZonesWriteQueue(scopes, NullLogger.Instance) { RetryDelay = retryDelay, RetainedRetryInterval = Timeout.InfiniteTimeSpan };
                queue.FatalFailure += ex => _fatal.TrySetResult(ex);
                queue.Start();
                Write = id => queue.Save(id, new uint[64]);
                Flush = queue.FlushAsync;
                Stop = queue.StopAsync;
                FlushCharacter = queue.FlushCharacterAsync;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Script = _ => null;
            try
            {
                await Stop().WaitAsync(Wait);
            }
            catch (InvalidOperationException)
            {
                // a scenario that ends with a retained or rejected write
            }

            await _provider.DisposeAsync();
        }

        private Task Step()
        {
            int call = Interlocked.Increment(ref _calls);
            return Script(call) is { } ex ? throw ex : Task.CompletedTask;
        }

        private sealed class Store(Seam owner) : ICharacterRestStore, IExploredZonesStore
        {
            Task<CharacterRestState?> ICharacterRestStore.LoadAsync(int characterId, CancellationToken cancellationToken) => Task.FromResult<CharacterRestState?>(null);
            Task ICharacterRestStore.SaveAsync(int characterId, CharacterRestState state, CancellationToken cancellationToken) => owner.Step();
            Task ICharacterRestStore.DeleteAsync(int characterId, CancellationToken cancellationToken) => owner.Step();
            Task<uint[]?> IExploredZonesStore.LoadAsync(int characterId, CancellationToken cancellationToken) => Task.FromResult<uint[]?>(null);
            Task IExploredZonesStore.SaveAsync(int characterId, IReadOnlyList<uint> words, CancellationToken cancellationToken) => owner.Step();
            Task IExploredZonesStore.DeleteAsync(int characterId, CancellationToken cancellationToken) => owner.Step();
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private int _stops;

        public Exception? ThrowOnStop { get; set; }

        public int Stops => Volatile.Read(ref _stops);

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            Interlocked.Increment(ref _stops);
            if (ThrowOnStop is { } ex)
            {
                throw ex;
            }
        }
    }

    private static long FaultCount(string site, Type exceptionType)
        => FaultLedger.Shared.Snapshot().Where(f => f.Site == site && f.ExceptionType == exceptionType.FullName).Sum(f => f.Count);

    private static SqliteException Busy() => new("SQLite Error 5: 'database is locked'.", 5);

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task EveryFailedAttempt_IsRecordedAtTheAttemptSite_ThreeTimes(string kind)
    {
        Spec spec = SpecOf(kind);
        await using var seam = new Seam(kind, TimeSpan.Zero);
        seam.Script = _ => new SeamBoom("attempt " + kind);
        long before = FaultCount(spec.SitePrefix + "attempt", typeof(SeamBoom));

        seam.Write(5101);
        await seam.Flush().WaitAsync(Wait);

        Assert.Equal(3, seam.Calls);
        Assert.Equal(3, FaultCount(spec.SitePrefix + "attempt", typeof(SeamBoom)) - before);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ABusyDatabase_IsRecordedPerQueuePolicy_RestSkipsUntilTheLastAttempt_ExploredRecordsEvery(string kind)
    {
        Spec spec = SpecOf(kind);
        await using var seam = new Seam(kind, TimeSpan.Zero);
        seam.Script = _ => Busy();
        long before = FaultCount(spec.SitePrefix + "attempt", typeof(SqliteException));

        seam.Write(5102);
        await seam.Flush().WaitAsync(Wait);

        Assert.Equal(3, seam.Calls);
        // rest skips the busy attempts 1 and 2 and records only the final one; explored records every attempt
        Assert.Equal(spec.SkipsBusy ? 1 : 3, FaultCount(spec.SitePrefix + "attempt", typeof(SqliteException)) - before);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ABusyDatabase_ThatClearsWithinTheInlineRetries_RecordsAttemptFaultsPerQueuePolicy(string kind)
    {
        Spec spec = SpecOf(kind);
        await using var seam = new Seam(kind, TimeSpan.Zero);
        seam.Script = call => call <= 2 ? Busy() : null;
        long before = FaultCount(spec.SitePrefix + "attempt", typeof(SqliteException));

        seam.Write(5103);
        await seam.Flush().WaitAsync(Wait);

        Assert.Equal(3, seam.Calls);
        Assert.Equal(spec.SkipsBusy ? 0 : 2, FaultCount(spec.SitePrefix + "attempt", typeof(SqliteException)) - before);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task TheRetryDelay_IsPausedBetweenAttempts_Constantly(string kind)
    {
        await using var seam = new Seam(kind, TimeSpan.FromMilliseconds(100));
        seam.Script = _ => new SeamBoom("delay");

        long start = Environment.TickCount64;
        seam.Write(5104);
        await seam.Flush().WaitAsync(Wait);
        long elapsed = Environment.TickCount64 - start;

        Assert.Equal(3, seam.Calls);
        Assert.True(elapsed >= 150, $"two pauses of 100 ms were expected between three attempts, took {elapsed} ms"); // lower bound only: timers may fire a little early, never skip
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task RetryExhausted_AnomalyText_NamesTheLiteralQueueAndCharacter(string kind)
    {
        Spec spec = SpecOf(kind);
        await using var seam = new Seam(kind, TimeSpan.Zero);
        seam.Script = _ => new IOException("down");
        using AnomalyCapture capture = PersistAnomalies.Capture();

        seam.Write(5105);
        await seam.Flush().WaitAsync(Wait);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Equal("persist/write-retry-exhausted", notice.Key);
        Assert.Equal("character 5105", notice.Subject);
        Assert.Equal($"queue={spec.QueueName} character=5105 attempts=3 error=IOException", notice.Detail);
        Assert.Equal(AnomalySeverity.Error, notice.Severity);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task WriteAfterStop_AnomalyText_AndTheRetainedFailureMessage(string kind)
    {
        Spec spec = SpecOf(kind);
        await using var seam = new Seam(kind, TimeSpan.Zero);
        await seam.Stop().WaitAsync(Wait);
        using AnomalyCapture capture = PersistAnomalies.Capture();

        seam.Write(5106);

        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Equal("persist/write-dropped-on-shutdown", notice.Key);
        Assert.Equal("character 5106", notice.Subject);
        Assert.Equal($"queue={spec.QueueName} character=5106 reason=queue-stopped", notice.Detail);

        InvalidOperationException notDurable = await Assert.ThrowsAsync<InvalidOperationException>(() => seam.FlushCharacter(5106));
        Assert.Equal($"{spec.NotDurableStem}5106 are not durable", notDurable.Message);
        Assert.Equal(spec.StoppedMessage, Assert.IsType<InvalidOperationException>(notDurable.InnerException).Message);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task StopWithARetainedWrite_ThrowsTheDrainMessage_AndReportsTheUndrainedAnomaly(string kind)
    {
        Spec spec = SpecOf(kind);
        await using var seam = new Seam(kind, TimeSpan.Zero);
        var cause = new IOException("still down");
        seam.Script = _ => cause;
        seam.Write(5107);
        await seam.Flush().WaitAsync(Wait);
        using AnomalyCapture capture = PersistAnomalies.Capture();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => seam.Stop().WaitAsync(Wait));

        Assert.Equal(spec.DrainMessageStem + "5107", ex.Message);
        Assert.Same(cause, ex.InnerException);
        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Equal("persist/write-dropped-on-shutdown", notice.Key);
        Assert.Equal($"queue {spec.QueueName}", notice.Subject);
        Assert.StartsWith($"queue={spec.QueueName} undrainedCharacters=1 firstCharacter=5107 refusedByStopDrain=0 stalledMs=", notice.Detail, StringComparison.Ordinal); // nothing was refused: the one attempt really failed
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task OwnershipRejection_IsFinalOnTheFirstAttempt_RecordsItsSite_StopsTheHost_AndRaisesFatal(string kind)
    {
        Spec spec = SpecOf(kind);
        await using var seam = new Seam(kind, TimeSpan.Zero);
        seam.Script = _ => new CharacterOwnershipRejectedException("world", 1);
        long before = FaultCount(spec.SitePrefix + "ownership", typeof(CharacterOwnershipRejectedException));
        using AnomalyCapture capture = PersistAnomalies.Capture();

        seam.Write(5108);
        Exception fatal = await seam.Fatal.WaitAsync(Wait);
        await seam.Flush().WaitAsync(Wait);

        Assert.IsType<CharacterOwnershipRejectedException>(fatal);
        Assert.Equal(1, seam.Calls);
        Assert.Equal(1, FaultCount(spec.SitePrefix + "ownership", typeof(CharacterOwnershipRejectedException)) - before);
        Assert.Equal(1, seam.Lifetime.Stops);
        AnomalyNotice notice = Assert.Single(capture.Notices);
        Assert.Equal("persist/circuit-open", notice.Key);
        Assert.Equal($"queue {spec.QueueName}", notice.Subject);
        Assert.Equal($"queue={spec.QueueName} reason=terminal-ownership-failure error=CharacterOwnershipRejectedException", notice.Detail);
        Assert.Equal(AnomalySeverity.Warning, notice.Severity);

        InvalidOperationException stop = await Assert.ThrowsAsync<InvalidOperationException>(() => seam.Stop().WaitAsync(Wait));
        Assert.Equal(spec.DrainMessageStem + "5108", stop.Message);
        Assert.Same(fatal, stop.InnerException);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AHostThatFailsToStop_IsRecordedAtTheStopHostSite_AndFatalStillRaised(string kind)
    {
        Spec spec = SpecOf(kind);
        await using var seam = new Seam(kind, TimeSpan.Zero);
        seam.Lifetime.ThrowOnStop = new SeamBoom("stop host " + kind);
        seam.Script = _ => new CharacterOwnershipRejectedException("world", 1);
        long before = FaultCount(spec.SitePrefix + "stop-host", typeof(SeamBoom));

        seam.Write(5109);
        await seam.Fatal.WaitAsync(Wait);

        Assert.Equal(1, seam.Lifetime.Stops);
        Assert.Equal(1, FaultCount(spec.SitePrefix + "stop-host", typeof(SeamBoom)) - before);
    }
}
