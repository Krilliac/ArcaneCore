using System.Text.Json;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Npc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Npc;

/// <summary>A saved flight's remaining route. The standard character row keeps the in-flight position.</summary>
public sealed class CharacterTaxiFlightRow
{
    public int CharacterId { get; set; }

    public string Nodes { get; set; } = string.Empty;

    public string Paths { get; set; } = string.Empty;

    public string Costs { get; set; } = string.Empty;
}

/// <summary>Characters schema v21: a flight can resume after logout (vmangos PlayerTaxi destinations).</summary>
public sealed class CharacterTaxiFlightDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 21;
    public const string Table = "character_taxi_flight";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CharacterTaxiFlightRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.CharacterId);
            entity.Property(r => r.CharacterId).HasColumnName("guid").ValueGeneratedNever();
            entity.Property(r => r.Nodes).HasColumnName("nodes").IsRequired();
            entity.Property(r => r.Paths).HasColumnName("paths").IsRequired();
            entity.Property(r => r.Costs).HasColumnName("costs").IsRequired();
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<ICharacterTaxiFlightStore, EfCharacterTaxiFlightStore>();

    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
        => db.Set<CharacterTaxiFlightRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken);
}

public sealed class EfCharacterTaxiFlightStore(CharacterDbContext db) : ICharacterTaxiFlightStore
{
    public async Task<TaxiFlightRoute?> LoadAsync(int characterId, CancellationToken cancellationToken = default)
    {
        CharacterTaxiFlightRow? row = await db.Set<CharacterTaxiFlightRow>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var route = new TaxiFlightRoute(
            JsonSerializer.Deserialize<uint[]>(row.Nodes) ?? [],
            JsonSerializer.Deserialize<uint[]>(row.Paths) ?? [],
            JsonSerializer.Deserialize<uint[]>(row.Costs) ?? []);
        if (!route.IsValid)
        {
            throw new InvalidDataException($"character {characterId} has an invalid taxi route");
        }

        return route;
    }

    public async Task SaveAsync(int characterId, TaxiFlightRoute route, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!route.IsValid)
        {
            throw new ArgumentException("taxi route must have matching nodes, paths and costs", nameof(route));
        }

        CharacterTaxiFlightRow? row = await db.Set<CharacterTaxiFlightRow>()
            .SingleOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new CharacterTaxiFlightRow { CharacterId = characterId };
            db.Add(row);
        }

        row.Nodes = JsonSerializer.Serialize(route.Nodes);
        row.Paths = JsonSerializer.Serialize(route.PathIds);
        row.Costs = JsonSerializer.Serialize(route.LegCosts);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default)
        => db.Set<CharacterTaxiFlightRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken);
}
