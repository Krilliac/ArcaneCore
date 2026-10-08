using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Instances;

/// <summary>
/// A group's permanent bind to an instance, stored under the character id of the group's leader (vmangos <c>group_instance</c>:
/// leader_guid, instance, permanent).
/// </summary>
public sealed class GroupInstanceRow
{
    public int LeaderCharacterId { get; set; }

    public int InstanceId { get; set; }

    public bool Permanent { get; set; }
}

/// <summary>
/// The <c>group_instance</c> table of the characters database (review finding 89): the permanent bind a raid group gets when its
/// leader is locked inside, kept across restarts so the leader's next group is locked to the same instance and its members follow
/// (<c>InstanceManager.RestoreStoredGroupBinds</c>). <see cref="EfInstanceStore"/> reads and writes it; deleting the instance deletes
/// its rows (vmangos MapPersistentStateMgr.cpp:756) and deleting the leader deletes the leader's rows (vmangos
/// CharacterDatabaseCleaner, group_instance.leader_guid).
/// </summary>
public sealed class GroupInstanceBindDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>The single place the schema version is set (characters 35, reserved for the wave-2 teleport/death lane).</summary>
    public const int Version = 35;

    public const string Table = "group_instance";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(Table)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<GroupInstanceRow>(entity =>
        {
            entity.ToTable(Table);
            entity.HasKey(r => new { r.LeaderCharacterId, r.InstanceId });
            entity.HasIndex(r => r.InstanceId);
        });
    }

    public void AddServices(IServiceCollection services)
    {
        // EfInstanceStore (InstanceDataModule) reads and writes the table with the other instance tables.
    }

    /// <summary>The rows the deleted character leads (vmangos CharacterDatabaseCleaner: group_instance.leader_guid).</summary>
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Set<GroupInstanceRow>().Where(r => r.LeaderCharacterId == characterId).ExecuteDeleteAsync(cancellationToken);
    }
}
