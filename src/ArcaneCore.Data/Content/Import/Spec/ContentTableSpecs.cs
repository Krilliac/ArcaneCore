using System.Collections.Frozen;

namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// The tables the dump importers read today, with their key, the columns they read and how the
/// dialects differ. The column lists are what <c>CreatureDumpImporter</c> (ReadTemplate,
/// ReadSpawn, ReadMovement, ReadModel, ReadAddon, ReadClassLevelStats, ReadAiScript, ReadAiText)
/// and <c>GameObjectLootDumpImporter</c> (ReadTemplate, ReadSpawn, ReadRelation,
/// ReadCreatureLoot, ReadLoot) look up by name; a spec test keeps them honest. A source table
/// without a spec here is counted and reported as not imported.
/// <para>
/// Dialect signatures cite the loader that fixes each name: vmangos
/// <c>ObjectMgr::LoadCreatureTemplates</c> (src/game/ObjectMgr.cpp:1190: <c>level_min</c>,
/// <c>display_id1</c>), <c>LoadCreatures</c> (:2319-2330: <c>wander_distance</c>,
/// <c>patch_min</c>), <c>LoadGameobjects</c> (:2527-2537: <c>patch_min</c>) and
/// <c>LootStore::LoadLootTable</c> (src/game/LootMgr.cpp:105: <c>patch_min</c>, <c>patch_max</c>);
/// cmangos mangos-classic <c>sql/base/mangos.sql</c> (<c>creature.spawnMask</c> :722,
/// <c>DisplayIdProbability1</c> added by sql/updates/mangos/z2823_01) and the z2815 classic-db
/// Full_DB dump (<c>ModelId1</c>, <c>MinLevel</c>, <c>spawnMask</c>).
/// </para>
/// </summary>
public static class ContentTableSpecs
{
    private static readonly string[] s_creatureTemplateColumns =
    [
        "Name", "SubName", "MinLevel", "MaxLevel", "level_min", "level_max",
        "DisplayId1", "DisplayId2", "DisplayId3", "DisplayId4", "ModelId1", "ModelId2", "ModelId3", "ModelId4",
        "display_id1", "display_id2", "display_id3", "display_id4",
        "DisplayIdProbability1", "DisplayIdProbability2", "DisplayIdProbability3", "DisplayIdProbability4",
        "display_probability1", "display_probability2", "display_probability3", "display_probability4",
        "Scale", "display_scale1", "Faction", "FactionAlliance", "faction", "NpcFlags", "npc_flags",
        "UnitFlags", "DynamicFlags", "CreatureTypeFlags", "CreatureType", "type", "Family", "pet_family", "Rank",
        "UnitClass", "unit_class", "InhabitType", "inhabit_type", "Civilian", "RacialLeader", "racial_leader",
        "SpeedWalk", "speed_walk", "SpeedRun", "speed_run",
        "MinLevelHealth", "MaxLevelHealth", "MinLevelMana", "MaxLevelMana", "Armor",
        "MinMeleeDmg", "MaxMeleeDmg", "MinRangedDmg", "MaxRangedDmg", "MeleeAttackPower", "RangedAttackPower",
        "MeleeBaseAttackTime", "base_attack_time", "RangedBaseAttackTime", "ranged_attack_time",
        "DamageSchool", "damage_school", "PetSpellDataId", "pet_spell_list_id", "MovementType", "movement_type",
        "CorpseDecay", "ExtraFlags", "flags_extra", "AIName", "ai_name",
        "patch", "static_flags1", "static_flags2", "health_multiplier", "mana_multiplier", "armor_multiplier",
        "damage_multiplier", "damage_variance",
        // GameObjectLootDumpImporter.ReadCreatureLoot
        "LootId", "loot_id", "SkinningLootId", "skinning_loot_id", "MinLootGold", "gold_min", "MaxLootGold", "gold_max",
        // GameObjectLootDumpImporter.ReadPickpocketId (classic-db PickpocketLootId, vmangos pickpocket_loot_id)
        "PickpocketLootId", "pickpocket_loot_id",
    ];

    private static readonly string[] s_spawnColumns =
    [
        "id", "map", "position_x", "position_y", "position_z", "orientation", "patch_min", "patch_max",
    ];

    private static readonly string[] s_lootColumns =
    [
        "ChanceOrQuestChance", "chance", "groupid", "mincountOrRef", "mincount", "maxcount", "condition_id", "conditionId",
        "patch_min", "patch_max",
    ];

    private static readonly DialectSignature[] s_spawnSignatures =
    [
        new(ContentDialect.CMangos, ["spawnMask"], ["patch_min"]),
        new(ContentDialect.VMangos, ["patch_min"], []),
    ];

