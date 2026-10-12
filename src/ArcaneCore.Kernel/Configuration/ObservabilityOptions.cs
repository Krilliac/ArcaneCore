namespace ArcaneCore.Kernel.Configuration;

/// <summary>
/// The <c>Ops:Observability</c> section: the client-visible failure log and the duplicate outbound packet detector of the world
/// daemon (docs/ops/observability.md). Both run on the send path, so both are bounded: a per-session token bucket limits the
/// lines, and nothing is formatted or allocated unless a line is going to be written. Read once when the daemon starts
/// (restart-only). No vmangos equivalent.
/// </summary>
[ConfigDaemons(ConfigDaemon.World)]
public sealed class ObservabilityOptions
{
    public const string SectionName = "Ops:Observability";

    /// <summary>One structured line for every refusal the server sends a client and for handlers that drop a request without answering (logger category <c>ArcaneCore.ClientRejections</c>).</summary>
    public ClientRejectionOptions ClientRejections { get; } = new();

    /// <summary>The per-session detector for an identical packet sent twice in a short window (logger category <c>ArcaneCore.DuplicatePackets</c>).</summary>
    public DuplicatePacketOptions DuplicatePackets { get; } = new();
}

/// <summary>Client-visible failure log (<c>Ops:Observability:ClientRejections</c>).</summary>
public sealed class ClientRejectionOptions
{
    /// <summary><c>false</c>: no refusal is decoded or logged and the send path does no extra work. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Lines a session may log in a burst before the refill rate applies. 1-1000. Default 20.</summary>
    public int BurstPerSession { get; set; } = 20;

    /// <summary>Lines a session earns back per minute once its burst is spent; a refusal over the limit is counted and summarised in the next line that is written. 1-6000. Default 60.</summary>
    public int RefillPerMinute { get; set; } = 60;

    /// <summary><c>true</c>: handlers that return without answering the client log the reason (at Debug for a bot, Information for a player). Default true.</summary>
    public bool LogSilentDrops { get; set; } = true;
}

/// <summary>Duplicate outbound packet detector (<c>Ops:Observability:DuplicatePackets</c>).</summary>
public sealed class DuplicatePacketOptions
{
    /// <summary><c>false</c>: nothing is hashed and the send path does no extra work. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary><c>true</c>: playerbot sessions are checked as well. Default false (bots are excluded).</summary>
    public bool IncludeBots { get; set; }

    /// <summary>Two identical packets closer together than this many milliseconds are a duplicate (a world tick is 50 ms). 1-5000. Default 100.</summary>
    public int WindowMs { get; set; } = 100;

    /// <summary>Recent packets remembered per session; older ones are overwritten. 4-256. Default 16.</summary>
    public int RingSize { get; set; } = 16;

    /// <summary>Warnings a session may log in a burst. 1-100. Default 3.</summary>
    public int WarnBurstPerSession { get; set; } = 3;

    /// <summary>Warnings a session earns back per minute after its burst. 1-600. Default 6.</summary>
    public int WarnPerMinute { get; set; } = 6;

    /// <summary><c>true</c>: the warning carries a compact call stack of the second send, and of the first as well once the opcode has duplicated before in this process. Default true.</summary>
    public bool CaptureStacks { get; set; } = true;
}
