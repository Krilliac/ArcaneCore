using System.Data;
using System.Globalization;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Ownership;

public sealed class CharacterOwnershipRow
{
    public string Scope { get; set; } = string.Empty;
    public string? OwnerNode { get; set; }
    public string? OwnerIncarnation { get; set; }
    public long Epoch { get; set; }
    public long WriteSequence { get; set; }
    public bool Active { get; set; }

    /// <summary>Worker scopes only (characters schema 72): the comma-separated, ascending map ids the shard owns; null owns every map.</summary>
    public string? OwnedMaps { get; set; }

    /// <summary>Worker scopes only (characters schema 72): unix milliseconds of the owner's last heartbeat; null before the first claim.</summary>
    public long? HeartbeatAtMs { get; set; }
}

/// <summary>
/// The ownership scopes of <c>character_ownership</c>. <see cref="World"/> is the realm-wide row (the combined World, and the realm gate
/// that worker claims lock first); a worker shard owns <c>worker:&lt;shard&gt;</c>, created when it first claims.
/// Design: _wf/writer-lock-DESIGN.md section 1.
/// </summary>
public static class CharacterOwnershipScopes
{
    public const string World = "world";
    public const string WorkerPrefix = "worker:";

    /// <summary><c>character_ownership.scope</c> is varchar(64), so a shard id may be at most 57 characters.</summary>
    public const int MaxShardLength = 64 - 7;

    public static bool IsWorker(string? scope) => scope is not null && scope.StartsWith(WorkerPrefix, StringComparison.Ordinal);

    /// <summary>True for <see cref="World"/> or a well-formed worker scope.</summary>
    public static bool IsValid(string? scope)
        => string.Equals(scope, World, StringComparison.Ordinal) || (IsWorker(scope) && IsValidShard(scope!.AsSpan(WorkerPrefix.Length)));

    /// <summary>A shard id is 1 to 57 characters of <c>[A-Za-z0-9:._-]</c>.</summary>
    public static bool IsValidShard(ReadOnlySpan<char> shard)
    {
        if (shard.Length is 0 or > MaxShardLength)
            return false;
        foreach (char c in shard)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is ':' or '.' or '_' or '-'))
                return false;
        }

        return true;
    }

    public static string ForShard(string shard)
    {
        ArgumentNullException.ThrowIfNull(shard);
        if (!IsValidShard(shard))
            throw new ArgumentException($"a shard id must be 1 to {MaxShardLength} characters of [A-Za-z0-9:._-]", nameof(shard));
        return WorkerPrefix + shard;
    }
}

