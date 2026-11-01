using System.Net;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Content;
using ArcaneCore.Kernel.Cluster;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Cluster.Gateway;

namespace ArcaneCore.Gateway;

/// <summary>
/// The <c>Gateway</c> section of the gateway daemon (docs/areas/gateway.md). The gateway also reads <c>Database:Auth:*</c>
/// (the realm's auth database: session keys, account status, bans), <c>Net:Protection:*</c> (the same transport protections
/// as the world listener) and <c>Logging</c>.
/// </summary>
[ConfigDaemons(ConfigDaemon.Gateway)]
public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>Address the client listener binds.</summary>
    [ConfigAllowed("an IPv4 or IPv6 address such as 0.0.0.0, 127.0.0.1 or ::1 (host names are not resolved)")]
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Port clients connect to; the realm list must name it (realmlist address) instead of the world's World:Port.</summary>
    public int Port { get; set; } = 8086;

    /// <summary>Most simultaneous client connections; 0 means no cap of the gateway's own (Net:Protection still applies).</summary>
    public int MaxConnections { get; set; }

    /// <summary>Most simultaneous client connections from one address; 0 means no cap of the gateway's own.</summary>
    public int MaxConnectionsPerIp { get; set; }

    /// <summary>A connection that has not sent a valid CMSG_AUTH_SESSION within this time is closed (the world's World:PreAuthTimeout rule).</summary>
    public TimeSpan PreAuthTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>A client whose unsent data exceeds this many bytes (it stopped reading) is disconnected (World:MaxOutboundBytes).</summary>
    public long MaxOutboundBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// The world workers this gateway links to, one link each (node id, address, port, optional maps). When empty, the single-worker keys
    /// <see cref="WorkerAddress"/> and <see cref="WorkerPort"/> describe one entry named <see cref="LegacyWorkerNodeId"/>; when set, those two
    /// keys are ignored. Every worker must accept the same <see cref="LinkSecret"/>, <see cref="RealmId"/> and manifest values. An entry may also name a standby
    /// process (StandbyNodeId, StandbyAddress, StandbyPort), the target of a whole-World handoff of that slot (needs Gateway:Handoff:Enabled).
    /// </summary>
    public List<GatewayWorkerEntry> Workers { get; set; } = [];

    /// <summary>
    /// Node id of the worker new sessions open on (it must name a <see cref="Workers"/> entry, or <see cref="LegacyWorkerNodeId"/> in single-worker mode). While it is
    /// down, sessions open on the ready link with the fewest sessions. Empty, the default, designates none: sessions always spread over the ready links.
    /// </summary>
    public string LoginWorker { get; set; } = string.Empty;

    /// <summary>Node id given to the entry made from <see cref="WorkerAddress"/> and <see cref="WorkerPort"/>.</summary>
    public const string LegacyWorkerNodeId = "worker";

    /// <summary>Address of the world worker's gateway listener (Cluster:Worker:BindAddress). Used only while <see cref="Workers"/> is empty.</summary>
    public string WorkerAddress { get; set; } = "127.0.0.1";

    /// <summary>Port of the world worker's gateway listener (Cluster:Worker:Port). Used only while <see cref="Workers"/> is empty.</summary>
    public int WorkerPort { get; set; } = 8095;

    /// <summary>The shared link secret (Cluster:Worker:LinkSecret on the worker): at least 16 characters, or the gateway refuses to start.</summary>
    public string LinkSecret { get; set; } = string.Empty;

    /// <summary>First wait before reconnecting a lost or refused worker link; doubles up to 30 s.</summary>
    public TimeSpan LinkRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>This gateway's node id in the link manifest.</summary>
    public string NodeId { get; set; } = "gateway";

    /// <summary>Must equal the worker's Cluster:Worker:RealmId.</summary>
    public int RealmId { get; set; } = 1;

    /// <summary>The content revision this gateway advertises; must equal the worker's. Defaults to the revision derived from the world schema version this build targets (<see cref="GatewayManifestVersions.ContentRevisionFor"/>), which is what a default worker (Cluster:Worker:VersionsFromDatabase on) advertises once its database is at that version. Set it only to pin an explicit override (then set the worker's labels and Cluster:Worker:VersionsFromDatabase=false to match).</summary>
    public string ContentRevision { get; set; } = GatewayManifestVersions.ContentRevisionFor(WorldDbContext.Schema.CurrentVersion);

    /// <summary>The characters schema version this gateway advertises; defaults to the version this build targets. Explicit override only.</summary>
    public int CharactersSchemaVersion { get; set; } = CharacterDbContext.Schema.CurrentVersion;

    /// <summary>The world schema version this gateway advertises; defaults to the version this build targets. Explicit override only.</summary>
    public int WorldSchemaVersion { get; set; } = WorldDbContext.Schema.CurrentVersion;

    public ClusterNodeManifest ToManifest(Guid incarnation) => GatewayLinkManifest.Create(
        ClusterRole.Gateway, NodeId, incarnation, RealmId, ContentRevision, CharactersSchemaVersion, WorldSchemaVersion);

    /// <summary>The worker entries the gateway links to: <see cref="Workers"/>, or one entry made from the single-worker keys.</summary>
    public IReadOnlyList<GatewayWorkerEntry> EffectiveWorkers() => Workers.Count > 0
        ? Workers
        : [new GatewayWorkerEntry { NodeId = LegacyWorkerNodeId, Address = WorkerAddress, Port = WorkerPort }];

    /// <summary>Every problem with <see cref="Workers"/> (empty when it is empty: the single-worker keys are validated instead).</summary>
    public IReadOnlyList<string> ValidateWorkers()
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < Workers.Count; index++)
        {
            GatewayWorkerEntry? entry = Workers[index];
            string prefix = $"{SectionName}:Workers:{index}";
            if (entry is null)
            {
                problems.Add($"{prefix}: must not be empty.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.NodeId))
            {
                problems.Add($"{prefix}:NodeId: must not be empty.");
            }
            else if (!seen.Add(entry.NodeId))
            {
                problems.Add($"{prefix}:NodeId: '{entry.NodeId}' is used by an earlier entry.");
            }

            if (string.IsNullOrWhiteSpace(entry.Address) || !IPAddress.TryParse(entry.Address, out _))
            {
                problems.Add($"{prefix}:Address: must be a valid IP address.");
            }

            if (entry.Port is < 1 or > IPEndPoint.MaxPort)
            {
                problems.Add($"{prefix}:Port: must be 1..{IPEndPoint.MaxPort}.");
            }

            if (entry.Maps is null || entry.Maps.Any(map => map < 0))
            {
                problems.Add($"{prefix}:Maps: map ids must not be negative.");
            }

            if (!string.IsNullOrWhiteSpace(entry.StandbyNodeId))
            {
                if (!seen.Add(entry.StandbyNodeId))
                {
                    problems.Add($"{prefix}:StandbyNodeId: '{entry.StandbyNodeId}' is used by another node id.");
                }

                if (string.IsNullOrWhiteSpace(entry.StandbyAddress) || !IPAddress.TryParse(entry.StandbyAddress, out _))
                {
                    problems.Add($"{prefix}:StandbyAddress: must be a valid IP address.");
                }

                if (entry.StandbyPort is < 1 or > IPEndPoint.MaxPort)
                {
                    problems.Add($"{prefix}:StandbyPort: must be 1..{IPEndPoint.MaxPort}.");
                }
            }
        }

        return problems;
    }

    /// <summary>Every problem with the bound values, as <c>Gateway:Key: problem</c> lines; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(BindAddress) || !IPAddress.TryParse(BindAddress, out _))
        {
            problems.Add($"{SectionName}:BindAddress: must be a valid IP address.");
        }

        if (Port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            problems.Add($"{SectionName}:Port: must be {IPEndPoint.MinPort}..{IPEndPoint.MaxPort}.");
        }

        if (MaxConnections < 0)
        {
            problems.Add($"{SectionName}:MaxConnections: must be 0 (unlimited) or positive.");
        }

        if (MaxConnectionsPerIp < 0)
        {
            problems.Add($"{SectionName}:MaxConnectionsPerIp: must be 0 (unlimited) or positive.");
        }

        if (PreAuthTimeout < TimeSpan.Zero)
        {
            problems.Add($"{SectionName}:PreAuthTimeout: must be 0 (disabled) or positive.");
        }

        if (MaxOutboundBytes <= 0)
        {
            problems.Add($"{SectionName}:MaxOutboundBytes: must be positive.");
        }

        if (Workers.Count > 0)
        {
            problems.AddRange(ValidateWorkers());
        }
        else
        {
            if (string.IsNullOrWhiteSpace(WorkerAddress) || !IPAddress.TryParse(WorkerAddress, out _))
            {
                problems.Add($"{SectionName}:WorkerAddress: must be a valid IP address.");
            }

            if (WorkerPort is < 1 or > IPEndPoint.MaxPort)
            {
                problems.Add($"{SectionName}:WorkerPort: must be 1..{IPEndPoint.MaxPort}.");
            }
        }

        if (!string.IsNullOrWhiteSpace(LoginWorker) && !EffectiveWorkers().Any(entry => string.Equals(entry?.NodeId, LoginWorker, StringComparison.Ordinal)))
        {
            problems.Add($"{SectionName}:LoginWorker: '{LoginWorker}' names no worker entry.");
        }

        if (LinkRetryDelay < TimeSpan.Zero)
        {
            problems.Add($"{SectionName}:LinkRetryDelay: must not be negative.");
        }

        if (string.IsNullOrWhiteSpace(NodeId))
        {
            problems.Add($"{SectionName}:NodeId: must not be empty.");
        }

        if (RealmId <= 0)
        {
            problems.Add($"{SectionName}:RealmId: must be positive.");
        }

        if (string.IsNullOrWhiteSpace(ContentRevision))
        {
            problems.Add($"{SectionName}:ContentRevision: must not be empty.");
        }

        if (CharactersSchemaVersion <= 0)
        {
            problems.Add($"{SectionName}:CharactersSchemaVersion: must be positive.");
        }

        if (WorldSchemaVersion <= 0)
        {
            problems.Add($"{SectionName}:WorldSchemaVersion: must be positive.");
        }

        return problems;
    }
}

