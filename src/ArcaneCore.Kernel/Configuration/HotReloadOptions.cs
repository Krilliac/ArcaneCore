namespace ArcaneCore.Kernel.Configuration;

/// <summary>
/// The <c>HotReload</c> configuration section. Live reload is operator-initiated only (the
/// <c>.reload</c> chat commands, like vmangos' <c>reload</c> command tree); nothing reloads by
/// itself, and every default below keeps the process behaving exactly as it did without this
/// feature.
/// </summary>
public sealed class HotReloadOptions
{
    public const string SectionName = "HotReload";

    /// <summary>
    /// Whether the <c>.reload</c> commands act. Default true: retail ships them enabled for
    /// administrators (vmangos Chat.cpp:1212 registers the <c>reload</c> root). When false the
    /// command still exists but replies that reloading is disabled.
    /// </summary>
    public bool Commands { get; set; } = true;

    /// <summary>How long a candidate may take to build (database read plus indexing) before the reload is abandoned. 0 = no limit.</summary>
    public int BuildTimeoutMs { get; set; } = 60_000;

    /// <summary>
    /// How long to wait for the world thread to run the swap before abandoning the reload. A swap
    /// that has not started by then is cancelled and never runs later. 0 = no limit.
    /// </summary>
    public int CommitTimeoutMs { get; set; } = 30_000;
}
