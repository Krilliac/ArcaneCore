using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Login;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Auth;

public sealed class SessionTokenDataModule : IDataModule
{
    public const int Version = 8;
    public DatabaseComponent Component => DatabaseComponent.Auth;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange("session_token")];
    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SessionTokenRecord>(entity =>
        {
            entity.ToTable("session_token"); entity.HasKey(x => x.TokenId);
            entity.Property(x => x.TokenId).HasMaxLength(16).IsRequired(); entity.Property(x => x.Kind).HasConversion<byte>().IsRequired();
            entity.Property(x => x.TokenSha256).HasMaxLength(32).IsRequired(); entity.Property(x => x.OriginTokenId).HasMaxLength(16).IsRequired();
            entity.Property(x => x.IntegritySeed).HasMaxLength(32).IsRequired(); entity.Property(x => x.ClientBuild).IsRequired();
            entity.Property(x => x.Ip).HasMaxLength(45).IsRequired(); entity.Property(x => x.CreatedUtc).IsRequired(); entity.Property(x => x.ExpiresUtc).IsRequired();
            entity.HasIndex(x => x.TokenSha256).IsUnique().HasDatabaseName("IX_session_token_TokenSha256");
            entity.HasIndex(x => new { x.AccountId, x.Kind }).HasDatabaseName("IX_session_token_AccountId_Kind");
            entity.HasIndex(x => x.ExpiresUtc).HasDatabaseName("IX_session_token_ExpiresUtc");
        });
    }
    public void AddServices(IServiceCollection services)
    { services.AddScoped<ISessionTokenStore, ArcaneCore.Data.Stores.EfSessionTokenStore>(); services.AddScoped<SessionTokenService>(); }
}
