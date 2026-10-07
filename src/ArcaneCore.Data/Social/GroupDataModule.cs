using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.Social;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.Data.Social;

/// <summary>
/// A party or raid (vmangos <c>groups</c>: group_id, leader_guid, loot_method, looter_guid, loot_threshold, icon1..icon8,
/// is_raid). The table is called <c>character_group</c>: <c>groups</c> is a reserved word in MySQL 8 and MariaDB
/// (docs/integration/sql-reserved-names-20261004.md).
/// </summary>
public sealed class GroupRow
{
    public long GroupId { get; set; }
    public int LeaderId { get; set; }
    public byte LootMethod { get; set; }
    public int LooterId { get; set; }
    public byte LootThreshold { get; set; }
    public bool IsRaid { get; set; }
    public long Icon1 { get; set; }
    public long Icon2 { get; set; }
    public long Icon3 { get; set; }
    public long Icon4 { get; set; }
    public long Icon5 { get; set; }
    public long Icon6 { get; set; }
    public long Icon7 { get; set; }
    public long Icon8 { get; set; }
}

/// <summary>
/// A member of a group (vmangos <c>group_member</c>: group_id, member_guid, assistant, subgroup), keyed by the member: a
/// character is in at most one group. <see cref="Slot"/> keeps the member order, which decides who leads when the leader
/// leaves (vmangos Group::_chooseLeader walks the member list); vmangos does not store it.
/// </summary>
public sealed class GroupMemberRow
{
    public int CharacterId { get; set; }
    public long GroupId { get; set; }
    public byte Slot { get; set; }
    public byte SubGroup { get; set; }
    public bool Assistant { get; set; }
}

/// <summary>
/// Groups survive a world restart (vmangos stores them in <c>groups</c> / <c>group_member</c> and ObjectMgr::LoadGroups
/// reads them at start). Characters schema <see cref="Version"/>; both tables are new, so the step is additive and
/// re-runnable. Unsigned 32-bit ids and 64-bit guids are stored in signed 64-bit columns (no unsigned types on
/// PostgreSQL), and the target icons are eight columns as in vmangos.
/// </summary>
public sealed class GroupDataModule : IDataModule, ICharacterDataCleanup
{
    /// <summary>The characters schema version of this module (wave-2 lane ops-social reservation 37).</summary>
    public const int Version = 37;

    public const string GroupTable = "character_group";

    public const string MemberTable = "character_group_member";

    public DatabaseComponent Component => DatabaseComponent.Characters;

    public int SchemaVersion => Version;

    public IReadOnlyList<SchemaChange> SchemaChanges { get; } = [new CreateTableChange(GroupTable), new CreateTableChange(MemberTable)];

    public void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GroupRow>(entity =>
        {
            entity.ToTable(GroupTable);
            entity.HasKey(r => r.GroupId);
            entity.Property(r => r.GroupId).ValueGeneratedNever();
        });
        modelBuilder.Entity<GroupMemberRow>(entity =>
        {
            entity.ToTable(MemberTable);
            entity.HasKey(r => r.CharacterId);
            entity.Property(r => r.CharacterId).ValueGeneratedNever();
            entity.HasIndex(r => r.GroupId);
        });
    }

    public void AddServices(IServiceCollection services) => services.AddScoped<IGroupStore, EfGroupStore>();

    /// <summary>A deleted character leaves its group (vmangos Player::DeleteFromDB → RemoveFromGroup); a group left with fewer than two is dropped at the next load.</summary>
    public Task DeleteCharacterDataAsync(CharacterDbContext db, int characterId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Set<GroupMemberRow>().Where(r => r.CharacterId == characterId).ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>EF Core implementation of <see cref="IGroupStore"/>.</summary>
public sealed class EfGroupStore(CharacterDbContext db) : IGroupStore
{
    public async Task<IReadOnlyList<GroupRecord>> LoadGroupsAsync(CancellationToken cancellationToken = default)
    {
        List<GroupRow> groups = await db.Set<GroupRow>().AsNoTracking().OrderBy(g => g.GroupId).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<GroupMemberRow> members = await db.Set<GroupMemberRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        ILookup<long, GroupMemberRow> byGroup = members.ToLookup(m => m.GroupId);
        return [.. groups.Select(g => new GroupRecord(
            (uint)g.GroupId, g.LeaderId, g.LootMethod, g.LooterId, g.LootThreshold, g.IsRaid,
            [(ulong)g.Icon1, (ulong)g.Icon2, (ulong)g.Icon3, (ulong)g.Icon4, (ulong)g.Icon5, (ulong)g.Icon6, (ulong)g.Icon7, (ulong)g.Icon8],
            [.. byGroup[g.GroupId].OrderBy(m => m.Slot).ThenBy(m => m.CharacterId).Select(m => new GroupMemberRecord(m.CharacterId, m.SubGroup, m.Assistant))]))];
    }

    public async Task SaveGroupAsync(GroupRecord group, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.TargetIcons.Count != 8)
        {
            throw new ArgumentException("a group has eight target icons", nameof(group));
        }

        long id = group.Id;
        List<int> memberIds = [.. group.Members.Select(m => m.CharacterId)];
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<GroupMemberRow>().Where(m => m.GroupId == id || memberIds.Contains(m.CharacterId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        GroupRow? row = await db.Set<GroupRow>().FirstOrDefaultAsync(g => g.GroupId == id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new GroupRow { GroupId = id };
            db.Set<GroupRow>().Add(row);
        }

        row.LeaderId = group.LeaderId;
        row.LootMethod = group.LootMethod;
        row.LooterId = group.LooterId;
        row.LootThreshold = group.LootThreshold;
        row.IsRaid = group.IsRaid;
        row.Icon1 = (long)group.TargetIcons[0];
        row.Icon2 = (long)group.TargetIcons[1];
        row.Icon3 = (long)group.TargetIcons[2];
        row.Icon4 = (long)group.TargetIcons[3];
        row.Icon5 = (long)group.TargetIcons[4];
        row.Icon6 = (long)group.TargetIcons[5];
        row.Icon7 = (long)group.TargetIcons[6];
        row.Icon8 = (long)group.TargetIcons[7];
        byte slot = 0;
        foreach (GroupMemberRecord member in group.Members)
        {
            db.Set<GroupMemberRow>().Add(new GroupMemberRow
            {
                CharacterId = member.CharacterId,
                GroupId = id,
                Slot = slot++,
                SubGroup = member.SubGroup,
                Assistant = member.Assistant,
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    public async Task DeleteGroupAsync(uint groupId, CancellationToken cancellationToken = default)
    {
        long id = groupId;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<GroupMemberRow>().Where(m => m.GroupId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<GroupRow>().Where(g => g.GroupId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
