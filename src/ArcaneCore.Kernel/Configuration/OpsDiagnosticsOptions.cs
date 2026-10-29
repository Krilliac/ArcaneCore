namespace ArcaneCore.Kernel.Configuration;

/// <summary>
/// The <c>Ops:Diagnostics</c> section: the operator diagnostics that run inside the world process (<c>.debug gc</c>,
/// <c>.debug alloc</c>, <c>.debug dump</c>; docs/ops/diagnostics.md). Only the process dump needs configuration, because it is
/// the one that can leak secrets and fill a disk. Read once when the daemon starts (restart-only). Not to be confused with the
/// <c>Diagnostics</c> section, which sets what a failed invariant or an unhandled exception does. No vmangos equivalent.
/// </summary>
[ConfigDaemons(ConfigDaemon.World)]
public sealed class OpsDiagnosticsOptions
{
    public const string SectionName = "Ops:Diagnostics";

    /// <summary>The process-dump command (<c>.debug dump</c>).</summary>
    public DumpDiagnosticsOptions Dumps { get; } = new();
}

/// <summary>Process dumps written by <c>.debug dump</c> (<c>Ops:Diagnostics:Dumps</c>).</summary>
public sealed class DumpDiagnosticsOptions
{
    /// <summary>
    /// <c>true</c> lets an administrator write a dump of the running world with <c>.debug dump</c>. Default false, because a dump holds
    /// everything in memory: session keys, password material in flight, character data. Keep the dump directory private and delete the
    /// files when the analysis is done.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The directory the dump files are written to. Empty (default) means <c>dumps</c> beside the log file
    /// (the resolved <c>Logging:ArcaneCore:File:Path</c>, so <c>logs/dumps</c> by default). It is created on the first dump.
    /// </summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>The least seconds between two dumps, so a repeated command cannot suspend the process again and again or fill the disk. 0 disables the limit. Default 600.</summary>
    public int MinIntervalSeconds { get; set; } = 600;
}
