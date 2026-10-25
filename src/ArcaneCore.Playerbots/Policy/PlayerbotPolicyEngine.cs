using System.Diagnostics;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.Policy;

/// <summary>
/// The bots' goal policy engine (<c>World:Playerbots:Policy</c>; docs/areas/playbots.md, Goal policy). Advisory: the rule ladder in
/// <see cref="PlayerbotBrain"/> stays the default and the fallback, and every goal a policy promotes is rechecked by its own controller.
/// <para>
/// World thread: a bot's think snapshots its features (<see cref="Submit"/>, at most every <see cref="PlayerbotPolicyOptions.IntervalMs"/>);
/// at the end of the tick <see cref="Dispatch"/> hands each map's snapshots to a worker as one batch. Worker: the policy scores the batch
/// within <see cref="PlayerbotPolicyOptions.TickBudgetMs"/> and publishes each bot's scores to its <see cref="PlayerbotPolicyAgent"/>.
/// A later think uses them while they are fresh (<see cref="PlayerbotPolicyOptions.StaleAfterMs"/>); late, missing or non-finite scores
/// leave that think to the ladder, and every such fallback is counted.
/// </para>
/// </summary>
internal sealed class PlayerbotPolicyEngine : IAsyncDisposable
{
    /// <summary>The anomaly area of the engine's reports (<c>bot-policy/...</c>).</summary>
    internal const string AnomalyArea = "policy";

    /// <summary>Rows one map batch holds; a snapshot past it is dropped (counted) and that think's scores are missing.</summary>
    internal const int MaxBatch = 1024;

    // Rows the worker scores between two budget checks.
    private const int Chunk = 32;

    private readonly PlayerbotPolicyOptions _options;
    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private readonly Dictionary<uint, MapBatch> _maps = []; // world thread
    private readonly List<Task> _flights = [];               // the batches started (locked: shutdown reads it off the world thread)
    private int _disposed;

    private long _submitted, _scored, _dropped, _overruns, _unscored, _errors;
    private long _applied, _agreed, _missing, _stale, _nonFinite, _explored, _floor, _refused;

    /// <summary>Make an engine. Never throws for a bad model: it reports one anomaly and scores with the ladder.</summary>
    internal PlayerbotPolicyEngine(PlayerbotPolicyOptions options, ILogger? logger = null, string? contentRoot = null, IPlayerbotPolicy? policy = null, TimeProvider? time = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _time = time ?? TimeProvider.System;
        if (policy is not null)
        {
            Policy = policy;
            return;
        }

        Policy = RuleBasedPolicy.Instance;
        if (string.IsNullOrWhiteSpace(options.ModelPath)) return;
        string path = options.ModelPath;
        try
        {
            path = Path.GetFullPath(Path.Combine(contentRoot ?? AppContext.BaseDirectory, options.ModelPath));
            PlayerbotModelPolicy model = PlayerbotModelPolicy.Load(path);
            Policy = model;
            ModelLoaded = true;
            _logger?.LogInformation("Playerbot goal policy model {Path} loaded ({Layers} multiply-adds per bot)", path, model.MultiplyAddsPerBot);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // One report, then the ladder: a bad model file must never stop the server or the bots.
            ModelError = error.Message;
            FaultLedger.Shared.Record("playerbot.policy-engine", error);
            AnomalyLedger.Shared.Report("bot-policy", "evaluation-fault", "policy model " + Path.GetFileName(path),
                $"{path}: {error.GetType().Name}: {error.Message}");
            AnomalyLedger.Shared.Report(PlayerbotAnomalies.Prefix + AnomalyArea, "model-load-failed", "policy model " + Path.GetFileName(path),
                $"{path}: {error.GetType().Name}: {error.Message}; the rule ladder scores instead");
            _logger?.LogWarning("Playerbot goal policy model {Path} did not load ({Type}: {Message}); the rule ladder decides", path, error.GetType().Name, error.Message);
        }
    }