/// <summary>One world worker the gateway links to (<c>Gateway:Workers:N</c>).</summary>
public sealed class GatewayWorkerEntry
{
    /// <summary>The worker's name in logs, metrics and link state; unique among the entries.</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Address of the worker's gateway listener (its Cluster:Worker:BindAddress).</summary>
    public string Address { get; set; } = "127.0.0.1";

    /// <summary>Port of the worker's gateway listener (its Cluster:Worker:Port).</summary>
    public int Port { get; set; } = 8095;

    /// <summary>Map ids this worker owns; empty means it is not map-restricted. Surfaced in the link state for map-aware routing.</summary>
    public int[] Maps { get; set; } = [];

    /// <summary>
    /// The standby process of this worker's slot (its Cluster:Worker:NodeId, with Cluster:Worker:Standby on): the target of a whole-World handoff of this slot. Empty, the default, means the
    /// slot has no standby and cannot be handed off. The gateway keeps a session-less control link to it (needs Gateway:Handoff:Enabled); it must differ from every other node id.
    /// </summary>
    public string StandbyNodeId { get; set; } = string.Empty;

    /// <summary>Address of the standby's gateway listener (its Cluster:Worker:BindAddress). Used only when <see cref="StandbyNodeId"/> is set.</summary>
    public string StandbyAddress { get; set; } = "127.0.0.1";

