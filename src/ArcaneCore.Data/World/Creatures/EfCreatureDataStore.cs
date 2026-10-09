using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.World.Pools;
using ArcaneCore.Data.World.SpawnGroups;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.World.Creatures;

/// <summary>Reads every creature table into an immutable <see cref="CreatureContent"/>.</summary>
public sealed class EfCreatureDataStore(WorldDbContext db) : ICreatureDataStore
{
    public async Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default)
    {
        List<CreatureTemplateRow> templates = await db.Set<CreatureTemplateRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CreatureSpawnRow> spawns = await db.Set<CreatureSpawnRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CreatureMovementRow> movement = await db.Set<CreatureMovementRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CreatureSpawnEntryRow> spawnEntries = await db.Set<CreatureSpawnEntryRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CreatureMovementTemplateRow> entryPaths = await db.Set<CreatureMovementTemplateRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CreatureModelInfoRow> models = await db.Set<CreatureModelInfoRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CreatureAddonRow> addons = await db.Set<CreatureAddonRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CreatureAiScriptRow> scripts = await db.Set<CreatureAiScriptRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CreatureAiTextRow> texts = await db.Set<CreatureAiTextRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<BroadcastTextRow> broadcastTexts = await db.Set<BroadcastTextRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CreatureAiSummonRow> summons = await db.Set<CreatureAiSummonRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CreatureTextTemplateRow> textTemplates = await db.Set<CreatureTextTemplateRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<RelayScriptRow> relaySteps = await db.Set<RelayScriptRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<RelayScriptTemplateRow> relayTemplates = await db.Set<RelayScriptTemplateRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var dbScripts = new List<DbScriptRow>();
        dbScripts.AddRange(await db.Set<QuestStartScriptRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false));
        dbScripts.AddRange(await db.Set<QuestEndScriptRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false));
        dbScripts.AddRange(await db.Set<GossipScriptRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false));
        dbScripts.AddRange(await db.Set<EventScriptRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false));
        List<ScriptWaypointRow> scriptWaypoints = await db.Set<ScriptWaypointRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        SpawnGroupCatalog spawnGroups = await SpawnGroupStore.LoadAsync(db, SpawnGroupType.Creature, cancellationToken).ConfigureAwait(false);
        Kernel.WorldData.Pools.PoolCatalog pools = await PoolStore.LoadAsync(
            db, PoolSpawnKind.Creature, spawns.GroupBy(s => s.Guid).ToDictionary(g => g.Key, g => (g.Last().Entry, g.Last().MapId)), cancellationToken).ConfigureAwait(false);

        return new CreatureContent(
            templates.Select(ToTemplate),
            spawns.Select(s => new CreatureSpawn
            {
                Guid = s.Guid,
                Entry = s.Entry,
                MapId = s.MapId,
                X = s.X,
                Y = s.Y,
                Z = s.Z,
                Orientation = s.Orientation,
                SpawnTimeMinSeconds = s.SpawnTimeMinSeconds,
                SpawnTimeMaxSeconds = s.SpawnTimeMaxSeconds,
                WanderDistance = s.WanderDistance,
                MovementType = s.MovementType,
            }),
            movement.Select(m => (m.SpawnGuid, new CreatureWaypoint(m.Point, m.X, m.Y, m.Z, m.Orientation, m.WaitTimeMs) { Run = m.Run })),
            models.Select(m => new CreatureModelInfo(m.DisplayId, m.BoundingRadius, m.CombatReach, m.Gender, m.DisplayIdOtherGender)),
            addons.Select(a => new CreatureAddon(a.Guid, a.MountDisplayId, a.StandState, a.SheathState, a.EmoteState)),
            new CreatureAiContent(
                scripts.Select(CreatureAiDataModule.ToEvent),
                texts.Select(CreatureAiDataModule.ToText),
                new BroadcastTextCatalog(broadcastTexts.Select(ToBroadcastText)),
                summons.Select(s => new CreatureAiSummon(s.Id, s.X, s.Y, s.Z, s.Orientation, s.SpawnTimeSeconds)),
                EventAiDialect.CMangos,
                textTemplates.Select(row => new CreatureAiTextChoice(row.Id, row.TargetId, row.Chance)))
            {
                RelayScripts = new RelayScriptCatalog(
                    relaySteps.Select(RelayScriptDataModule.ToStep),
                    relayTemplates.Select(row => new RelayScriptTemplateChoice(row.Id, row.RelayId, row.Chance))),
                DbScripts = new DbScriptCatalog(dbScripts.Select(row => (DbScriptDataModule.KindOf(row), DbScriptDataModule.ToStep(row)))),
            },
            entryPaths.Select(p => (p.Entry, p.PathId, new CreatureWaypoint(p.Point, p.X, p.Y, p.Z, p.Orientation, p.WaitTimeMs))),
            spawnEntries.Select(e => (e.SpawnGuid, e.Entry)),
            scriptWaypoints.Select(DbScriptDataModule.ToWaypoint))
        {
            SpawnGroups = spawnGroups,
            Pools = pools,
        };
    }

    internal static BroadcastText ToBroadcastText(BroadcastTextRow r) => new(
        r.Id, r.Text, r.FemaleText, r.ChatType, r.Language, r.SoundId,
        [r.EmoteId1, r.EmoteId2, r.EmoteId3], [r.EmoteDelay1, r.EmoteDelay2, r.EmoteDelay3]);

    internal static CreatureTemplate ToTemplate(CreatureTemplateRow r) => new()
    {
        Entry = r.Entry,
        Name = r.Name,
        SubName = r.SubName,
        MinLevel = r.MinLevel,
        MaxLevel = r.MaxLevel,
        DisplayIds = [r.DisplayId1, r.DisplayId2, r.DisplayId3, r.DisplayId4],
        DisplayProbabilities = [r.DisplayProbability1, r.DisplayProbability2, r.DisplayProbability3, r.DisplayProbability4],
        Scale = r.Scale,
        DisplayScales = [r.Scale, r.DisplayScale2, r.DisplayScale3, r.DisplayScale4],
        Faction = r.Faction,
        NpcFlags = r.NpcFlags,
        GossipMenuId = r.GossipMenuId,
        TrainerType = r.TrainerType,
        TrainerClass = r.TrainerClass,
        TrainerRace = r.TrainerRace,
        TrainerSpell = r.TrainerSpell,
        UnitFlags = r.UnitFlags,
        DynamicFlags = r.DynamicFlags,
        TypeFlags = r.TypeFlags,
        CreatureType = r.CreatureType,
        Family = r.Family,
        Rank = r.Rank,
        UnitClass = r.UnitClass,
        InhabitType = r.InhabitType,
        Civilian = r.Civilian,
        RacialLeader = r.RacialLeader,
        SpeedWalk = r.SpeedWalk,
        SpeedRun = r.SpeedRun,
        MinLevelHealth = r.MinLevelHealth,
        MaxLevelHealth = r.MaxLevelHealth,
        MinLevelMana = r.MinLevelMana,
        MaxLevelMana = r.MaxLevelMana,
        Armor = r.Armor,
        MinMeleeDamage = r.MinMeleeDamage,
        MaxMeleeDamage = r.MaxMeleeDamage,
        MinRangedDamage = r.MinRangedDamage,
        MaxRangedDamage = r.MaxRangedDamage,
        MeleeAttackPower = r.MeleeAttackPower,
        RangedAttackPower = r.RangedAttackPower,
        MeleeBaseAttackTime = r.MeleeBaseAttackTime,
        RangedBaseAttackTime = r.RangedBaseAttackTime,
        DamageSchool = r.DamageSchool,
        PetSpellDataId = r.PetSpellDataId,
        MovementType = r.MovementType,
        CorpseDecaySeconds = r.CorpseDecaySeconds,
        ExtraFlags = r.ExtraFlags,
        AIName = r.AIName,
        ScriptName = r.ScriptName,
        Detection = r.Detection ?? CreatureTemplate.DefaultDetectionRange,
        CallForHelp = r.CallForHelp,
        Pursuit = r.Pursuit,
        Leash = r.Leash,
        Timeout = r.Timeout,
        StaticFlags1 = r.StaticFlags1,
        StaticFlags2 = r.StaticFlags2,
        ExtraFlagsDialect = (CreatureExtraFlagsDialect)r.ExtraFlagsDialect,
    };
}
