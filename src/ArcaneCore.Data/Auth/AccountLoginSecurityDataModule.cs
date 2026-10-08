using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Auth;

/// <summary>Auth v5 account PIN/TOTP and IP lock columns.</summary>
public sealed class AccountLoginSecurityDataModule : IDataModule
{
    public const int Version = 5;
    public DatabaseComponent Component => DatabaseComponent.Auth;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange("account", nameof(Account.LockFlags)),
        new AddColumnChange("account", nameof(Account.SecurityInfo)),
        new AddColumnChange("account", nameof(Account.LastIp)),
    ];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(entity =>
        {
            entity.Property(a => a.LockFlags).HasConversion<byte>();
            entity.Property(a => a.SecurityInfo).HasMaxLength(128).HasDefaultValue(string.Empty).IsRequired();
            entity.Property(a => a.LastIp).HasMaxLength(45).HasDefaultValue(string.Empty).IsRequired();
        });
    }

    public void AddServices(IServiceCollection services)
        => services.AddScoped<IAccountLoginSecurityStore, EfAccountStore>();
}
