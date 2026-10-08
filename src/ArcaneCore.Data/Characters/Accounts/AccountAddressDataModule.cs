using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Accounts;

/// <summary>The last address an account entered the world from (vmangos <c>account.last_ip</c>), keyed by the account.</summary>
public sealed class AccountAddressRow
{
    public int AccountId { get; set; }

    /// <summary>The normalised address (IPv4-mapped IPv6 as IPv4), at most 45 characters like <c>ip_banned.ip</c>.</summary>
    public string Ip { get; set; } = string.Empty;

    public long LastSeenUnix { get; set; }
}

/// <summary>
/// The <c>account_last_ip</c> table of the characters database (schema <see cref="Version"/>), written by the world daemon
/// when a session authenticates and read by <c>.ban allip</c>. Account-scoped like <c>account_mute</c>, so a character
/// deletion leaves it alone. Additive and re-runnable.
/// </summary>
public sealed class AccountAddressDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>The characters schema version of this module (wave-2 lane ops-social, reserved as 38; renumbered to 37 at the integration).</summary>
    public const int Version = 37; // reserved as 38 in the wave-2 plan; renumbered down at the 2026-10-07 integration (no gaps)

    public const string Table = "account_last_ip";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
        => modelBuilder.Entity<AccountAddressRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.AccountId);
            entity.Property(r => r.AccountId).ValueGeneratedNever();
            entity.Property(r => r.Ip).HasMaxLength(45).IsRequired();
            entity.HasIndex(r => r.Ip);
        });

    public void AddServices(IServiceCollection services) => services.AddScoped<IAccountAddressStore, EfAccountAddressStore>();

    /// <summary>The last address belongs to the account, not to a character: nothing to delete (as <c>account_mute</c>).</summary>
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>EF Core implementation of <see cref="IAccountAddressStore"/>.</summary>
public sealed class EfAccountAddressStore(CharacterDbContext db) : IAccountAddressStore
{
    public async Task RecordAsync(int accountId, string ip, long nowUnix, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ip);
        DbSet<AccountAddressRow> set = db.Set<AccountAddressRow>();
        AccountAddressRow? row = await set.FirstOrDefaultAsync(r => r.AccountId == accountId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new AccountAddressRow { AccountId = accountId };
            set.Add(row);
        }

        row.Ip = ip;
        row.LastSeenUnix = nowUnix;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<AccountAddressRecord>> FindByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        string p = prefix ?? string.Empty;
        List<AccountAddressRow> rows = await db.Set<AccountAddressRow>().AsNoTracking()
            .Where(r => r.Ip.StartsWith(p)).OrderBy(r => r.AccountId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => new AccountAddressRecord(r.AccountId, r.Ip, r.LastSeenUnix))];
    }
}
