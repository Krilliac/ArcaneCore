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
/// <b>Characters version 40</b>: the number the wave-2 plan reserves for the battlegrounds lane. Versions must be contiguous, so this branch holds
/// 35 to 39 open with empty steps (<see cref="BattlegroundLaneCharactersGap"/>); tests read <see cref="Version"/>, never a literal.
/// </para>
/// </summary>
public sealed class CharacterBattlegroundDataModule : IDataModule, ICharacterDataCleanup
{
    public const int Version = 40;

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

/// <summary>
/// Empty characters schema steps 35 to 39: the wave-2 plan reserves those numbers for other lanes and gives the battlegrounds lane 40, but
/// <see cref="DataModules.Compose"/> requires contiguous versions, so this branch holds the gap open with steps that change nothing and own no
/// rows. INTEGRATOR: delete each placeholder whose number a merged lane really uses (Compose reports "claimed twice" until you do); keep the
/// ones nobody claimed. Never ship a build with these placeholders to a live realm.
/// </summary>
public abstract class BattlegroundLaneCharactersGap(int version) : IDataModule, ICharacterDataCleanup
{
    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion { get; } = version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
    }

    public void AddServices(IServiceCollection services)
    {
    }

    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Characters step 35 held open for the lane that owns it (see <see cref="BattlegroundLaneCharactersGap"/>).</summary>
public sealed class BattlegroundLaneCharactersGap35() : BattlegroundLaneCharactersGap(Version)
{
    public const int Version = 35;
}

/// <summary>Characters step 36 held open for the lane that owns it (see <see cref="BattlegroundLaneCharactersGap"/>).</summary>
public sealed class BattlegroundLaneCharactersGap36() : BattlegroundLaneCharactersGap(Version)
{
    public const int Version = 36;
}

/// <summary>Characters step 37 held open for the lane that owns it (see <see cref="BattlegroundLaneCharactersGap"/>).</summary>
public sealed class BattlegroundLaneCharactersGap37() : BattlegroundLaneCharactersGap(Version)
{
    public const int Version = 37;
}

/// <summary>Characters step 38 held open for the lane that owns it (see <see cref="BattlegroundLaneCharactersGap"/>).</summary>
public sealed class BattlegroundLaneCharactersGap38() : BattlegroundLaneCharactersGap(Version)
{
    public const int Version = 38;
}

/// <summary>Characters step 39 held open for the lane that owns it (see <see cref="BattlegroundLaneCharactersGap"/>).</summary>
public sealed class BattlegroundLaneCharactersGap39() : BattlegroundLaneCharactersGap(Version)
{
    public const int Version = 39;
}
