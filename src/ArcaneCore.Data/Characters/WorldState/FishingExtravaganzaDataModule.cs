using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.WorldState;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.WorldState;

/// <summary>The single row of the vmangos VAR_STV_FISHING_* saved variables.</summary>
public sealed class FishingExtravaganzaRow
{
    public int Id { get; set; }
    public bool AnnounceBegin { get; set; }
    public bool AnnounceOver { get; set; }
    public bool HasWinner { get; set; }
    public long PreviousWinUnix { get; set; }
}

/// <summary>Characters schema 48: Riggle Bassbait's tournament state, so a restart neither repeats a yell nor allows a second winner.</summary>
public sealed class FishingExtravaganzaDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 48;
    public const string Table = "world_stv_fishing";
    public DatabaseComponent Component => DatabaseComponent.Characters;
    public int SchemaVersion => Version;
    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
        => modelBuilder.Entity<FishingExtravaganzaRow>(e =>
        {
            e.ToTable(Table);
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.AnnounceBegin).HasColumnName("announce_begin");
            e.Property(r => r.AnnounceOver).HasColumnName("announce_over");
            e.Property(r => r.HasWinner).HasColumnName("has_winner");
            e.Property(r => r.PreviousWinUnix).HasColumnName("previous_win_unix");
        });

    public void AddServices(IServiceCollection services) => services.AddScoped<IFishingExtravaganzaStore, EfFishingExtravaganzaStore>();
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class EfFishingExtravaganzaStore(CharacterDbContext db) : IFishingExtravaganzaStore
{
    public async Task<FishingExtravaganzaState?> LoadAsync(CancellationToken cancellationToken = default)
        => await db.Set<FishingExtravaganzaRow>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == 1, cancellationToken) is { } row
            ? new FishingExtravaganzaState(row.AnnounceBegin, row.AnnounceOver, row.HasWinner, row.PreviousWinUnix)
            : null;

    public async Task SaveAsync(FishingExtravaganzaState state, CancellationToken cancellationToken = default)
    {
        FishingExtravaganzaRow? row = await db.Set<FishingExtravaganzaRow>().FirstOrDefaultAsync(r => r.Id == 1, cancellationToken);
        if (row is null) db.Add(row = new FishingExtravaganzaRow { Id = 1 });
        row.AnnounceBegin = state.AnnounceBegin;
        row.AnnounceOver = state.AnnounceOver;
        row.HasWinner = state.HasWinner;
        row.PreviousWinUnix = state.PreviousWinTime;
        await db.SaveChangesAsync(cancellationToken);
    }
}
