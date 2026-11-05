using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Cluster.Gateway;
using ArcaneCore.World.Cluster.Social;

namespace ArcaneCore.World.Clustering;

/// <summary>Which durable ownership scope a fenced world claims (<c>Cluster:Fencing:Scope</c>).</summary>
public enum CharacterFencingScope
{
    /// <summary>The realm-wide <c>world</c> row: one writer for the whole characters database (the Combined world, and today's single worker). The default.</summary>
    Realm = 0,

    /// <summary>One <c>worker:&lt;shard&gt;</c> row per map worker, so several workers write the same database at once (design _wf/writer-lock-DESIGN.md).</summary>
    Worker = 1,
}

/// <summary>A Worker-scope configuration that can never start (bad shard, SQLite without the test switch, a role or map list that does not fit); a refusal, not a crash.</summary>
public sealed class WorkerScopeConfigurationException(string message) : InvalidOperationException(message);

/// <summary>Configuration for the one durable writer authority of the characters database.</summary>
public sealed class CharacterFencingOptions
{
    public const string SectionName = "Cluster:Fencing";

    /// <summary>Enable durable character ownership fencing at world startup; disabled by default. Mandatory in worker mode (Cluster:Worker:Enabled): a worker with fencing off refuses to start. Also required (startup check, exit 78) with Cluster:Node:Role other than Combined and for every world child of a Cluster:Supervisor that runs more than one world: an unfenced world skips the durable id allocator priming and collides on in-memory id counters. Each world of such a cluster needs its own OwnerNode and Cluster:Worker:NodeId, on a PostgreSQL or MariaDB characters database.</summary>
    public bool Enabled { get; set; }

    /// <summary>The durable owner identity; defaults to the current machine name and must be stable while the world owns the database. Must be distinct for each world of a multi-world cluster (the supervisor check refuses a duplicate).</summary>
    public string OwnerNode { get; set; } = Environment.MachineName;

    /// <summary>The positive ownership epoch to compare and swap during an explicit takeover; pair with <see cref="TakeoverIncarnation"/>.</summary>
    public long? TakeoverEpoch { get; set; }

    /// <summary>The non-empty ownership incarnation to compare and swap during an explicit takeover; pair with <see cref="TakeoverEpoch"/>.</summary>
    public Guid? TakeoverIncarnation { get; set; }

    public const int DefaultClaimWatchIntervalSeconds = 10;

    /// <summary>With fencing disabled, how often the world reads the characters ownership row to notice that a fenced world has claimed the database, in seconds (1 to 300). Independent of autosave; an unfenced world can keep writing for up to this long after a claim.</summary>
    public int ClaimWatchIntervalSeconds { get; set; } = DefaultClaimWatchIntervalSeconds;

    /// <summary>Which durable ownership scope this world claims: Realm (the default, one realm-wide writer, exactly as before) or Worker (one scope per map worker shard, so several workers write the same characters database). Worker needs Cluster:Worker:Enabled, Cluster:Fencing:Enabled, Cluster:Node:Role=MapWorker, a non-empty explicit Cluster:Worker:OwnedMaps and a PostgreSQL or MariaDB characters database (SQLite only with AllowSqliteWorkerScope).</summary>
    public CharacterFencingScope Scope { get; set; } = CharacterFencingScope.Realm;

    /// <summary>With Scope=Worker, the shard id this worker claims and locks (1 to 57 characters of A-Z a-z 0-9 : . _ -); empty uses Cluster:Worker:NodeId. A standby sets the active worker's shard so a handoff hands over the same scope. Shard ids must be unique per world child, ignoring case.</summary>
    public string Shard { get; set; } = string.Empty;

    public const int DefaultHeartbeatSeconds = 10;

    /// <summary>With Scope=Worker, how often the worker records that it is alive on its ownership row, in seconds (1 to 60). A reap only ever fires on a heartbeat older than ReapAfterSeconds.</summary>
    public int HeartbeatSeconds { get; set; } = DefaultHeartbeatSeconds;

    public const int DefaultReapAfterSeconds = 150;

    /// <summary>With Scope=Worker, the age of a worker row's last heartbeat before another worker may reap it as dead, in seconds (30 to 3600, and more than three heartbeats). Keep it above the advisory lock's stale-session timeout (about 4 x 30 s).</summary>
    public int ReapAfterSeconds { get; set; } = DefaultReapAfterSeconds;

    /// <summary>With Scope=Worker, whether a worker may reap a dead shard (lock free and heartbeat older than ReapAfterSeconds) so its characters become claimable; false leaves that to an operator. Ignored in Realm scope.</summary>
    public bool ReapDeadOwners { get; set; } = true;

    /// <summary>With Scope=Worker, allows a SQLite characters database. Off by default: a split cluster needs PostgreSQL or MariaDB; only in-process tests set it.</summary>
    public bool AllowSqliteWorkerScope { get; set; }

    /// <summary>The upper bound of owned maps one worker may claim: the durable list is at most 2048 characters.</summary>
    public const int MaxOwnedMapCount = 1024;

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
        if (!Enum.IsDefined(Scope))
            throw new InvalidOperationException($"Cluster:Fencing:Scope {(int)Scope} is not Realm or Worker");
        if (Scope == CharacterFencingScope.Worker)
        {
            // The heartbeat and reap settings only mean something in Worker scope, so a Realm-scope world with an out-of-range value still starts.
            if (HeartbeatSeconds is < 1 or > 60)
                throw new InvalidOperationException("Cluster:Fencing:HeartbeatSeconds must be between 1 and 60");
            if (ReapAfterSeconds is < 30 or > 3600 || ReapAfterSeconds <= HeartbeatSeconds * 3)
                throw new InvalidOperationException("Cluster:Fencing:ReapAfterSeconds must be between 30 and 3600 and more than three heartbeats");
            if (!Enabled)
                throw new InvalidOperationException("Cluster:Fencing:Scope=Worker requires Cluster:Fencing:Enabled = true");
            if (!string.IsNullOrWhiteSpace(Shard) && !CharacterOwnershipScopes.IsValidShard(Shard.Trim()))
                throw new InvalidOperationException($"Cluster:Fencing:Shard must be 1 to {CharacterOwnershipScopes.MaxShardLength} characters of [A-Za-z0-9:._-]");
        }