/// <summary>Durable ownership for the combined World process. The row is deliberately realm-local.</summary>
public sealed class CharacterOwnershipDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 60;
    public const string Table = "character_ownership";

    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange(Table),
        new EnsureRowChange(Table, "scope", "world", new Dictionary<string, object?>
        {
            ["owner_node"] = null,
            ["owner_incarnation"] = null,
            ["epoch"] = 0L,
            ["write_sequence"] = 0L,
            ["active"] = false,
        }),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CharacterOwnershipRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.Scope);
            entity.Property(r => r.Scope).HasColumnName("scope").HasMaxLength(64).ValueGeneratedNever();
            entity.Property(r => r.OwnerNode).HasColumnName("owner_node").HasMaxLength(128);
            entity.Property(r => r.OwnerIncarnation).HasColumnName("owner_incarnation").HasMaxLength(36);
            entity.Property(r => r.Epoch).HasColumnName("epoch");
            entity.Property(r => r.WriteSequence).HasColumnName("write_sequence");
            entity.Property(r => r.Active).HasColumnName("active");
            // A few focused SQLite fixtures use EnsureCreated directly instead of the schema
            // bootstrapper; keep their local-mode gate equivalent to the additive seed step.
            entity.HasData(new CharacterOwnershipRow { Scope = "world", Epoch = 0, WriteSequence = 0, Active = false });
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ICharacterOwnershipStore, EfCharacterOwnershipStore>();

    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class EfCharacterOwnershipStore(CharacterDbContext db) : ICharacterOwnershipStore
{
    /// <summary><c>character_ownership.owned_maps</c> is varchar(2048).</summary>
    internal const int MaxOwnedMapsLength = 2048;

    public async Task<bool> IsOwnedAsync(string scope, CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        return await db.Set<CharacterOwnershipRow>().AsNoTracking()
            .AnyAsync(r => r.Scope == scope && r.Active, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True when any ownership row is active: the combined World, or at least one worker shard. An unfenced world's claim watch uses this
    /// instead of <c>IsOwnedAsync("world")</c> once worker rows can exist; with only the 'world' row it answers the same.
    /// </summary>
    public async Task<bool> IsAnyOwnedAsync(CancellationToken cancellationToken = default)
        => await db.Set<CharacterOwnershipRow>().AsNoTracking().AnyAsync(r => r.Active, cancellationToken).ConfigureAwait(false);

    public async Task<CharacterOwnershipToken?> GetCurrentAsync(string scope, CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        using IDisposable exempt = CharacterWriteFence.Exempt(); // the ownership store is the fence itself; this only reads
        CharacterOwnershipRow? row = await db.Set<CharacterOwnershipRow>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.Scope == scope, cancellationToken).ConfigureAwait(false);
        return row is { Active: true } && Guid.TryParse(row.OwnerIncarnation, out _) ? Token(row) : null;
    }

    public Task<CharacterOwnershipToken?> TryAcquireAsync(string scope, string ownerNode, Guid incarnation, CancellationToken cancellationToken = default)
        => TryAcquireAsync(scope, ownerNode, incarnation, ownedMaps: null, cancellationToken);

    /// <summary>
    /// Claims <paramref name="scope"/>. <c>world</c> keeps today's semantics (one UPDATE of the inactive row) and additionally refuses while any
    /// worker row is active. A worker scope runs the gated claim in one transaction: lock the realm gate ('world' must be inactive), insert the shard row
    /// if it is missing, refuse when <paramref name="ownedMaps"/> overlaps another active worker (null owns every map, so it refuses beside any other active
    /// worker), then activate an inactive row and bump its epoch. An active row under the same node and incarnation is returned idempotently; any other
    /// active row is refused (null): taking it over is <see cref="TryReclaimAsync"/> (a crash) or <see cref="TryTakeoverAsync"/> (a handoff).
    /// Thread affinity: none; one store instance wraps one <see cref="DbContext"/> and is not thread-safe.
    /// </summary>
    public async Task<CharacterOwnershipToken?> TryAcquireAsync(string scope, string ownerNode, Guid incarnation, IReadOnlyCollection<uint>? ownedMaps, CancellationToken cancellationToken = default)
    {
        ValidateScope(scope);
        Validate(scope, ownerNode, incarnation);
        using IDisposable exempt = CharacterWriteFence.Exempt(); // the ownership store is the fence itself
        if (CharacterOwnershipScopes.IsWorker(scope))
            return await AcquireWorkerAsync(scope, ownerNode, incarnation, ownedMaps, cancellationToken).ConfigureAwait(false);

        if (ownedMaps is not null)
            throw new ArgumentException("the world ownership scope owns every map", nameof(ownedMaps));
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        int changed = await db.Set<CharacterOwnershipRow>()
            .Where(r => r.Scope == scope && !r.Active && r.Epoch < long.MaxValue && r.WriteSequence < long.MaxValue)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.OwnerNode, ownerNode)
                .SetProperty(r => r.OwnerIncarnation, incarnation.ToString("D"))
                .SetProperty(r => r.Active, true)
                .SetProperty(r => r.Epoch, r => r.Epoch + 1)
                .SetProperty(r => r.WriteSequence, r => r.WriteSequence + 1), cancellationToken)
            .ConfigureAwait(false);
        if (changed == 0)
        {
            CharacterOwnershipRow? current = await db.Set<CharacterOwnershipRow>().AsNoTracking()
                .SingleOrDefaultAsync(r => r.Scope == scope, cancellationToken).ConfigureAwait(false);
            if (current is null)
                throw new InvalidOperationException($"the characters ownership fence row '{scope}' is missing; schema initialization is incomplete");
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return current.Active && current.OwnerNode == ownerNode
                && Guid.TryParse(current.OwnerIncarnation, out Guid same) && same == incarnation ? Token(current) : null;
        }

        // The UPDATE above holds the 'world' row for this transaction, and every worker claim locks that row first, so no worker can
        // activate between this check and the commit. (A NOT EXISTS subquery inside the UPDATE itself would be MariaDB error 1093.)
        if (await db.Set<CharacterOwnershipRow>().AsNoTracking().AnyAsync(r => r.Scope != scope && r.Active, cancellationToken).ConfigureAwait(false))
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        CharacterOwnershipRow row = await db.Set<CharacterOwnershipRow>().AsNoTracking().SingleAsync(r => r.Scope == scope, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Token(row);
    }

    public async Task<CharacterOwnershipToken?> TryTakeoverAsync(CharacterOwnershipToken observed, string ownerNode, Guid newIncarnation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ValidateScope(observed.Scope);
        ValidateToken(observed);
        Validate(observed.Scope, ownerNode, newIncarnation);
        string incarnation = newIncarnation.ToString("D");
        using IDisposable exempt = CharacterWriteFence.Exempt();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        int changed = await db.Set<CharacterOwnershipRow>()
            .Where(r => r.Scope == observed.Scope && r.Active && r.Epoch == observed.Epoch && r.Epoch < long.MaxValue
                && r.WriteSequence < long.MaxValue && r.OwnerIncarnation == observed.Incarnation.ToString("D"))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.OwnerNode, ownerNode)
                .SetProperty(r => r.OwnerIncarnation, incarnation)
                .SetProperty(r => r.Epoch, r => r.Epoch + 1)
                .SetProperty(r => r.WriteSequence, r => r.WriteSequence + 1), cancellationToken)
            .ConfigureAwait(false);
        if (changed == 0)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (CharacterOwnershipScopes.IsWorker(observed.Scope))
        {
            // A handoff moves a live shard, so the new owner starts with a fresh heartbeat (the world row has none).
            long now = NowUnixMs();
            await db.Set<CharacterOwnershipRow>().Where(r => r.Scope == observed.Scope)
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.HeartbeatAtMs, (long?)now), cancellationToken).ConfigureAwait(false);
        }

        CharacterOwnershipRow row = await db.Set<CharacterOwnershipRow>().AsNoTracking().SingleAsync(r => r.Scope == observed.Scope, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Token(row);
    }

    /// <summary>
    /// Crash recovery of a worker shard (design section 5): CAS epoch N under the observed incarnation to N+1 under <paramref name="newIncarnation"/>,
    /// with the new owned maps and a fresh heartbeat. Gated like a claim (the 'world' row must be inactive; the maps must not overlap another active worker).
    /// The caller must hold the shard's advisory lock, which is the proof the old owner is dead; this store cannot check that. A zombie of the old incarnation
    /// is refused on its next transaction by the epoch check. Null when the row moved, is inactive, or the gate refuses. Realm mode never reclaims (design Q7).
    /// </summary>
    public async Task<CharacterOwnershipToken?> TryReclaimAsync(CharacterOwnershipToken observed, string ownerNode, Guid newIncarnation, IReadOnlyCollection<uint>? ownedMaps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observed);
        RequireWorkerScope(observed.Scope);
        ValidateToken(observed);
        Validate(observed.Scope, ownerNode, newIncarnation);
        string? storedMaps = ownedMaps is null ? null : FormatMaps(ownedMaps);
        string incarnation = newIncarnation.ToString("D");
        long now = NowUnixMs();
        using IDisposable exempt = CharacterWriteFence.Exempt();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await LockGateAsync(cancellationToken).ConfigureAwait(false)
            || await OverlapsOtherActiveWorkerAsync(observed.Scope, ownedMaps, cancellationToken).ConfigureAwait(false))
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        int changed = await db.Set<CharacterOwnershipRow>()
            .Where(r => r.Scope == observed.Scope && r.Active && r.Epoch == observed.Epoch && r.Epoch < long.MaxValue
                && r.WriteSequence < long.MaxValue && r.OwnerIncarnation == observed.Incarnation.ToString("D"))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.OwnerNode, ownerNode)
                .SetProperty(r => r.OwnerIncarnation, incarnation)
                .SetProperty(r => r.Epoch, r => r.Epoch + 1)
                .SetProperty(r => r.WriteSequence, r => r.WriteSequence + 1)
                .SetProperty(r => r.OwnedMaps, storedMaps)
                .SetProperty(r => r.HeartbeatAtMs, (long?)now), cancellationToken)
            .ConfigureAwait(false);
        if (changed == 0)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        CharacterOwnershipRow row = await db.Set<CharacterOwnershipRow>().AsNoTracking().SingleAsync(r => r.Scope == observed.Scope, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Token(row);
    }

    /// <summary>
    /// Deactivates a worker shard whose owner is gone (design section 5, step 4): CAS on the observed epoch and incarnation, and only when the last
    /// heartbeat is at least <paramref name="minHeartbeatAgeMs"/> older than <paramref name="nowUnixMs"/>. A row with no heartbeat is never reaped (fail closed).
    /// The epoch is bumped, so the reaped owner's token is dead. The caller must have probe-taken the shard's advisory lock and checked <c>ReapDeadOwners</c>.
    /// </summary>
    public async Task<bool> ReapAsync(CharacterOwnershipToken observed, long nowUnixMs, long minHeartbeatAgeMs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observed);
        RequireWorkerScope(observed.Scope);
        ValidateToken(observed);
        ArgumentOutOfRangeException.ThrowIfNegative(minHeartbeatAgeMs);
        long newestReapable = nowUnixMs - minHeartbeatAgeMs;
        using IDisposable exempt = CharacterWriteFence.Exempt();
        int changed = await db.Set<CharacterOwnershipRow>()
            .Where(r => r.Scope == observed.Scope && r.Active && r.Epoch == observed.Epoch && r.Epoch < long.MaxValue
                && r.WriteSequence < long.MaxValue && r.OwnerIncarnation == observed.Incarnation.ToString("D")
                && r.HeartbeatAtMs != null && r.HeartbeatAtMs <= newestReapable)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.OwnerNode, (string?)null)
                .SetProperty(r => r.OwnerIncarnation, (string?)null)
                .SetProperty(r => r.Active, false)
                .SetProperty(r => r.Epoch, r => r.Epoch + 1)
                .SetProperty(r => r.WriteSequence, r => r.WriteSequence + 1)
                .SetProperty(r => r.OwnedMaps, (string?)null), cancellationToken)
            .ConfigureAwait(false);
        return changed > 0;
    }

    /// <summary>
    /// Records that the shard's owner is alive. False means the token no longer owns the shard (reclaimed, reaped or released), which the caller treats as
    /// losing ownership. Does not touch <c>write_sequence</c> or the epoch.
    /// </summary>
    public async Task<bool> HeartbeatAsync(CharacterOwnershipToken token, long nowUnixMs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        RequireWorkerScope(token.Scope);
        ValidateToken(token);
        using IDisposable exempt = CharacterWriteFence.Exempt();
        int changed = await db.Set<CharacterOwnershipRow>()
            .Where(r => r.Scope == token.Scope && r.Active && r.Epoch == token.Epoch && r.OwnerIncarnation == token.Incarnation.ToString("D"))
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.HeartbeatAtMs, (long?)nowUnixMs), cancellationToken)
            .ConfigureAwait(false);
        return changed > 0;
    }

    public async Task ReleaseAsync(CharacterOwnershipToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        ValidateScope(token.Scope);
        ValidateToken(token);
        using IDisposable exempt = CharacterWriteFence.Exempt();
        await db.Set<CharacterOwnershipRow>()
            .Where(r => r.Scope == token.Scope && r.Active && r.Epoch == token.Epoch && r.WriteSequence < long.MaxValue
                && r.OwnerIncarnation == token.Incarnation.ToString("D"))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.OwnerNode, (string?)null)
                .SetProperty(r => r.OwnerIncarnation, (string?)null)
                .SetProperty(r => r.Active, false)
                .SetProperty(r => r.OwnedMaps, (string?)null)
                .SetProperty(r => r.WriteSequence, r => r.WriteSequence + 1), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CharacterOwnershipToken?> AcquireWorkerAsync(string scope, string ownerNode, Guid incarnation, IReadOnlyCollection<uint>? ownedMaps, CancellationToken cancellationToken)
    {
        string? storedMaps = ownedMaps is null ? null : FormatMaps(ownedMaps);
        string incarnationText = incarnation.ToString("D");
        long now = NowUnixMs();
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Gate: lock the realm row. A fenced Combined world holds it active, so this refuses; concurrent worker claims serialise here,
        // which is also why the insert below cannot lose a primary-key race to another claim.
        if (!await LockGateAsync(cancellationToken).ConfigureAwait(false))
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        CharacterOwnershipRow? current = await db.Set<CharacterOwnershipRow>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.Scope == scope, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            var created = new CharacterOwnershipRow { Scope = scope, Epoch = 0, WriteSequence = 0, Active = false };
            db.Set<CharacterOwnershipRow>().Add(created);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                db.Entry(created).State = EntityState.Detached;
            }
        }
        else if (current.Active)
        {
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return current.OwnerNode == ownerNode && Guid.TryParse(current.OwnerIncarnation, out Guid same) && same == incarnation ? Token(current) : null;
        }

        if (await OverlapsOtherActiveWorkerAsync(scope, ownedMaps, cancellationToken).ConfigureAwait(false))
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        int changed = await db.Set<CharacterOwnershipRow>()
            .Where(r => r.Scope == scope && !r.Active && r.Epoch < long.MaxValue && r.WriteSequence < long.MaxValue)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.OwnerNode, ownerNode)
                .SetProperty(r => r.OwnerIncarnation, incarnationText)
                .SetProperty(r => r.Active, true)
                .SetProperty(r => r.Epoch, r => r.Epoch + 1)
                .SetProperty(r => r.WriteSequence, r => r.WriteSequence + 1)
                .SetProperty(r => r.OwnedMaps, storedMaps)
                .SetProperty(r => r.HeartbeatAtMs, (long?)now), cancellationToken)
            .ConfigureAwait(false);
        if (changed == 0)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        CharacterOwnershipRow row = await db.Set<CharacterOwnershipRow>().AsNoTracking().SingleAsync(r => r.Scope == scope, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Token(row);
    }

    /// <summary>
    /// Locks the 'world' row for the open transaction by bumping its write_sequence while it is inactive (an UPDATE takes the row lock on every
    /// provider, and conflicts with the tokenless FOR SHARE check). False when the row is active (a fenced Combined world) and so refuses worker claims.
    /// </summary>
    private async Task<bool> LockGateAsync(CancellationToken cancellationToken)
    {
        int changed = await db.Set<CharacterOwnershipRow>()
            .Where(r => r.Scope == CharacterOwnershipScopes.World && !r.Active && r.WriteSequence < long.MaxValue)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.WriteSequence, r => r.WriteSequence + 1), cancellationToken)
            .ConfigureAwait(false);
        if (changed > 0)
            return true;
        bool present = await db.Set<CharacterOwnershipRow>().AsNoTracking().AnyAsync(r => r.Scope == CharacterOwnershipScopes.World, cancellationToken).ConfigureAwait(false);
        if (!present)
            throw new InvalidOperationException("the characters ownership fence row 'world' is missing; schema initialization is incomplete");
        return false;
    }

    private async Task<bool> OverlapsOtherActiveWorkerAsync(string scope, IReadOnlyCollection<uint>? ownedMaps, CancellationToken cancellationToken)
    {
        List<string?> others = await db.Set<CharacterOwnershipRow>().AsNoTracking()
            .Where(r => r.Active && r.Scope != scope && r.Scope != CharacterOwnershipScopes.World)
            .Select(r => r.OwnedMaps)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (others.Count == 0)
            return false;
        if (ownedMaps is null)
            return true; // owns every map: refuses beside any other active worker
        foreach (string? stored in others)
        {
            if (stored is null)
                return true; // that worker owns every map
            foreach (string part in stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // An entry that does not parse is treated as overlapping (fail closed), not skipped.
                if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out uint map) || ownedMaps.Contains(map))
                    return true;
            }
        }

        return false;
    }

    internal static string FormatMaps(IReadOnlyCollection<uint> maps)
    {
        if (maps.Count == 0)
            throw new ArgumentException("an owned map set must not be empty; pass null to own every map", nameof(maps));
        string text = string.Join(',', maps.Distinct().OrderBy(m => m).Select(m => m.ToString(CultureInfo.InvariantCulture)));
        if (text.Length > MaxOwnedMapsLength)
            throw new ArgumentException($"the owned map list exceeds {MaxOwnedMapsLength} characters", nameof(maps));
        return text;
    }

    private static long NowUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static CharacterOwnershipToken Token(CharacterOwnershipRow row)
        => new(row.Scope, row.Epoch, Guid.Parse(row.OwnerIncarnation!));

    private static void Validate(string scope, string ownerNode, Guid incarnation)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(ownerNode) || ownerNode.Length > 128 || incarnation == Guid.Empty)
            throw new ArgumentException("ownership scope, node and incarnation are required");
    }

    private static void ValidateScope(string scope)
    {
        if (!CharacterOwnershipScopes.IsValid(scope))
            throw new ArgumentException("the ownership scope must be 'world' or 'worker:<shard>' (shard: 1 to 57 characters of [A-Za-z0-9:._-])", nameof(scope));
    }

    private static void RequireWorkerScope(string scope)
    {
        if (!CharacterOwnershipScopes.IsWorker(scope) || !CharacterOwnershipScopes.IsValid(scope))
            throw new ArgumentException("this operation applies to a worker ownership scope ('worker:<shard>') only", nameof(scope));
    }

    private static void ValidateToken(CharacterOwnershipToken token)
    {
        if (token.Epoch <= 0 || token.Incarnation == Guid.Empty)
            throw new ArgumentException("an ownership token must contain a positive epoch and non-empty incarnation", nameof(token));
    }
}
