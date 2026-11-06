using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Realms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Auth;

public enum LanternfallBuildStatus : byte { Active = 1, Grace = 2, Retired = 3 }

public sealed class RealmClientBuildRow
{
    public int RealmId { get; set; }
    public uint BuildId { get; set; }
    public LanternfallBuildStatus Status { get; set; }
    public DateTime ChangedUtc { get; set; }
}

public sealed class RealmClientBuildDataModule : IDataModule
{
    public const int Version = 11;
    public DatabaseComponent Component => DatabaseComponent.Auth;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange("realm_client_build")];
    public void ConfigureModel(ModelBuilder modelBuilder) => modelBuilder.Entity<RealmClientBuildRow>(entity =>
    {
        entity.ToTable("realm_client_build");
        entity.HasKey(x => new { x.RealmId, x.BuildId });
        entity.Property(x => x.Status).HasConversion<byte>().IsRequired();
        entity.Property(x => x.ChangedUtc).IsRequired();
    });
    public void AddServices(IServiceCollection services)
        => services.AddScoped<ILanternfallRealmStore, EfLanternfallRealmStore>();
}