        if (HasTakeover && (TakeoverEpoch is not > 0 || TakeoverIncarnation is null || TakeoverIncarnation == Guid.Empty))
            throw new InvalidOperationException("Cluster:Fencing takeover requires a positive epoch and non-empty incarnation together");
    }

    /// <summary>
    /// The shard this worker claims: <see cref="Shard"/> when set, else <paramref name="nodeId"/> (Cluster:Worker:NodeId). A node id that already starts
    /// with the scope prefix (<c>worker:Kalimdor</c>) names the shard <c>Kalimdor</c>, so the scope reads <c>worker:Kalimdor</c> and not <c>worker:worker:Kalimdor</c>.
    /// </summary>
    public string ResolveShard(string nodeId)
    {
        string shard = string.IsNullOrWhiteSpace(Shard) ? nodeId?.Trim() ?? string.Empty : Shard.Trim();
        return shard.Length > CharacterOwnershipScopes.WorkerPrefix.Length && shard.StartsWith(CharacterOwnershipScopes.WorkerPrefix, StringComparison.Ordinal)
            ? shard[CharacterOwnershipScopes.WorkerPrefix.Length..]
            : shard;
    }

    /// <summary>The durable scope this world claims: <c>world</c> in Realm scope, <c>worker:&lt;shard&gt;</c> in Worker scope.</summary>
    /// <exception cref="WorkerScopeConfigurationException">Worker scope with a shard id that is not valid.</exception>
    public string ResolveScope(string nodeId)
    {
        if (Scope != CharacterFencingScope.Worker)
            return CharacterOwnershipScopes.World;
        string shard = ResolveShard(nodeId);
        if (!CharacterOwnershipScopes.IsValidShard(shard))
            throw new WorkerScopeConfigurationException($"the worker shard '{shard}' is not valid: 1 to {CharacterOwnershipScopes.MaxShardLength} characters of [A-Za-z0-9:._-] (set Cluster:Fencing:Shard)");
        return CharacterOwnershipScopes.ForShard(shard);
    }

    /// <summary>
    /// The checks Worker scope adds on top of <see cref="Validate"/>: worker mode on, role MapWorker, an explicit map list (as a durable map set within
    /// <see cref="MaxOwnedMapCount"/>), a valid shard. A no-op in Realm scope.
    /// </summary>
    /// <exception cref="WorkerScopeConfigurationException">The configuration cannot run in Worker scope.</exception>
    public void ValidateWorkerScope(GatewayWorkerOptions? worker, ClusterNodeRole role)
    {
        if (Scope != CharacterFencingScope.Worker)
            return;
        if (worker is not { Enabled: true })
            throw new WorkerScopeConfigurationException("Cluster:Fencing:Scope=Worker requires Cluster:Worker:Enabled = true");
        if (role != ClusterNodeRole.MapWorker)
            throw new WorkerScopeConfigurationException("Cluster:Fencing:Scope=Worker requires Cluster:Node:Role=MapWorker");
        if (worker.Standby && string.IsNullOrWhiteSpace(Shard))
            throw new WorkerScopeConfigurationException("a standby worker takes over the shard of the worker it replaces: set Cluster:Fencing:Shard explicitly (its NodeId differs from the active worker's)");
        ResolveScope(worker.NodeId);
        if (!TryGetOwnedMaps(worker, out _, out string? error))
            throw new WorkerScopeConfigurationException($"Cluster:Fencing:Scope=Worker: {error}");
    }

    /// <summary>
    /// The durable owned map set of a worker: <see cref="GatewayWorkerOptions.OwnedMaps"/> expanded to ascending, distinct map ids. False with
    /// <paramref name="error"/> when the list is empty (a Worker-scope worker must name its maps), malformed, or larger than <see cref="MaxOwnedMapCount"/>.
    /// </summary>
    public static bool TryGetOwnedMaps(GatewayWorkerOptions worker, out IReadOnlyCollection<uint> maps, out string? error)
    {
        ArgumentNullException.ThrowIfNull(worker);
        maps = [];
        if (!MapOwnership.TryParseRanges(worker.OwnedMaps, out IReadOnlyList<MapOwnership.Range> ranges, out string? parseError))
        {
            error = $"Cluster:Worker:OwnedMaps is not a map list: {parseError}";
            return false;
        }

        if (ranges.Count == 0)
        {
            error = "Cluster:Worker:OwnedMaps must list the worker's maps explicitly; a worker that owns every map cannot share the database";
            return false;
        }

        var ids = new SortedSet<uint>();
        foreach (MapOwnership.Range range in ranges)
        {
            if ((ulong)range.Last - range.First >= MaxOwnedMapCount)
            {
                error = $"Cluster:Worker:OwnedMaps names more than {MaxOwnedMapCount} maps";
                return false;
            }

            for (ulong id = range.First; id <= range.Last; id++)
                ids.Add((uint)id);
            if (ids.Count > MaxOwnedMapCount)
            {
                error = $"Cluster:Worker:OwnedMaps names more than {MaxOwnedMapCount} maps";
                return false;
            }
        }

        maps = [.. ids];
        error = null;
        return true;
    }
}
