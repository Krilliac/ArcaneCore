namespace ArcaneCore.World.Clustering;

/// <summary>Configuration for the one durable writer authority of the characters database.</summary>
public sealed class CharacterFencingOptions
{
    public const string SectionName = "Cluster:Fencing";

    /// <summary>Enable durable character ownership fencing at world startup; disabled by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>The durable owner identity; defaults to the current machine name and must be stable while the world owns the database.</summary>
    public string OwnerNode { get; set; } = Environment.MachineName;

    /// <summary>The positive ownership epoch to compare and swap during an explicit takeover; pair with <see cref="TakeoverIncarnation"/>.</summary>
    public long? TakeoverEpoch { get; set; }

    /// <summary>The non-empty ownership incarnation to compare and swap during an explicit takeover; pair with <see cref="TakeoverEpoch"/>.</summary>
    public Guid? TakeoverIncarnation { get; set; }

    /// <summary>With fencing disabled, how often the world reads the characters ownership row to notice that a fenced world has claimed the database, in seconds (1 to 300). Independent of autosave; an unfenced world can keep writing for up to this long after a claim.</summary>
    public int ClaimWatchIntervalSeconds { get; set; } = DefaultClaimWatchIntervalSeconds;

    public const int DefaultClaimWatchIntervalSeconds = 10;

    public bool HasTakeover => TakeoverEpoch is not null || TakeoverIncarnation is not null;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OwnerNode))
            throw new InvalidOperationException("Cluster:Fencing:OwnerNode must not be empty");
        if (OwnerNode.Length > 128)
            throw new InvalidOperationException("Cluster:Fencing:OwnerNode must be at most 128 characters");
        if (ClaimWatchIntervalSeconds is < 1 or > 300)
            throw new InvalidOperationException("Cluster:Fencing:ClaimWatchIntervalSeconds must be between 1 and 300");
        if (!Enabled && HasTakeover)
            throw new InvalidOperationException("Cluster:Fencing takeover cannot be configured while fencing is disabled");
        if (HasTakeover && (TakeoverEpoch is not > 0 || TakeoverIncarnation is null || TakeoverIncarnation == Guid.Empty))
            throw new InvalidOperationException("Cluster:Fencing takeover requires a positive epoch and non-empty incarnation together");
    }
}
