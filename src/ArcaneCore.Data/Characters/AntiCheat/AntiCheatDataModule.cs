using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.AntiCheat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.AntiCheat;

/// <summary>One <c>character_anticheat_log</c> row (the fork's character_anticheat_violation, coalesced; docs/areas/anticheat.md).</summary>
public sealed class AntiCheatLogRow
{
    public long Id { get; set; }
    public int CharacterId { get; set; }
    public int AccountId { get; set; }
    public byte Type { get; set; }
    public float Weight { get; set; }
    public float Score { get; set; }
    public int Count { get; set; }
    public uint MapId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public string Detail { get; set; } = string.Empty;
    public long FirstAt { get; set; }
    public long LastAt { get; set; }

    public AntiCheatLogEntry ToEntry() => new(CharacterId, AccountId, Type, Weight, Score, Count, MapId, X, Y, Z, Detail, FirstAt, LastAt);

    public static AntiCheatLogRow From(AntiCheatLogEntry entry) => new()
    {
        CharacterId = entry.CharacterId, AccountId = entry.AccountId, Type = entry.Type, Weight = entry.Weight, Score = entry.Score,
        Count = entry.Count, MapId = entry.MapId, X = entry.X, Y = entry.Y, Z = entry.Z,
        Detail = entry.Detail.Length > AntiCheatDataModule.MaxDetailLength ? entry.Detail[..AntiCheatDataModule.MaxDetailLength] : entry.Detail,
        FirstAt = entry.FirstAt, LastAt = entry.LastAt,
    };
}

/// <summary>
/// Characters schema module of the anticheat lane (docs/areas/anticheat.md): the batched violation log. Version 41 (the
/// wave-6 integration renumbered it down from the lane's 43 so the schema has no placeholder gaps; the instance-persist lane takes 42).
/// </summary>
public sealed class AntiCheatDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 41;

    public const string LogTable = "character_anticheat_log";

    public const int MaxDetailLength = 128;

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(LogTable)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<AntiCheatLogRow>(entity =>
        {
            entity.ToTable(LogTable);
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(r => r.CharacterId).HasColumnName("character_id");
            entity.Property(r => r.AccountId).HasColumnName("account_id");
            entity.Property(r => r.Type).HasColumnName("type");
            entity.Property(r => r.Weight).HasColumnName("weight");
            entity.Property(r => r.Score).HasColumnName("score");
            entity.Property(r => r.Count).HasColumnName("count");
            entity.Property(r => r.MapId).HasColumnName("map");
            entity.Property(r => r.X).HasColumnName("x");
            entity.Property(r => r.Y).HasColumnName("y");
            entity.Property(r => r.Z).HasColumnName("z");
            entity.Property(r => r.Detail).HasColumnName("detail").HasMaxLength(MaxDetailLength).IsRequired();
            entity.Property(r => r.FirstAt).HasColumnName("first_at");
            entity.Property(r => r.LastAt).HasColumnName("last_at");
            entity.HasIndex(r => new { r.CharacterId, r.Id });
            entity.HasIndex(r => r.LastAt);
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IAntiCheatLogStore, EfAntiCheatLogStore>();

    /// <summary>The character's violation log goes with the character.</summary>
    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Set<AntiCheatLogRow>().Where(r => r.CharacterId == characterId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>EF Core implementation of <see cref="IAntiCheatLogStore"/>: one SaveChanges per batch.</summary>
public sealed class EfAntiCheatLogStore(CharacterDbContext db) : IAntiCheatLogStore
{
    public async Task AppendAsync(IReadOnlyList<AntiCheatLogEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        db.Set<AntiCheatLogRow>().AddRange(entries.Select(AntiCheatLogRow.From));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<AntiCheatLogEntry>> RecentAsync(int characterId, int limit, CancellationToken cancellationToken = default)
        => [.. (await db.Set<AntiCheatLogRow>().AsNoTracking().Where(r => r.CharacterId == characterId)
            .OrderByDescending(r => r.Id).Take(Math.Max(0, limit)).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(r => r.ToEntry())];

    public Task<int> DeleteAsync(int characterId, CancellationToken cancellationToken = default)
        => db.Set<AntiCheatLogRow>().Where(r => r.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken);
}
