namespace ArcaneCore.World.HotCode;

/// <summary>A point-in-time copy of <see cref="HotCodeState"/> (what <c>.hotcode status</c> prints).</summary>
public sealed record HotCodeStatus(
    long Generation,
    long AppliedGeneration,
    bool Frozen,
    bool Degraded,
    string? DegradedReason,
    long MetadataUpdates,
    long Applied,
    long Rejected,
    DateTimeOffset? LastRefreshUtc)
{
    /// <summary>
    /// The running process no longer matches the build on disk: at least one code edit was applied
    /// in memory and cannot be rolled back. A restart returns it to the built state.
    /// </summary>
    public bool DivergedFromBuild => MetadataUpdates > 0;
}

/// <summary>
/// Thread-safe bookkeeping of the code hot reload: the refresh generation (idempotency), whether
/// refreshing is frozen, whether the last refresh was rejected (the old registries are then still
/// in force), and how many code edits the runtime applied.
/// </summary>
public sealed class HotCodeState
{
    private readonly Lock _gate = new();
    private long _generation;
    private long _appliedGeneration;
    private bool _frozen;
    private string? _degradedReason;
    private long _metadataUpdates;
    private long _applied;
    private long _rejected;
    private DateTimeOffset? _lastRefreshUtc;

    /// <summary>A fresh generation number for a refresh (monotonic, starts at 1).</summary>
    public long NextGeneration()
    {
        lock (_gate)
        {
            return ++_generation;
        }
    }

    public long AppliedGeneration
    {
        get
        {
            lock (_gate)
            {
                return _appliedGeneration;
            }
        }
    }

    public bool Frozen
    {
        get
        {
            lock (_gate)
            {
                return _frozen;
            }
        }
    }

    /// <summary>Stop (or resume) registry refreshes. Applied code edits are unaffected: the runtime applies those itself.</summary>
    public void SetFrozen(bool frozen)
    {
        lock (_gate)
        {
            _frozen = frozen;
        }
    }

    /// <summary>The runtime reported an applied metadata update (a code edit is now live).</summary>
    public void RecordMetadataUpdate()
    {
        lock (_gate)
        {
            _metadataUpdates++;
        }
    }

    /// <summary>A refresh finished (applied, or found nothing new) for <paramref name="generation"/>.</summary>
    public void RecordSuccess(long generation, bool changedRegistries, DateTimeOffset now)
    {
        lock (_gate)
        {
            _appliedGeneration = Math.Max(_appliedGeneration, generation);
            _degradedReason = null;
            _lastRefreshUtc = now;
            if (changedRegistries)
            {
                _applied++;
            }
        }
    }

    /// <summary>A refresh was rejected: nothing was changed, the old registries are still in force.</summary>
    public void RecordRejected(string reason, DateTimeOffset now)
    {
        lock (_gate)
        {
            _degradedReason = reason;
            _lastRefreshUtc = now;
            _rejected++;
        }
    }

    public HotCodeStatus Snapshot()
    {
        lock (_gate)
        {
            return new HotCodeStatus(
                _generation, _appliedGeneration, _frozen, _degradedReason is not null, _degradedReason,
                _metadataUpdates, _applied, _rejected, _lastRefreshUtc);
        }
    }
}
