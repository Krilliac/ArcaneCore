using System.Data;
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
    public async Task<bool> IsOwnedAsync(string scope, CancellationToken cancellationToken = default)
    {
        RequireWorld(scope);
        return await db.Set<CharacterOwnershipRow>().AsNoTracking()
            .AnyAsync(r => r.Scope == scope && r.Active, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CharacterOwnershipToken?> GetCurrentAsync(string scope, CancellationToken cancellationToken = default)
    {
        RequireWorld(scope);
        using IDisposable exempt = CharacterWriteFence.Exempt(); // the ownership store is the fence itself; this only reads
        CharacterOwnershipRow? row = await db.Set<CharacterOwnershipRow>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.Scope == scope, cancellationToken).ConfigureAwait(false);
        return row is { Active: true } && Guid.TryParse(row.OwnerIncarnation, out _) ? Token(row) : null;
    }

    public async Task<CharacterOwnershipToken?> TryAcquireAsync(string scope, string ownerNode, Guid incarnation, CancellationToken cancellationToken = default)
    {
        RequireWorld(scope);
        Validate(scope, ownerNode, incarnation);
        using IDisposable exempt = CharacterWriteFence.Exempt(); // the ownership store is the fence itself
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

        CharacterOwnershipRow row = await db.Set<CharacterOwnershipRow>().AsNoTracking().SingleAsync(r => r.Scope == scope, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Token(row);
    }

    public async Task<CharacterOwnershipToken?> TryTakeoverAsync(CharacterOwnershipToken observed, string ownerNode, Guid newIncarnation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observed);
        RequireWorld(observed.Scope);
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

        CharacterOwnershipRow row = await db.Set<CharacterOwnershipRow>().AsNoTracking().SingleAsync(r => r.Scope == observed.Scope, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Token(row);
    }

    public async Task ReleaseAsync(CharacterOwnershipToken token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        RequireWorld(token.Scope);
        ValidateToken(token);
        using IDisposable exempt = CharacterWriteFence.Exempt();
        await db.Set<CharacterOwnershipRow>()
            .Where(r => r.Scope == token.Scope && r.Active && r.Epoch == token.Epoch && r.WriteSequence < long.MaxValue
                && r.OwnerIncarnation == token.Incarnation.ToString("D"))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.OwnerNode, (string?)null)
                .SetProperty(r => r.OwnerIncarnation, (string?)null)
                .SetProperty(r => r.Active, false)
                .SetProperty(r => r.WriteSequence, r => r.WriteSequence + 1), cancellationToken)
            .ConfigureAwait(false);
    }

    private static CharacterOwnershipToken Token(CharacterOwnershipRow row)
        => new(row.Scope, row.Epoch, Guid.Parse(row.OwnerIncarnation!));

    private static void Validate(string scope, string ownerNode, Guid incarnation)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(ownerNode) || ownerNode.Length > 128 || incarnation == Guid.Empty)
            throw new ArgumentException("ownership scope, node and incarnation are required");
    }

    private static void RequireWorld(string scope)
    {
        if (!string.Equals(scope, "world", StringComparison.Ordinal))
            throw new ArgumentException("only the combined World ownership scope is supported", nameof(scope));
    }

    private static void ValidateToken(CharacterOwnershipToken token)
    {
        if (token.Epoch <= 0 || token.Incarnation == Guid.Empty)
            throw new ArgumentException("an ownership token must contain a positive epoch and non-empty incarnation", nameof(token));
    }
}
