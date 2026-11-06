using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Login;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Auth;

public sealed class AccountCredentialDataModule : IDataModule
{
    public const int Version = 9;
    public DatabaseComponent Component => DatabaseComponent.Auth;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange("account_credential")];
    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountCredential>(e =>
        {
            e.ToTable("account_credential"); e.HasKey(x => x.AccountId);
            e.Property(x => x.AccountId).ValueGeneratedNever();
            e.Property(x => x.Email).HasMaxLength(254).IsRequired();
            e.Property(x => x.EmailNormalized).HasMaxLength(254).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(255);
            e.Property(x => x.SecurityStamp).HasMaxLength(16).IsRequired();
            e.Property(x => x.TotpSecretEncrypted).HasMaxLength(96);
            e.Property(x => x.TotpKeyId).IsRequired(); e.Property(x => x.TotpLastStep).IsRequired();
            e.Property(x => x.FailedLoginCount).IsRequired();
            e.HasIndex(x => x.EmailNormalized).IsUnique().HasDatabaseName("IX_account_credential_EmailNormalized");
        });
    }
    public void AddServices(IServiceCollection services) { }
}
