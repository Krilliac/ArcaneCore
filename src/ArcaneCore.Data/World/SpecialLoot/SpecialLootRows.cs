using ArcaneCore.Data.World.GameObjects;

namespace ArcaneCore.Data.World.SpecialLoot;

/// <summary><c>fishing_loot_template</c>: keyed by area id (sub-zone or zone); entry 0 is the failed-cast junk table.</summary>
public sealed class FishingLootTemplateRow : LootTemplateRowBase;

/// <summary><c>pickpocketing_loot_template</c>: keyed by <c>creature_template</c> pickpocket loot id.</summary>
public sealed class PickpocketingLootTemplateRow : LootTemplateRowBase;

/// <summary><c>disenchant_loot_template</c>: keyed by <c>item_template.DisenchantID</c>.</summary>
public sealed class DisenchantLootTemplateRow : LootTemplateRowBase;

/// <summary>
/// <c>skill_fishing_base_level</c> (mangos-classic sql/base/mangos.sql:10512; vmangos
/// ObjectMgr::LoadFishingBaseSkillLevel): the fishing skill an area needs. The value is signed (classic-db has -70
/// and -20 rows) and 0 reads as "no row" exactly like vmangos.
/// </summary>
public sealed class SkillFishingBaseLevelRow
{
    /// <summary>Area id (AreaTable.dbc), a zone or a sub-zone.</summary>
    public uint Entry { get; set; }

    public int Skill { get; set; }
}

/// <summary>
/// The pickpocket loot id of a creature (<c>creature_template.PickpocketLootId</c> in classic-db,
/// <c>pickpocket_loot_id</c> in vmangos), kept in the special-loot module so the creatures schema is not edited
/// (same approach as <c>creature_loot_info</c>).
/// </summary>
public sealed class CreaturePickpocketLootRow
{
    public uint Entry { get; set; }

    public uint LootId { get; set; }
}