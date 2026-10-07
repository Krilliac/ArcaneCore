using ArcaneCore.Data.Characters.Pets;
using ArcaneCore.Data.Characters.Playerbots;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Content.Names;
using ArcaneCore.Data.Skills;
using ArcaneCore.Data.World.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.Schema.Upgrade;

/// <summary>
/// The Codex line's numbering (codex/server-continue-20261004, last commit 0e07c29b) of the steps it added after the
/// shared history (characters and world 20). The 2026-10-07 merge kept the ccr numbers 21+ and moved the Codex modules
/// above them (docs/integration/codex-merge-20261007.md); databases the Codex line created still record the old
/// numbers. The objects below are what that commit's modules created (git show 0e07c29b:src/ArcaneCore.Data/...), not
/// what today's modules create: they identify a database written then. Auth needs nothing (the Codex auth step 4 is
/// this build's auth step 4).
/// </summary>
public static class CodexLine
{
    public const string Name = "Codex (0e07c29b)";

    /// <summary>The last version the Codex and ccr lines share for characters and world.</summary>
    public const int DivergedAfter = 20;

    /// <summary>The table of the dropped Codex world step 26 (NpcTemplateServiceMetadataModule).</summary>
    public const string NpcMetadataTable = "npc_template_service_metadata";

    public static ForeignLine Characters { get; } = new()
    {
        Name = Name,
        DivergedAfter = DivergedAfter,
        Steps =
        [
            new(21, PersistentPetDataModule.Version, "PersistentPetDataModule", [new("character_pet")]),
            new(22, ItemCooldownOwnerDataModule.Version, "ItemCooldownOwnerDataModule", [new(ItemCooldownOwnerDataModule.Table)]),
            new(23, PetCooldownDataModule.Version, "PetCooldownDataModule", [new("character_pet_cooldown")]),
            new(24, PetNamingDataModule.Version, "PetNamingDataModule",
            [
                new("character_pet", nameof(PersistentPetRow.Name)),
                new("character_pet", nameof(PersistentPetRow.NameTimestamp)),
                new("character_pet", nameof(PersistentPetRow.RenameAllowed)),
            ]),
            new(25, ManagedPlayerbotDataModule.Version, "ManagedPlayerbotDataModule", [new(ManagedPlayerbotDataModule.Table)]),
        ],
    };

    public static ForeignLine World { get; } = new()
    {
        Name = Name,
        DivergedAfter = DivergedAfter,
        Steps =
        [
            new(21, ReservedNameWorldDataModule.Version, "ReservedNameWorldDataModule", [new("reserved_name")]),
            new(22, ItemEnchantmentWorldDataModule.Version, "ItemEnchantmentWorldDataModule", [new("spell_proc_item_enchant")]),
            new(23, CreatureDisplayScaleDataModule.Version, "CreatureDisplayScaleDataModule",
            [
                new("creature_template", nameof(CreatureTemplateRow.DisplayScale2)),
                new("creature_template", nameof(CreatureTemplateRow.DisplayScale3)),
                new("creature_template", nameof(CreatureTemplateRow.DisplayScale4)),
            ]),
            new(24, SpellEnchantChargesWorldDataModule.Version, "SpellEnchantChargesWorldDataModule", [new("spell_enchant_charges")]),
            new(25, StartingSkillWorldDataModule.Version, "StartingSkillWorldDataModule", [new(StartingSkillWorldDataModule.Table)]),
            new(26, null, "NpcTemplateServiceMetadataModule",
            [
                new(NpcMetadataTable, "entry"),
                new(NpcMetadataTable, "gossip_menu_id"),
                new(NpcMetadataTable, "trainer_type"),
                new(NpcMetadataTable, "trainer_class"),
                new(NpcMetadataTable, "trainer_race"),
                new(NpcMetadataTable, "trainer_spell"),
            ]),
            new(27, CreatureTextTemplateDataModule.Version, "CreatureTextTemplateDataModule", [new(CreatureTextTemplateDataModule.Table)]),
        ],
        DataMoves =
        [
            new(26, CreatureNpcMetadataDataModule.Version,
                $"copy the NPC service metadata of {NpcMetadataTable} (Codex world step 26, dropped by the merge) into the creature_template " +
                $"columns of world step {CreatureNpcMetadataDataModule.Version} (the table itself is left in place)",
                CountNpcMetadataAsync,
                MoveNpcMetadataAsync),
        ],
    };

    /// <summary>The (column in creature_template, column in npc_template_service_metadata) pairs; the same vmangos fields (CreatureDefines.h:242,272-275).</summary>
    private static readonly (string Template, string Codex)[] s_npcColumns =
    [
        (nameof(CreatureTemplateRow.GossipMenuId), "gossip_menu_id"),
        (nameof(CreatureTemplateRow.TrainerType), "trainer_type"),
        (nameof(CreatureTemplateRow.TrainerClass), "trainer_class"),
        (nameof(CreatureTemplateRow.TrainerRace), "trainer_race"),
        (nameof(CreatureTemplateRow.TrainerSpell), "trainer_spell"),
    ];

    private static Task<long> CountNpcMetadataAsync(DbContext db, CancellationToken ct)
    {
        (string template, string metadata, string match) = NpcMetadataSql(db);
        return SchemaCatalog.ScalarAsync(db, $"SELECT COUNT(*) FROM {template} WHERE EXISTS {match}", ct);
    }

    /// <summary>
    /// One correlated UPDATE (portable: SQLite, MariaDB/MySQL and PostgreSQL accept it): every template with a Codex
    /// metadata row takes that row's five values. Running it again writes the same values.
    /// </summary>
    private static async Task<long> MoveNpcMetadataAsync(DbContext db, CancellationToken ct)
    {
        ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
        (string template, string metadata, string match) = NpcMetadataSql(db);
        string entry = $"{metadata}.{sql.DelimitIdentifier("entry")} = {template}.{sql.DelimitIdentifier(nameof(CreatureTemplateRow.Entry))}";
        string assignments = string.Join(", ", s_npcColumns.Select(c =>
            $"{sql.DelimitIdentifier(c.Template)} = (SELECT {metadata}.{sql.DelimitIdentifier(c.Codex)} FROM {metadata} WHERE {entry})"));
        // Only delimited constant identifiers, no values: nothing to parameterise.
        string update = $"UPDATE {template} SET {assignments} WHERE EXISTS {match}";
        return await db.Database.ExecuteSqlRawAsync(update, ct).ConfigureAwait(false);
    }

    private static (string Template, string Metadata, string Match) NpcMetadataSql(DbContext db)
    {
        ISqlGenerationHelper sql = db.GetService<ISqlGenerationHelper>();
        string template = sql.DelimitIdentifier("creature_template");
        string metadata = sql.DelimitIdentifier(NpcMetadataTable);
        string match = $"(SELECT 1 FROM {metadata} WHERE {metadata}.{sql.DelimitIdentifier("entry")} = " +
                       $"{template}.{sql.DelimitIdentifier(nameof(CreatureTemplateRow.Entry))})";
        return (template, metadata, match);
    }
}