    // item_template: cmangos z2815 `displayid` vs vmangos `display_id` + `patch`
    // (vmangos ObjectMgr.cpp:3820 LoadItemPrototypes select).
    private static readonly DialectSignature[] s_itemSignatures =
    [
        new(ContentDialect.CMangos, ["displayid"], ["patch", "display_id"]),
        new(ContentDialect.VMangos, ["display_id", "patch"], []),
    ];

    // quest_template: vmangos has RewXP and patch (ObjectMgr.cpp:5523-5566 LoadQuests select);
    // cmangos z2815 has neither and carries RewMoneyMaxLevel (Quest::XPValue derives the XP from it).
    private static readonly DialectSignature[] s_questSignatures =
    [
        new(ContentDialect.CMangos, ["RewMoneyMaxLevel"], ["patch", "RewXP"]),
        new(ContentDialect.VMangos, ["patch", "RewXP"], []),
    ];

    // creature_onkill_reputation: vmangos rows carry `patch` (ObjectMgr.cpp:8902); classic-db has none.
    private static readonly DialectSignature[] s_onKillSignatures =
    [
        new(ContentDialect.CMangos, ["RewOnKillRepFaction1"], ["patch"]),
        new(ContentDialect.VMangos, ["RewOnKillRepFaction1", "patch"], []),
    ];

    // playercreateinfo_spell and spell_target_position: vmangos selects WHERE 5875 BETWEEN build_min AND
    // build_max (ObjectMgr.cpp:4679, Spells/SpellMgr.cpp:52); classic-db has no build columns.
    private static readonly DialectSignature[] s_buildRangeSignatures =
    [
        new(ContentDialect.CMangos, [], ["build_min"]),
        new(ContentDialect.VMangos, ["build_min", "build_max"], []),
    ];

    // areatrigger_teleport: vmangos rows carry patch (ObjectMgr.cpp:7717); classic-db has none.
    private static readonly DialectSignature[] s_portalSignatures =
    [
        new(ContentDialect.CMangos, ["target_map"], ["patch"]),
        new(ContentDialect.VMangos, ["target_map", "patch"], []),
    ];

    // quest relations: vmangos filters on patch_min/patch_max (ObjectMgr.cpp:9178).
    private static readonly DialectSignature[] s_relationSignatures =
    [
        new(ContentDialect.CMangos, ["id", "quest"], ["patch_min"]),
        new(ContentDialect.VMangos, ["patch_min", "patch_max"], []),
    ];

    private static readonly DialectSignature[] s_lootSignatures =
    [
        new(ContentDialect.CMangos, ["condition_id"], ["patch_min"]),
        new(ContentDialect.VMangos, ["patch_min", "patch_max"], []),
    ];

