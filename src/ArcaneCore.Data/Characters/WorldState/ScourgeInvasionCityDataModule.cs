using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.WorldState;

/// <summary>A capital's saved next city attack (mangos-classic SI_TIMER_UNDERCITY / SI_TIMER_STORMWIND).</summary>
public sealed class ScourgeInvasionCityRow
{
    public uint ZoneId { get; set; }
    public long NextAttackUnix { get; set; }
}

/// <summary>Characters schema 47: the two Scourge city-attack timers, so a restart does not resummon a fresh Pallid Horror early.</summary>
public sealed class ScourgeInvasionCityDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 47;
    public const string Table = "world_scourge_invasion_city";
    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
        => modelBuilder.Entity<ScourgeInvasionCityRow>(e =>
        {
            e.ToTable(Table);
            e.HasKey(r => r.ZoneId);
            e.Property(r => r.ZoneId).HasColumnName("zone_id").ValueGeneratedNever();
            e.Property(r => r.NextAttackUnix).HasColumnName("next_attack_unix");
        });

    public void AddServices(IServiceCollection services) { }
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken) => Task.CompletedTask;
}
