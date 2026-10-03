using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Auth;

/// <summary>
/// A ban of an account (vmangos <c>account_banned</c>, sql/logon.sql:75-86). Times are unix seconds;
/// permanent is bandate == unbandate. <see cref="BanId"/> replaces retail's (id, bandate) primary key so
/// two bans within one second do not collide. The never-written retail <c>gmlevel</c> column is omitted.
/// </summary>
public sealed class AccountBanRow
{
    public int BanId { get; set; }
    public int AccountId { get; set; }
    public long BanDate { get; set; }
    public long UnbanDate { get; set; }
    public string BannedBy { get; set; } = string.Empty;
    public string BanReason { get; set; } = string.Empty;
    public bool Active { get; set; }
    public int Realm { get; set; }

    public AccountBanRecord ToRecord() => new(BanId, AccountId, BanDate, UnbanDate, BannedBy, BanReason, Active, Realm);
}

/// <summary>An IP ban (vmangos <c>ip_banned</c>, sql/logon.sql:139-146), with 64-bit dates (retail int(11) overflows in 2038).</summary>
public sealed class IpBanRow
{
    public string Ip { get; set; } = string.Empty;
    public long BanDate { get; set; }
    public long UnbanDate { get; set; }
    public string BannedBy { get; set; } = string.Empty;
    public string BanReason { get; set; } = string.Empty;

    public IpBanRecord ToRecord() => new(Ip, BanDate, UnbanDate, BannedBy, BanReason);
}

/// <summary>
/// Ban tables of the auth database (account_banned, ip_banned) and their store. The version is one
/// named constant: the integrator renumbers it, nothing else hard-codes it.
/// </summary>
public sealed class BanDataModule : IDataModule
{
    /// <summary>Auth schema version introducing the ban tables.</summary>
    public const int Version = 3;

    public DatabaseComponent Component => DatabaseComponent.Auth;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new CreateTableChange("account_banned"),
        new CreateTableChange("ip_banned"),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountBanRow>(entity =>
        {
            entity.ToTable("account_banned");
            entity.HasKey(r => r.BanId);
            entity.Property(r => r.BanId).ValueGeneratedOnAdd();
            entity.Property(r => r.BannedBy).HasMaxLength(50).IsRequired();
            entity.Property(r => r.BanReason).HasMaxLength(255).IsRequired();
            entity.HasIndex(r => new { r.AccountId, r.Active });
            entity.HasIndex(r => r.Active);
        });

        modelBuilder.Entity<IpBanRow>(entity =>
        {
            entity.ToTable("ip_banned");
            entity.HasKey(r => r.Ip);
            entity.Property(r => r.Ip).HasMaxLength(45).IsRequired();
            entity.Property(r => r.BannedBy).HasMaxLength(50).IsRequired();
            entity.Property(r => r.BanReason).HasMaxLength(255).IsRequired();
        });
    }

    public void AddServices(IServiceCollection services)
    {
        services.AddSingleton<AccountStatusEvents>();
        services.AddScoped<IBanStore>(sp => new EfBanStore(
            sp.GetRequiredService<AuthDbContext>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<AccountStatusEvents>()));
        services.AddScoped<IAccountAdmin>(sp => new EfAccountStore(
            sp.GetRequiredService<AuthDbContext>(), sp.GetService<AccountStatusEvents>()));
    }
}
