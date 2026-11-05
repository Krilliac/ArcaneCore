using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Kernel.Cluster.Social;
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

public sealed class EfSilithystStore(CharacterDbContext db, IRealmLeaseFenceSource? leaseFences = null) : ISilithystStore
{
    /// <summary>Kalimdor, the map of the Silithus zone script (SilithusCatalog.MapId in the game layer, which the data layer cannot reference).</summary>
    public const uint SilithusMapId = 1;

    public async Task<SilithystState?> LoadAsync(CancellationToken cancellationToken = default)
        => await db.Set<SilithystRow>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == 1, cancellationToken) is { } row
            ? new SilithystState((uint)row.GatheredAlliance, (uint)row.GatheredHorde, (uint)row.MaxResources)
            : null;

    public async Task SaveAsync(SilithystState state, CancellationToken cancellationToken = default)
    {
        // Durable world state is the worldstate lease holder's to write (writer-lock design section 2b; decision Q10), except the Silithyst totals: only the owner of Kalimdor runs the Silithus zone script, so a
        // worker that explicitly owns that map is the one writer of this row even when another node holds the lease (a split deployment loses every turn-in otherwise). A worker that owns every map is not
        // treated as the owner here (an empty list is the Combined/default shape, and the lease still decides).
        if (leaseFences is null || leaseFences.OwnedMaps.OwnsEverything || !leaseFences.OwnedMaps.Owns(SilithusMapId, 0))
            LeaseFencedWrite.RequireHeld(leaseFences, RealmAuthorityScopes.WorldState);
        SilithystRow? row = await db.Set<SilithystRow>().FirstOrDefaultAsync(r => r.Id == 1, cancellationToken);
        if (row is null) db.Add(row = new SilithystRow { Id = 1 });
        row.GatheredAlliance = state.GatheredAlliance;
        row.GatheredHorde = state.GatheredHorde;
        row.MaxResources = state.MaxResources;
        await db.SaveChangesAsync(cancellationToken);
    }
}
