using ArcaneCore.Data.Content;
using ArcaneCore.Data.World.Pools;
using ArcaneCore.Data.World.SpawnGroups;
using ArcaneCore.Data.World.SpecialLoot;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Kernel.WorldData.Loot;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.World.GameObjects;

/// <summary>Reads every game object table into an immutable <see cref="GameObjectContent"/>.</summary>
public sealed class EfGameObjectDataStore(WorldDbContext db) : IGameObjectDataStore
{
    public async Task<GameObjectContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<GameObjectTemplateRow> templates = await db.Set<GameObjectTemplateRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<GameObjectSpawnRow> spawns = await db.Set<GameObjectSpawnRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<LockTemplateRow> locks = await db.Set<LockTemplateRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<GameObjectQuestStarterRow> starters = await db.Set<GameObjectQuestStarterRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<GameObjectQuestEnderRow> enders = await db.Set<GameObjectQuestEnderRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<GameObjectSpawnEntryRow> spawnEntries = await db.Set<GameObjectSpawnEntryRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        SpawnGroupCatalog groups = await SpawnGroupStore.LoadAsync(db, SpawnGroupType.GameObject, cancellationToken).ConfigureAwait(false);
        Kernel.WorldData.Pools.PoolCatalog pools = await PoolStore.LoadAsync(
            db, PoolSpawnKind.GameObject, spawns.GroupBy(s => s.Guid).ToDictionary(g => g.Key, g => (g.Last().Entry, g.Last().MapId)), cancellationToken).ConfigureAwait(false);

        return new GameObjectContent(
            templates.Select(t => new GameObjectTemplate
            {
                Entry = t.Entry,
                Type = t.Type,
                DisplayId = t.DisplayId,
                Name = t.Name,
                Faction = t.Faction,
                Flags = t.Flags,
                Size = t.Size,
                Data = t.GetData(),
                MinGold = t.MinGold,
                MaxGold = t.MaxGold,
            }),
            spawns.Select(s => new GameObjectSpawn
            {
                Guid = s.Guid,
                Entry = s.Entry,
                MapId = s.MapId,
                X = s.X,
                Y = s.Y,
                Z = s.Z,
                Orientation = s.Orientation,
                Rotation0 = s.Rotation0,
                Rotation1 = s.Rotation1,
                Rotation2 = s.Rotation2,
                Rotation3 = s.Rotation3,
                SpawnTimeSeconds = s.SpawnTimeSeconds,
                SpawnTimeMaxSeconds = s.SpawnTimeMaxSeconds,
                SpawnFlags = s.SpawnFlags,
                AnimProgress = s.AnimProgress,
                State = s.State,
            }),
            locks.Select(l =>
            {
                (uint[] types, uint[] indexes, uint[] skills) = l.Get();
                return new LockEntry(l.Id, types, indexes, skills);
            }),
            starters.Select(r => (r.Id, r.Quest)),
            enders.Select(r => (r.Id, r.Quest)),
            spawnEntries.Select(r => (r.SpawnGuid, r.Entry)))
        {
            SpawnGroups = groups,
            Pools = pools,
        };
    }
}

/// <summary>
/// Reads the loot tables (the five original ones plus the special-loot module's fishing, pickpocketing and disenchant
/// tables), <c>creature_loot_info</c>, <c>skill_fishing_base_level</c> and <c>creature_pickpocket_loot</c> into an
/// immutable <see cref="LootContent"/>.
/// </summary>
public sealed class EfLootDataStore(WorldDbContext db) : ILootDataStore
{
    public async Task<LootContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        var rows = new List<(LootTableKind, LootStoreRow)>();
        await AddAsync<CreatureLootTemplateRow>(LootTableKind.Creature).ConfigureAwait(false);
        await AddAsync<GameObjectLootTemplateRow>(LootTableKind.GameObject).ConfigureAwait(false);
        await AddAsync<ItemLootTemplateRow>(LootTableKind.Item).ConfigureAwait(false);
        await AddAsync<SkinningLootTemplateRow>(LootTableKind.Skinning).ConfigureAwait(false);
        await AddAsync<ReferenceLootTemplateRow>(LootTableKind.Reference).ConfigureAwait(false);
        await AddAsync<FishingLootTemplateRow>(LootTableKind.Fishing).ConfigureAwait(false);
        await AddAsync<PickpocketingLootTemplateRow>(LootTableKind.Pickpocketing).ConfigureAwait(false);
        await AddAsync<DisenchantLootTemplateRow>(LootTableKind.Disenchant).ConfigureAwait(false);
        List<CreatureLootInfoRow> creatures = await db.Set<CreatureLootInfoRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<SkillFishingBaseLevelRow> fishingBase = await db.Set<SkillFishingBaseLevelRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CreaturePickpocketLootRow> pickpocket = await db.Set<CreaturePickpocketLootRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return new LootContent(
            rows,
            creatures.Select(c => new CreatureLootInfo(c.Entry, c.LootId, c.SkinningLootId, c.MinGold, c.MaxGold)),
            fishingBase.Select(f => KeyValuePair.Create(f.Entry, f.Skill)),
            pickpocket.Select(p => KeyValuePair.Create(p.Entry, p.LootId)));

        async Task AddAsync<T>(LootTableKind kind)
            where T : LootTemplateRowBase
        {
            foreach (T r in await db.Set<T>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((kind, new LootStoreRow(r.Entry, r.Item, r.ChanceOrQuestChance, r.GroupId, r.MinCountOrRef, r.MaxCount, r.ConditionId)));
            }
        }
    }
}
