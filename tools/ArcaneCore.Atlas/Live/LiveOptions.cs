namespace ArcaneCore.Atlas.Live;

/// <summary>
/// How the live client reaches a feed and how long it waits. Everything has a working default; tests inject the pipe name and shorter
/// timeouts.
/// </summary>
public sealed record LiveOptions
{
    /// <summary>The defaults: the real pipe naming, discovery over <c>\\.\pipe\</c>, 5 s for hello and queries, 8 MiB lines.</summary>
    public static LiveOptions Default { get; } = new();

    /// <summary>The pipe name for a world daemon's pid. Default <c>arcanecore-mapfeed-{pid}</c>.</summary>
    public Func<int, string> PipeNameFor { get; init; } = LiveProtocol.PipeName;

    /// <summary>How feeds are found when <c>live.connect</c> is given no pid.</summary>
    public IFeedDiscovery Discovery { get; init; } = new PipeFeedDiscovery();

    /// <summary>How long to wait for the pipe to accept a connection.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long to wait for the server's hello reply.</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a <c>live.query</c> waits for its result.</summary>
    // ref: ATLAS-2026-10-18.md:220-221 (query id echoed in the result) and the lane brief ("correlated by id, 5 s timeout")
    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a write to the pipe may take before the connection is treated as stuck.</summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Longest line accepted from the server, in bytes.</summary>
    public int MaxLineBytes { get; init; } = LiveProtocol.MaxLineBytes;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(PipeNameFor);
        ArgumentNullException.ThrowIfNull(Discovery);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ConnectTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(HelloTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(QueryTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(WriteTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxLineBytes, 1024);
    }
}