    /// <summary>Port of the standby's gateway listener (its Cluster:Worker:Port). Used only when <see cref="StandbyNodeId"/> is set.</summary>
    public int StandbyPort { get; set; } = 8096;
}

/// <summary>
/// The opt-in key of the gateway's whole-World handoff hosting (<c>Gateway:Handoff:Enabled</c>, design _wf/handoff-control-DESIGN.md sections 2.6 and 3). Off, the default: the gateway
/// opens no characters database connection, no standby link and no control channel, and behaves exactly as before.
/// </summary>
[ConfigDaemons(ConfigDaemon.Gateway)]
public sealed class GatewayHandoffOptions
{
    public const string SectionName = "Gateway:Handoff";

    /// <summary>`true` lets this gateway coordinate whole-World handoffs and finish interrupted ones from the journal: it connects to the characters database (Database:Characters:*, schema validation only, nothing is created or upgraded), keeps a control link to each slot's standby (Gateway:Workers:N:StandbyNodeId) and opens a control channel to every worker. The workers must have Cluster:Worker:Handoff:Enabled on. `false`, the default, changes nothing.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often the gateway looks for unresolved handoffs in the journal and recovers them (before the epoch takeover they abort, after it they roll forward). A failing recovery backs off up to 60 s.</summary>
    public TimeSpan RecoveryInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a handoff may stay unresolved before the gateway raises an error-severity anomaly (it keeps retrying).</summary>
    public TimeSpan AnomalyAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Every problem with the bound values, as <c>Gateway:Handoff:Key: problem</c> lines; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (RecoveryInterval <= TimeSpan.Zero)
        {
            problems.Add($"{SectionName}:RecoveryInterval: must be positive.");
        }