    internal PlayerbotPolicyOptions Options => _options;

    /// <summary>The policy that scores (<see cref="RuleBasedPolicy"/> without a model, or when the model did not load).</summary>
    internal IPlayerbotPolicy Policy { get; }

    /// <summary>A model file loaded and scores.</summary>
    internal bool ModelLoaded { get; }

    /// <summary>Why the configured model did not load (null when none was configured or it loaded): the model is disabled.</summary>
    internal string? ModelError { get; }

    /// <summary>Decision telemetry (null unless <see cref="PlayerbotPolicyTelemetryOptions.Enabled"/>; set by the feature).</summary>
    internal PlayerbotPolicyTelemetry? Telemetry { get; set; }

    internal PlayerbotPolicyAgent CreateAgent(ulong botGuid) => new(this, botGuid);

    /// <summary>Queue one bot's snapshot for its map's next batch (world thread). False when the batch is full (the bot's think falls back).</summary>
    internal bool Submit(PlayerbotPolicyAgent agent, uint mapId, ReadOnlySpan<float> features, long sequence)
    {
        if (Volatile.Read(ref _disposed) != 0 || features.Length != PlayerbotPolicySchema.FeatureCount) return false;
        if (!_maps.TryGetValue(mapId, out MapBatch? batch)) _maps[mapId] = batch = new MapBatch();
        if (batch.PendingCount >= MaxBatch)
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }
        features.CopyTo(batch.PendingFeatures.AsSpan(batch.PendingCount * PlayerbotPolicySchema.FeatureCount));
        batch.PendingRows[batch.PendingCount++] = new Row(agent, sequence);
        Interlocked.Increment(ref _submitted);
        return true;
    }

    /// <summary>Start a worker batch for every map with snapshots waiting and no batch still scoring (world thread, once per tick).</summary>
    internal void Dispatch()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_flights) _flights.RemoveAll(flight => flight.IsCompleted);
        foreach (MapBatch batch in _maps.Values)
        {
            if (batch.PendingCount == 0 || Volatile.Read(ref batch.InFlight) != 0) continue;
            // Swap the buffers: the worker owns the flight side until it clears InFlight; the world thread only writes the pending side.
            (batch.FlightFeatures, batch.PendingFeatures) = (batch.PendingFeatures, batch.FlightFeatures);
            (batch.FlightRows, batch.PendingRows) = (batch.PendingRows, batch.FlightRows);
            batch.FlightCount = batch.PendingCount;
            batch.PendingCount = 0;
            Volatile.Write(ref batch.InFlight, 1);
            Task flight = Task.Run(() => Score(batch));
            lock (_flights) _flights.Add(flight);
        }
    }

    /// <summary>Wait for the batches started so far (tests, shutdown).</summary>
    internal Task WhenIdleAsync()
    {
        lock (_flights) return Task.WhenAll(_flights.ToArray());
    }

    private void Score(MapBatch batch)
    {
        const int f = PlayerbotPolicySchema.FeatureCount, g = PlayerbotPolicySchema.GoalCount;
        int count = batch.FlightCount, done = 0;
        try
        {
            long started = _time.GetTimestamp();
            while (done < count)
            {
                int rows = Math.Min(Chunk, count - done);
                Policy.Score(batch.FlightFeatures.AsSpan(done * f, rows * f), rows, batch.Scores.AsSpan(done * g, rows * g));
                for (int i = done; i < done + rows; i++)
                {
                    ReadOnlySpan<float> line = batch.Scores.AsSpan(i * g, g);
                    batch.FlightRows[i].Agent.Publish(new PlayerbotPolicyScores(batch.FlightRows[i].Sequence, line.ToArray()));
                }
                done += rows;
                if (done < count && _time.GetElapsedTime(started).TotalMilliseconds > _options.TickBudgetMs)
                {
                    // Over the tick budget: the rest of the batch is left to the ladder this time.
                    Interlocked.Increment(ref _overruns);
                    Interlocked.Add(ref _unscored, count - done);
                    break;
                }
            }
            if (done == count && _time.GetElapsedTime(started).TotalMilliseconds > _options.TickBudgetMs) Interlocked.Increment(ref _overruns);
            Interlocked.Add(ref _scored, done);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Interlocked.Increment(ref _errors);
            Interlocked.Add(ref _unscored, count - done);
            FaultLedger.Shared.Record("playerbot.policy", error);
            FaultLedger.Shared.Record("playerbot.policy-engine", error);
            AnomalyLedger.Shared.Report("bot-policy", "evaluation-fault", Policy.Name, $"{error.GetType().Name}: {error.Message}");
            _logger?.LogWarning(error, "Playerbot goal policy {Policy} failed scoring a batch of {Count} ({Type}); the ladder decides", Policy.Name, count, error.GetType().Name);
        }
        finally
        {
            Array.Clear(batch.FlightRows, 0, count); // drop the agent references
            Volatile.Write(ref batch.InFlight, 0);
        }
    }

    // Per-think counters (world thread, PlayerbotPolicyAgent.Decide and Refuse).
    internal void CountApplied() => Interlocked.Increment(ref _applied);
    internal void CountAgreed() => Interlocked.Increment(ref _agreed);
    internal void CountMissing() => Interlocked.Increment(ref _missing);
    internal void CountStale() => Interlocked.Increment(ref _stale);
    internal void CountNonFinite() => Interlocked.Increment(ref _nonFinite);
    internal void CountExplored() => Interlocked.Increment(ref _explored);
    internal void CountFloor() => Interlocked.Increment(ref _floor);
    internal void CountRefused() => Interlocked.Increment(ref _refused);

    internal PlayerbotPolicyStats Stats => new(Policy.Name, ModelLoaded, ModelError,
        Interlocked.Read(ref _submitted), Interlocked.Read(ref _scored), Interlocked.Read(ref _dropped), Interlocked.Read(ref _overruns),
        Interlocked.Read(ref _unscored), Interlocked.Read(ref _errors), Interlocked.Read(ref _applied), Interlocked.Read(ref _agreed),
        Interlocked.Read(ref _missing), Interlocked.Read(ref _stale), Interlocked.Read(ref _nonFinite), Interlocked.Read(ref _explored),
        Interlocked.Read(ref _floor), Interlocked.Read(ref _refused));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException) { }
        if (Telemetry is { } telemetry)
        {
            Telemetry = null;
            await telemetry.DisposeAsync().ConfigureAwait(false);
        }
    }

    private readonly record struct Row(PlayerbotPolicyAgent Agent, long Sequence);

    private sealed class MapBatch
    {
        public float[] PendingFeatures = new float[MaxBatch * PlayerbotPolicySchema.FeatureCount];
        public float[] FlightFeatures = new float[MaxBatch * PlayerbotPolicySchema.FeatureCount];
        public Row[] PendingRows = new Row[MaxBatch];
        public Row[] FlightRows = new Row[MaxBatch];
        public readonly float[] Scores = new float[MaxBatch * PlayerbotPolicySchema.GoalCount];
        public int PendingCount;
        public int FlightCount;
        public int InFlight;
    }
}

/// <summary>One bot's scores for one snapshot (immutable; published by the worker).</summary>
internal sealed record PlayerbotPolicyScores(long Sequence, float[] Values);

/// <summary>
/// The engine's counters (the feature's five-minute log line, tests). <c>Applied</c> to <c>Refused</c> count thinks, not snapshots: every
/// think on a snapshot decides again, and a refused promotion counts once as <c>Refused</c> on top of the think's own source.
/// </summary>
internal readonly record struct PlayerbotPolicyStats(string Policy, bool ModelLoaded, string? ModelError,
    long Submitted, long Scored, long Dropped, long Overruns, long Unscored, long Errors,
    long Applied, long Agreed, long Missing, long Stale, long NonFinite, long Explored, long Floor, long Refused);
