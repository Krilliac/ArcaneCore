using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Data.Creatures;
using ArcaneCore.Data.Loot;
using ArcaneCore.Kernel.Instances;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Instances;

/// <summary>EF Core implementation of <see cref="IInstanceStore"/> over the characters database.</summary>
public sealed class EfInstanceStore(CharacterDbContext db) : IInstanceStore
{
    public async Task<InstanceStoreSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        // vmangos MapPersistentStateManager::CleanupInstances: binds of deleted characters or of
        // missing instances go first.
        await db.Set<CharacterInstanceRow>()
            .Where(b => !db.Characters.Any(c => c.Id == b.CharacterId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<CharacterInstanceRow>()
            .Where(b => !db.Set<InstanceRow>().Any(i => i.Id == b.InstanceId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<CharacterLastInstanceRow>()
            .Where(l => !db.Characters.Any(c => c.Id == l.CharacterId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // The group_instance half of CleanupInstances: rows of a deleted leader or of a missing instance.
        await db.Set<GroupInstanceRow>()
            .Where(b => !db.Characters.Any(c => c.Id == b.LeaderCharacterId) || !db.Set<InstanceRow>().Any(i => i.Id == b.InstanceId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        List<InstanceRow> instances = await db.Set<InstanceRow>().AsNoTracking().OrderBy(i => i.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CharacterInstanceRow> binds = await db.Set<CharacterInstanceRow>().AsNoTracking()
            .OrderBy(b => b.CharacterId).ThenBy(b => b.InstanceId).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<InstanceResetRow> resets = await db.Set<InstanceResetRow>().AsNoTracking().OrderBy(r => r.MapId).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CharacterLastInstanceRow> last = await db.Set<CharacterLastInstanceRow>().AsNoTracking().OrderBy(l => l.CharacterId).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<GroupInstanceRow> groupBinds = await db.Set<GroupInstanceRow>().AsNoTracking()
            .OrderBy(b => b.LeaderCharacterId).ThenBy(b => b.InstanceId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new InstanceStoreSnapshot(
            [.. instances.Select(i => new InstanceRecord((uint)i.Id, (uint)i.MapId, i.ResetTime))],
            [.. binds.Select(b => new CharacterInstanceBindRecord(b.CharacterId, (uint)b.InstanceId, b.Permanent))],
            [.. resets.Select(r => new InstanceResetRecord((uint)r.MapId, r.ResetTime))],
            [.. last.Select(l => new CharacterLastInstanceRecord(l.CharacterId, (uint)l.MapId, (uint)l.InstanceId))])
        {
            GroupBinds = [.. groupBinds.Select(b => new GroupInstanceBindRecord(b.LeaderCharacterId, (uint)b.InstanceId, b.Permanent))],
        };
    }

    public async Task SaveInstanceAsync(InstanceRecord instance, CancellationToken cancellationToken = default)
    {
        int id = (int)instance.Id;
        InstanceRow? row = await db.Set<InstanceRow>().FirstOrDefaultAsync(i => i.Id == id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new InstanceRow { Id = id };
            db.Set<InstanceRow>().Add(row);
        }

        row.MapId = (int)instance.MapId;
        row.ResetTime = instance.ResetTime;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteInstanceAsync(uint instanceId, CancellationToken cancellationToken = default)
    {
        int id = (int)instanceId;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<CharacterInstanceRow>().Where(b => b.InstanceId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<GroupInstanceRow>().Where(b => b.InstanceId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<CharacterLastInstanceRow>().Where(l => l.InstanceId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // Consumed/remaining chest loot lives and dies with its logical instance save. This is
        // hygiene; the startup purge in EfLootStateStore is what guards a reused instance id.
        await db.Set<LootStatePlayerRow>().Where(r => r.InstanceId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<LootStateItemRow>().Where(r => r.InstanceId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<LootStateRow>().Where(r => r.InstanceId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // The dead creatures of a reset instance come back with it (vmangos MapPersistentState deletes its creature_respawn rows).
        await db.Set<CreatureRespawnRow>().Where(r => r.InstanceId == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // A body in the instance stays (vmangos DeleteInstanceFromDB leaves the corpse table alone) but stops naming it: the id
        // can be handed out again after a restart. No new instance map adopts an instance-0 body; only a login inside an instance of
        // the same map takes it in (MapCombat.ResolveCorpseMap's legacy-row rule). InstanceManager.ForgetDeletedInstanceOfBodies does
        // the same to the bodies of online ghosts.
        await db.Set<CharacterCorpseRow>().Where(r => r.InstanceId == instanceId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.InstanceId, 0u), cancellationToken).ConfigureAwait(false);
        await db.Set<InstanceRow>().Where(i => i.Id == id).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveBindAsync(CharacterInstanceBindRecord bind, CancellationToken cancellationToken = default)
    {
        int instanceId = (int)bind.InstanceId;
        CharacterInstanceRow? row = await db.Set<CharacterInstanceRow>()
            .FirstOrDefaultAsync(b => b.CharacterId == bind.CharacterId && b.InstanceId == instanceId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            db.Set<CharacterInstanceRow>().Add(new CharacterInstanceRow { CharacterId = bind.CharacterId, InstanceId = instanceId, Permanent = bind.Permanent });
        }
        else
        {
            row.Permanent = bind.Permanent;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteBindAsync(int characterId, uint instanceId, CancellationToken cancellationToken = default)
    {
        int id = (int)instanceId;
        await db.Set<CharacterInstanceRow>()
            .Where(b => b.CharacterId == characterId && b.InstanceId == id)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveGroupBindAsync(GroupInstanceBindRecord bind, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bind);
        int instanceId = (int)bind.InstanceId;
        GroupInstanceRow? row = await db.Set<GroupInstanceRow>()
            .FirstOrDefaultAsync(b => b.LeaderCharacterId == bind.LeaderCharacterId && b.InstanceId == instanceId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            db.Set<GroupInstanceRow>().Add(new GroupInstanceRow { LeaderCharacterId = bind.LeaderCharacterId, InstanceId = instanceId, Permanent = bind.Permanent });
        }
        else
        {
            row.Permanent = bind.Permanent;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteGroupBindAsync(int leaderCharacterId, uint instanceId, CancellationToken cancellationToken = default)
    {
        int id = (int)instanceId;
        await db.Set<GroupInstanceRow>()
            .Where(b => b.LeaderCharacterId == leaderCharacterId && b.InstanceId == id)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveResetTimeAsync(InstanceResetRecord reset, CancellationToken cancellationToken = default)
    {
        int mapId = (int)reset.MapId;
        InstanceResetRow? row = await db.Set<InstanceResetRow>().FirstOrDefaultAsync(r => r.MapId == mapId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            db.Set<InstanceResetRow>().Add(new InstanceResetRow { MapId = mapId, ResetTime = reset.ResetTime });
        }
        else
        {
            row.ResetTime = reset.ResetTime;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveLastInstanceAsync(CharacterLastInstanceRecord last, CancellationToken cancellationToken = default)
    {
        CharacterLastInstanceRow? row = await db.Set<CharacterLastInstanceRow>()
            .FirstOrDefaultAsync(l => l.CharacterId == last.CharacterId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new CharacterLastInstanceRow { CharacterId = last.CharacterId };
            db.Set<CharacterLastInstanceRow>().Add(row);
        }

        row.MapId = (int)last.MapId;
        row.InstanceId = (int)last.InstanceId;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Only for a character id that has no characters row: the queued removal after a deletion
        // must not wipe a character recreated with the same id (docs/integration/character-delete.md).
        await db.Set<CharacterInstanceRow>().Where(b => b.CharacterId == characterId && !db.Characters.Any(c => c.Id == characterId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<CharacterLastInstanceRow>().Where(l => l.CharacterId == characterId && !db.Characters.Any(c => c.Id == characterId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await db.Set<GroupInstanceRow>().Where(b => b.LeaderCharacterId == characterId && !db.Characters.Any(c => c.Id == characterId))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }
}
