namespace ArcaneCore.World.Clustering;

/// <summary>Configuration for the one durable writer authority of the characters database.</summary>
public sealed class CharacterFencingOptions
{
    public const string SectionName = "Cluster:Fencing";

    /// <summary>Enable durable character ownership fencing at world startup; disabled by default. Mandatory in worker mode (Cluster:Worker:Enabled): a worker with fencing off refuses to start.</summary>
    public bool Enabled { get; set; }

    /// <summary>The durable owner identity; defaults to the current machine name and must be stable while the world owns the database.</summary>
    public string OwnerNode { get; set; } = Environment.MachineName;

    /// <summary>The positive ownership epoch to compare and swap during an explicit takeover; pair with <see cref="TakeoverIncarnation"/>.</summary>
    public long? TakeoverEpoch { get; set; }

    /// <summary>The non-empty ownership incarnation to compare and swap during an explicit takeover; pair with <see cref="TakeoverEpoch"/>.</summary>
    public Guid? TakeoverIncarnation { get; set; }

    public const int DefaultClaimWatchIntervalSeconds = 10;

    /// <summary>With fencing disabled, how often the world reads the characters ownership row to notice that a fenced world has claimed the database, in seconds (1 to 300). Independent of autosave; an unfenced world can keep writing for up to this long after a claim.</summary>
    public int ClaimWatchIntervalSeconds { get; set; } = DefaultClaimWatchIntervalSeconds;

    public bool HasTakeover => TakeoverEpoch is not null || TakeoverIncarnation is not null;

    /// <summary>
    /// Set (never bound from configuration) when worker mode (<c>Cluster:Worker:Enabled</c>) is on: a worker's players can be handed to another
    /// world, so its characters writes must carry the ownership epoch, and <see cref="Validate"/> refuses a worker with fencing off. The characters
    /// write fence is attached in worker mode either way (<c>DataServiceCollectionExtensions.IsCharacterWriteFenceRequired</c>).
    /// </summary>
    public bool RequiredByWorkerMode { get; internal set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OwnerNode))
            throw new InvalidOperationException("Cluster:Fencing:OwnerNode must not be empty");
        if (OwnerNode.Length > 128)
            throw new InvalidOperationException("Cluster:Fencing:OwnerNode must be at most 128 characters");
        if (ClaimWatchIntervalSeconds is < 1 or > 300)
            throw new InvalidOperationException("Cluster:Fencing:ClaimWatchIntervalSeconds must be between 1 and 300");
        if (RequiredByWorkerMode && !Enabled)
            throw new InvalidOperationException("Cluster:Worker:Enabled requires Cluster:Fencing:Enabled = true: in worker mode every characters write must carry the ownership epoch so a handoff can fence the old world");
        if (!Enabled && HasTakeover)
            throw new InvalidOperationException("Cluster:Fencing takeover cannot be configured while fencing is disabled");
        if (HasTakeover && (TakeoverEpoch is not > 0 || TakeoverIncarnation is null || TakeoverIncarnation == Guid.Empty))
            throw new InvalidOperationException("Cluster:Fencing takeover requires a positive epoch and non-empty incarnation together");
    }
}
