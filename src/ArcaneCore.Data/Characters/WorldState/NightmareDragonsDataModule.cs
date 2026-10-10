using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.WorldState;

/// <summary>The single row of the vmangos Dragons of Nightmare saved variables.</summary>
public sealed class NightmareDragonsRow
{
    public int Id { get; set; }
    public long Perm1 { get; set; }
    public long Perm2 { get; set; }
    public long Perm3 { get; set; }
    public long Perm4 { get; set; }
    public long RespawnUnix { get; set; }
    public long RequiredUpdates { get; set; }
    public bool Active { get; set; }
    public int KilledMask { get; set; }
}

/// <summary>Characters schema 50: the weekly Emerald Dragon rotation (which dragon stands at which portal, the next spawn time, the dead).</summary>
public sealed class NightmareDragonsDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 50;
    public const string Table = "world_nightmare_dragons";
    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
        => modelBuilder.Entity<NightmareDragonsRow>(e =>
        {
            e.ToTable(Table);
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Perm1).HasColumnName("perm_1");
            e.Property(r => r.Perm2).HasColumnName("perm_2");
            e.Property(r => r.Perm3).HasColumnName("perm_3");
            e.Property(r => r.Perm4).HasColumnName("perm_4");
            e.Property(r => r.RespawnUnix).HasColumnName("respawn_unix");
            e.Property(r => r.RequiredUpdates).HasColumnName("required_updates");
            e.Property(r => r.Active).HasColumnName("active");
            e.Property(r => r.KilledMask).HasColumnName("killed_mask");
        });

    public void AddServices(IServiceCollection services) => services.AddScoped<INightmareDragonsStore, EfNightmareDragonsStore>();
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class EfNightmareDragonsStore(CharacterDbContext db) : INightmareDragonsStore
{
    public async Task<NightmareDragonsState?> LoadAsync(CancellationToken cancellationToken = default)
        => await db.Set<NightmareDragonsRow>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == 1, cancellationToken) is { } row
            ? new NightmareDragonsState([(uint)row.Perm1, (uint)row.Perm2, (uint)row.Perm3, (uint)row.Perm4], row.RespawnUnix,
                (uint)row.RequiredUpdates, row.Active, (byte)row.KilledMask)
            : null;

    public async Task SaveAsync(NightmareDragonsState state, CancellationToken cancellationToken = default)
    {
        NightmareDragonsRow? row = await db.Set<NightmareDragonsRow>().FirstOrDefaultAsync(r => r.Id == 1, cancellationToken);
        if (row is null) db.Add(row = new NightmareDragonsRow { Id = 1 });
        row.Perm1 = state.Permutation[0];
        row.Perm2 = state.Permutation[1];
        row.Perm3 = state.Permutation[2];
        row.Perm4 = state.Permutation[3];
        row.RespawnUnix = state.RespawnUnix;
        row.RequiredUpdates = state.RequiredUpdates;
        row.Active = state.Active;
        row.KilledMask = state.KilledMask;
        await db.SaveChangesAsync(cancellationToken);
    }
}