        if (AnomalyAfter <= TimeSpan.Zero)
        {
            problems.Add($"{SectionName}:AnomalyAfter: must be positive.");
        }

        return problems;
    }
}

/// <summary>
/// How long the gateway waits for the worker's reply to each control verb (<c>Gateway:Control:*</c>, design _wf/handoff-control-DESIGN.md section 2.4). Bound to
/// <see cref="ArcaneCore.World.Cluster.Control.LinkControlTimeouts"/> by the hosting. Read only while <c>Gateway:Handoff:Enabled</c> is on.
/// </summary>
[ConfigDaemons(ConfigDaemon.Gateway)]
public sealed class GatewayControlOptions
{
    public const string SectionName = "Gateway:Control";

    /// <summary>Reply timeout of the quick verbs: Describe, Warm, StopAdmissions, Resume, ReleaseWriterLock, Retire, TargetDescribe, Reserve, Install, Discard, ResumeSource, RetireSource. Default 10 s.</summary>
    public TimeSpan Default { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Reply timeout of Freeze and Suspend (the world thread plus a durable flush). Default 30 s.</summary>
    public TimeSpan Freeze { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Reply timeout of Flush (every captured save made durable). Default 60 s.</summary>
    public TimeSpan Flush { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Reply timeout of Activate (the target starts the retail arrival). Default 60 s.</summary>
    public TimeSpan Activate { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Reply timeout of Promote (the standby takes the writer lock and runs as the active world). Default 120 s.</summary>
    public TimeSpan Promote { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>The timeouts the link control channels of this gateway use.</summary>
    public ArcaneCore.World.Cluster.Control.LinkControlTimeouts ToTimeouts() => new()
    {
        Default = Default,
        Freeze = Freeze,
        Flush = Flush,
        Activate = Activate,
        Promote = Promote,
    };

    /// <summary>Every problem with the bound values, as <c>Gateway:Control:Key: problem</c> lines; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        Check(Default, nameof(Default), problems);
        Check(Freeze, nameof(Freeze), problems);
        Check(Flush, nameof(Flush), problems);
        Check(Activate, nameof(Activate), problems);
        Check(Promote, nameof(Promote), problems);
        return problems;

        static void Check(TimeSpan value, string name, List<string> problems)
        {
            if (value <= TimeSpan.Zero)
            {
                problems.Add($"{SectionName}:{name}: must be positive.");
            }
        }
    }
}
