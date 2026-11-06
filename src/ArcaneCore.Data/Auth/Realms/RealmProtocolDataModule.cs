using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Realms;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Auth;

public sealed class RealmProtocolDataModule : IDataModule
{
    public const int Version = 10;
    public DatabaseComponent Component => DatabaseComponent.Auth;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } =
    [
        new AddColumnChange("realmlist", nameof(RealmEntry.ProtocolVersion)),
        new AddColumnChange("realmlist", nameof(RealmEntry.QueueLength)),
        new AddColumnChange("realmlist", nameof(RealmEntry.Host)),
        new AddColumnChange("realmlist", nameof(RealmEntry.Port)),
    ];
    public void ConfigureModel(ModelBuilder modelBuilder) => modelBuilder.Entity<RealmEntry>(entity =>
    {
        entity.Property(r => r.ProtocolVersion).HasDefaultValue(0u);
        entity.Property(r => r.QueueLength).HasDefaultValue(0u);
        entity.Property(r => r.Host).HasMaxLength(253).HasDefaultValue(string.Empty).IsRequired();
        entity.Property(r => r.Port).HasConversion<int>().HasDefaultValue((ushort)0);
    });
    public void AddServices(IServiceCollection services) { }
}
