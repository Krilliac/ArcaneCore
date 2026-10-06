using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Auth.Playerbots;

/// <summary>Pending proof that a newly created P0 account is reserved for one managed playerbot.</summary>
public sealed class ManagedPlayerbotProvisionRow
{
    public Guid BotId { get; set; }
    public int AccountId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public long CreatedUnix { get; set; }
}

/// <summary>Auth schema step for atomic managed-playerbot account provisioning.</summary>
public sealed class ManagedPlayerbotProvisionDataModule : IDataModule
{
    public const int Version = 4;
    public const string Table = "managed_playerbot_provision";

    public DatabaseComponent Component => DatabaseComponent.Auth;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ManagedPlayerbotProvisionRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(row => row.BotId);
            entity.Property(row => row.BotId).ValueGeneratedNever();
            entity.Property(row => row.AccountName).HasMaxLength(16).IsRequired();
            entity.HasIndex(row => row.AccountId).IsUnique();
        });
    }

    public void AddServices(IServiceCollection services)
        => services.AddScoped<IManagedPlayerbotProvisionStore, EfManagedPlayerbotProvisionStore>();
}

/// <summary>Atomic account/journal provisioning with strict new-P0 ownership checks.</summary>
public sealed class EfManagedPlayerbotProvisionStore(AuthDbContext db) : IManagedPlayerbotProvisionStore
{
    private const int MaxPendingRows = 4096;
    public async Task<Account> CreateAsync(Guid botId, Account account, CancellationToken cancellationToken = default)
    {
        ValidateNew(botId, account);
        account.Username = account.Username.ToUpperInvariant();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await db.Accounts.AnyAsync(row => row.Username == account.Username, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("managed playerbot provisioning refuses an existing account");
        if (await db.Set<ManagedPlayerbotProvisionRow>().AnyAsync(row => row.BotId == botId,
                cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("managed playerbot provisioning journal already exists");

        var proof = new ManagedPlayerbotProvisionRow
        {
            BotId = botId, AccountName = account.Username, CreatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        try
        {
            db.Accounts.Add(account);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            proof.AccountId = account.Id;
            db.Set<ManagedPlayerbotProvisionRow>().Add(proof);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return account;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            db.Entry(proof).State = EntityState.Detached;
            db.Entry(account).State = EntityState.Detached;
            account.Id = 0;
            throw;
        }
    }

    public async Task<IReadOnlyList<ManagedPlayerbotProvision>> LoadPendingAsync(CancellationToken cancellationToken = default)
    {
        List<ManagedPlayerbotProvisionRow> rows = await db.Set<ManagedPlayerbotProvisionRow>().AsNoTracking()
            .OrderBy(row => row.CreatedUnix).ThenBy(row => row.BotId).Take(MaxPendingRows + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count > MaxPendingRows)
            throw new InvalidOperationException("managed playerbot provisioning journal exceeds its reconciliation bound");
        return rows.Select(row => new ManagedPlayerbotProvision(row.BotId, row.AccountId, row.AccountName, row.CreatedUnix)).ToArray();
    }

    public async Task<bool> CompleteAsync(Guid botId, int accountId, CancellationToken cancellationToken = default)
    {
        if (botId == Guid.Empty || accountId <= 0) return false;
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        ManagedPlayerbotProvisionRow? journal = await db.Set<ManagedPlayerbotProvisionRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.BotId == botId && row.AccountId == accountId, cancellationToken).ConfigureAwait(false);
        Account? account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(row => row.Id == accountId, cancellationToken).ConfigureAwait(false);
        if (journal is null || account is null || account.Username != journal.AccountName || !IsNewP0(account))
            return false;
        int changed = await db.Set<ManagedPlayerbotProvisionRow>().Where(row => row.BotId == botId && row.AccountId == accountId
            && db.Accounts.Any(owner => owner.Id == row.AccountId && owner.Username == row.AccountName
                && owner.Status == AccountStatus.Active && owner.Security == AccountSecurity.Player && owner.SessionKey == null))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (changed != 1) return false;
        DetachProof(botId);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RollbackEmptyOwnerAsync(Guid botId, int accountId, CancellationToken cancellationToken = default)
    {
        if (botId == Guid.Empty || accountId <= 0) return false;
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        ManagedPlayerbotProvisionRow? journal = await db.Set<ManagedPlayerbotProvisionRow>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.BotId == botId && row.AccountId == accountId, cancellationToken).ConfigureAwait(false);
        Account? account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(row => row.Id == accountId, cancellationToken).ConfigureAwait(false);
        if (journal is null || account is null || account.Username != journal.AccountName || !IsNewP0(account))
            return false;
        int removed = await db.Accounts.Where(row => row.Id == accountId && row.Username == journal.AccountName
            && row.Security == AccountSecurity.Player && row.Status == AccountStatus.Active && row.SessionKey == null)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        if (removed != 1) return false;
        if (await db.Set<ManagedPlayerbotProvisionRow>().Where(row => row.BotId == botId && row.AccountId == accountId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("provision proof changed during rollback");
        DetachProof(botId);
        foreach (var entry in db.ChangeTracker.Entries<Account>().Where(entry => entry.Entity.Id == accountId).ToArray())
            entry.State = EntityState.Detached;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void DetachProof(Guid botId)
    {
        foreach (var entry in db.ChangeTracker.Entries<ManagedPlayerbotProvisionRow>().Where(entry => entry.Entity.BotId == botId).ToArray())
            entry.State = EntityState.Detached;
    }

    private static bool IsNewP0(Account account)
        => account.Id > 0 && account.Status == AccountStatus.Active
            && account.Security == AccountSecurity.Player && account.SessionKey is null;

    private static void ValidateNew(Guid botId, Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (botId == Guid.Empty || account.Id != 0 || string.IsNullOrWhiteSpace(account.Username)
            || account.Username.Length > 16 || account.Salt.Length != 32 || account.Verifier.Length != 32
            || account.SessionKey is not null || account.Status != AccountStatus.Active
            || account.Security != AccountSecurity.Player)
            throw new ArgumentException("managed playerbot provisioning requires a new active P0 account with salt/verifier only", nameof(account));
    }
}