    private static readonly TableSpec[] s_specs =
    [
        new("creature_template", [new KeyColumn("Entry")], s_creatureTemplateColumns,
        [
            new DialectSignature(ContentDialect.CMangosClassic, "ModelId1", "MinLevel"),
            new DialectSignature(ContentDialect.CMangosHead, "DisplayId1", "DisplayIdProbability1", "MinLevel"),
            new DialectSignature(ContentDialect.VMangos, "level_min", "display_id1"),
        ]),
        new("creature", [new KeyColumn("guid")],
            [.. s_spawnColumns, "spawntimesecsmin", "spawntimesecsmax", "spawntimesecs", "id2", "spawndist", "wander_distance", "MovementType", "movement_type"],
            s_spawnSignatures),
        new("creature_movement", [new KeyColumn("Id"), new KeyColumn("Point")],
            ["PositionX", "position_x", "PositionY", "position_y", "PositionZ", "position_z", "Orientation", "WaitTime", "Run", "run"], []),
        new("creature_model_info", [new KeyColumn("modelid", "display_id")],
            ["build", "bounding_radius", "combat_reach", "gender", "modelid_other_gender", "display_id_other_gender"], []),
        new("creature_display_info_addon", [new KeyColumn("modelid", "display_id")],
            ["build", "bounding_radius", "combat_reach", "gender", "modelid_other_gender", "display_id_other_gender"], []),
        new("creature_addon", [new KeyColumn("guid")],
            ["mount", "mount_display_id", "stand_state", "sheath_state", "emote", "emote_state"], []),
        new("creature_classlevelstats", [new KeyColumn("class"), new KeyColumn("level")],
            ["health", "mana", "armor", "melee_damage", "ranged_damage", "attack_power", "ranged_attack_power"], []),
        new("creature_ai_scripts", [new KeyColumn("id")],
            [
                "creature_id", "event_type", "event_inverse_phase_mask", "event_chance", "event_flags",
                "event_param1", "event_param2", "event_param3", "event_param4",
                "action1_type", "action1_param1", "action1_param2", "action1_param3",
                "action2_type", "action2_param1", "action2_param2", "action2_param3",
                "action3_type", "action3_param1", "action3_param2", "action3_param3", "comment",
            ], []),
        new("creature_ai_texts", [new KeyColumn("entry")], ["content_default", "type", "language", "emote"], []),
        new("gameobject_template", [new KeyColumn("entry")],
            [
                "type", "displayId", "display_id", "name", "faction", "flags", "size", "patch",
                .. Enumerable.Range(0, 24).Select(i => "data" + i),
            ], []),
        new("gameobject", [new KeyColumn("guid")],
            [
                .. s_spawnColumns, "rotation0", "rotation1", "rotation2", "rotation3", "spawntimesecs", "spawntimesecsmin",
                "animprogress", "state",
            ],
            s_spawnSignatures),
        new("gameobject_questrelation", [new KeyColumn("id"), new KeyColumn("quest")], ["patch_min", "patch_max"], []),
        new("gameobject_involvedrelation", [new KeyColumn("id"), new KeyColumn("quest")], ["patch_min", "patch_max"], []),
        new("creature_loot_template", [new KeyColumn("entry"), new KeyColumn("item")], s_lootColumns, s_lootSignatures),
        new("gameobject_loot_template", [new KeyColumn("entry"), new KeyColumn("item")], s_lootColumns, s_lootSignatures),
        new("item_loot_template", [new KeyColumn("entry"), new KeyColumn("item")], s_lootColumns, s_lootSignatures),
        new("skinning_loot_template", [new KeyColumn("entry"), new KeyColumn("item")], s_lootColumns, s_lootSignatures),
        new("reference_loot_template", [new KeyColumn("entry"), new KeyColumn("item")], s_lootColumns, s_lootSignatures),
        new("fishing_loot_template", [new KeyColumn("entry"), new KeyColumn("item")], s_lootColumns, s_lootSignatures),
        new("pickpocketing_loot_template", [new KeyColumn("entry"), new KeyColumn("item")], s_lootColumns, s_lootSignatures),
        new("disenchant_loot_template", [new KeyColumn("entry"), new KeyColumn("item")], s_lootColumns, s_lootSignatures),
        new("skill_fishing_base_level", [new KeyColumn("entry")], ["skill"], []),
        new("item_template", [new KeyColumn("entry")], [], s_itemSignatures, ItemQuestDumpImporter.ReadsItemColumn),
        new("quest_template", [new KeyColumn("entry")], [], s_questSignatures, ItemQuestDumpImporter.ReadsQuestColumn),
        new("creature_questrelation", [new KeyColumn("id"), new KeyColumn("quest")], ["patch_min", "patch_max"], s_relationSignatures),
        new("creature_involvedrelation", [new KeyColumn("id"), new KeyColumn("quest")], ["patch_min", "patch_max"], s_relationSignatures),
        new("playercreateinfo_item", [new KeyColumn("race"), new KeyColumn("class"), new KeyColumn("itemid")], ["amount"], []),
        new("creature_onkill_reputation", [new KeyColumn("creature_id")], [], s_onKillSignatures, OnKillReputationDumpImporter.ReadsColumn),
        new("playercreateinfo", [new KeyColumn("race"), new KeyColumn("class")], [], [], PlayerCreateDumpImporter.ReadsStartColumn),
        new("playercreateinfo_spell", [new KeyColumn("race"), new KeyColumn("class"), new KeyColumn("spell")], [], s_buildRangeSignatures, PlayerCreateDumpImporter.ReadsSpellColumn),
        new("playercreateinfo_action", [new KeyColumn("race"), new KeyColumn("class"), new KeyColumn("button")], [], [], PlayerCreateActionDumpImporter.ReadsColumn),
        new("spell_target_position", [new KeyColumn("id")], [], s_buildRangeSignatures, PlayerCreateDumpImporter.ReadsTargetColumn),
        new("player_levelstats", [new KeyColumn("race"), new KeyColumn("class"), new KeyColumn("level")], [], [], PlayerCreateDumpImporter.ReadsLevelColumn),
        new("player_classlevelstats", [new KeyColumn("class"), new KeyColumn("level")], [], [], PlayerCreateDumpImporter.ReadsClassColumn),
        new("areatrigger_teleport", [new KeyColumn("id")], [], s_portalSignatures, LocationDumpImporter.ReadsPortalColumn),
        new("game_tele", [new KeyColumn("id")], [], [], LocationDumpImporter.ReadsTeleColumn),
    ];

    private static readonly FrozenDictionary<string, TableSpec> s_byTable =
        s_specs.ToFrozenDictionary(s => s.Table, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every table spec, in declaration order.</summary>
    public static IReadOnlyList<TableSpec> All => s_specs;

    /// <summary>The spec for <paramref name="table"/> (case-insensitive), or <c>null</c> when no importer reads it.</summary>
    public static TableSpec? Find(string table) => s_byTable.GetValueOrDefault(table);
}
