using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Data.Content;
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
        List<CreatureModelInfoRow> models = await db.Set<CreatureModelInfoRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CreatureAddonRow> addons = await db.Set<CreatureAddonRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CreatureAiScriptRow> scripts = await db.Set<CreatureAiScriptRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CreatureAiTextRow> texts = await db.Set<CreatureAiTextRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<BroadcastTextRow> broadcastTexts = await db.Set<BroadcastTextRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        List<CreatureAiSummonRow> summons = await db.Set<CreatureAiSummonRow>().AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);

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
                EventAiDialect.CMangos));
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
        Faction = r.Faction,
        NpcFlags = r.NpcFlags,
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
