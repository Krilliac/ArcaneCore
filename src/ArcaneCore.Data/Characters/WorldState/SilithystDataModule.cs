using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.WorldState;

/// <summary>The single row of the vmangos Silithus saved variables (WS_OPVP_SI_GATHERED_A/H, WS_OPVP_SI_SILITHYST_MAX).</summary>
public sealed class SilithystRow
{
    public int Id { get; set; }
    public long GatheredAlliance { get; set; }
    public long GatheredHorde { get; set; }
    public long MaxResources { get; set; }
}

/// <summary>
/// Characters schema 53 (outdoor-pvp lane; 50 is #74 and 51-52 are #73, which merge first): the Silithyst totals, so a restart keeps a GM-set maximum as vmangos' saved variables do.
/// </summary>
public sealed class SilithystDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 53;
    public const string Table = "world_silithyst";
    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
        => modelBuilder.Entity<SilithystRow>(e =>
        {
            e.ToTable(Table);
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.GatheredAlliance).HasColumnName("gathered_alliance");
            e.Property(r => r.GatheredHorde).HasColumnName("gathered_horde");
            e.Property(r => r.MaxResources).HasColumnName("max_resources");
        });

    public void AddServices(IServiceCollection services) => services.AddScoped<ISilithystStore, EfSilithystStore>();
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class EfSilithystStore(CharacterDbContext db) : ISilithystStore
{
    public async Task<SilithystState?> LoadAsync(CancellationToken cancellationToken = default)
        => await db.Set<SilithystRow>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == 1, cancellationToken) is { } row
            ? new SilithystState((uint)row.GatheredAlliance, (uint)row.GatheredHorde, (uint)row.MaxResources)
            : null;

    public async Task SaveAsync(SilithystState state, CancellationToken cancellationToken = default)
    {
        SilithystRow? row = await db.Set<SilithystRow>().FirstOrDefaultAsync(r => r.Id == 1, cancellationToken);
        if (row is null) db.Add(row = new SilithystRow { Id = 1 });
        row.GatheredAlliance = state.GatheredAlliance;
        row.GatheredHorde = state.GatheredHorde;
        row.MaxResources = state.MaxResources;
        await db.SaveChangesAsync(cancellationToken);
    }
}
