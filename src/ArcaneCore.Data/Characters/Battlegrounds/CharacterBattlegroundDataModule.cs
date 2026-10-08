using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Characters.Battlegrounds;

/// <summary><c>character_battleground_data</c> row (vmangos columns <c>guid, instance_id, team, join_x, join_y, join_z, join_o, join_map</c>).</summary>
public sealed class CharacterBattlegroundRow
{
    public int CharacterId { get; set; }

    public uint InstanceId { get; set; }

    public uint Team { get; set; }

    public float JoinX { get; set; }

    public float JoinY { get; set; }

    public float JoinZ { get; set; }

    public float JoinOrientation { get; set; }

    public uint JoinMapId { get; set; }
}

/// <summary>
/// The battleground binding of a character (vmangos <c>character_battleground_data</c>, Player.cpp:20950-20982): written when the character
/// enters a match, removed when it leaves; a login on a battleground map reads it to return the character to where it joined from.
/// <para>
/// <b>Characters version 39</b>: reserved as 40 in the wave-2 plan and renumbered down at the 2026-10-07 integration, which
/// closed the unclaimed numbers (docs/integration/wave2-20261007.md). Tests read <see cref="Version"/>, never a literal.
/// </para>
/// </summary>
public sealed class CharacterBattlegroundDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 39; // reserved as 40 in the wave-2 plan; renumbered down at the 2026-10-07 integration (no gaps)

    public const string Table = "character_battleground_data";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CharacterBattlegroundRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => r.CharacterId);
            entity.Property(r => r.CharacterId).HasColumnName("guid").ValueGeneratedNever();
            entity.Property(r => r.InstanceId).HasColumnName("instance_id");
            entity.Property(r => r.Team).HasColumnName("team");
            entity.Property(r => r.JoinX).HasColumnName("join_x");
            entity.Property(r => r.JoinY).HasColumnName("join_y");
            entity.Property(r => r.JoinZ).HasColumnName("join_z");
            entity.Property(r => r.JoinOrientation).HasColumnName("join_o");
            entity.Property(r => r.JoinMapId).HasColumnName("join_map");
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IBattlegroundEntryPointStore, EfBattlegroundEntryPointStore>();

    public async Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Set<CharacterBattlegroundRow>().Where(r => r.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>EF Core implementation of <see cref="IBattlegroundEntryPointStore"/>.</summary>
public sealed class EfBattlegroundEntryPointStore(CharacterDbContext db) : IBattlegroundEntryPointStore
{
    public async Task<BattlegroundEntryPointRecord?> LoadAsync(int characterId, CancellationToken cancellationToken = default)
    {
        CharacterBattlegroundRow? row = await db.Set<CharacterBattlegroundRow>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.CharacterId == characterId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : new BattlegroundEntryPointRecord(row.CharacterId, row.InstanceId, row.Team, row.JoinMapId, row.JoinX, row.JoinY, row.JoinZ, row.JoinOrientation);
    }

    public async Task SaveAsync(BattlegroundEntryPointRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<CharacterBattlegroundRow>().Where(r => r.CharacterId == record.CharacterId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.Set<CharacterBattlegroundRow>().Add(new CharacterBattlegroundRow
        {
            CharacterId = record.CharacterId,
            InstanceId = record.InstanceId,
            Team = record.Team,
            JoinMapId = record.JoinMapId,
            JoinX = record.JoinX,
            JoinY = record.JoinY,
            JoinZ = record.JoinZ,
            JoinOrientation = record.JoinOrientation,
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default)
        => db.Set<CharacterBattlegroundRow>().Where(r => r.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken);
}
