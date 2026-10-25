using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Auth;

/// <summary>Auth v6 last-login time and client OS, platform, locale and build columns.</summary>
public sealed class AccountLoginInfoDataModule : IDataModule
{
    public const int Version = 6;
    public DatabaseComponent Component => DatabaseComponent.Auth;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange("account", nameof(Account.LastLoginUtc)),
        new AddColumnChange("account", nameof(Account.LastOs)),
        new AddColumnChange("account", nameof(Account.LastPlatform)),
        new AddColumnChange("account", nameof(Account.LastLocale)),
        new AddColumnChange("account", nameof(Account.LastBuild)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(entity =>
        {
            entity.Property(a => a.LastOs).HasMaxLength(4).HasDefaultValue(string.Empty).IsRequired();
            entity.Property(a => a.LastPlatform).HasMaxLength(4).HasDefaultValue(string.Empty).IsRequired();
            entity.Property(a => a.LastLocale).HasMaxLength(4).HasDefaultValue(string.Empty).IsRequired();
            entity.Property(a => a.LastBuild).HasConversion<int>().HasDefaultValue((ushort)0);
        });
    }

    public void AddServices(IServiceCollection services)
        => services.AddScoped<IAccountLoginInfoStore, EfAccountStore>();
}
