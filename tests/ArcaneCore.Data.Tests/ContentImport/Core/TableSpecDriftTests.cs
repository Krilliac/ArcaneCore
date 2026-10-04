using System.Text.Json;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Data.World.GameObjects;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Core;

/// <summary>
/// <see cref="ContentTableSpecs"/> says which source columns the importers read. These tests keep
/// that list honest against the importers themselves: for every column a spec calls mapped,
/// setting it on an otherwise empty row must change what the importer would write (in at least
/// one of the cmangos and vmangos layouts), and columns the importers do not read must not.
/// Columns whose effect is a row filter or needs a second table (patch ranges, build, class
/// level stats) are covered by the existing dialect tests, not here.
/// </summary>
public sealed class TableSpecDriftTests
{
    // Not observable in isolation: row filters (patch/build), a warning only (id2), vmangos stat
    // multipliers (applied only together with creature_classlevelstats).
    private static readonly HashSet<string> s_notObservableAlone = new(StringComparer.OrdinalIgnoreCase)
    {
        "patch", "patch_min", "patch_max", "build", "id2",
        "health_multiplier", "mana_multiplier", "armor_multiplier", "damage_multiplier", "damage_variance",
    };

    // Columns of the real classic-db tables that no importer reads (names taken from the cmangos schema).
    private static readonly string[] s_unmappedSamples =
        ["trainer_id", "KillCredit1", "ScriptName", "Comment", "StringId1", "ResistanceFire", "MechanicImmuneMask", "VendorTemplateId", "equipment_id"];

    public static IEnumerable<object[]> ObservableTables()
    {
        foreach (string table in new[]
        {
            "creature_template", "creature", "creature_movement", "creature_model_info", "creature_display_info_addon",
            "creature_addon", "gameobject", "creature_loot_template", "gameobject_loot_template", "item_loot_template",
            "skinning_loot_template", "reference_loot_template",
            "fishing_loot_template", "pickpocketing_loot_template", "disenchant_loot_template",
        })
        {
            yield return [table];
        }
    }

    [Theory]
    [MemberData(nameof(ObservableTables))]
    public void EveryMappedColumn_ChangesWhatTheImporterWrites(string table)
    {
        TableSpec spec = ContentTableSpecs.Find(table)!;
        string[] keys = [.. spec.Keys.Select(k => k.Canonical)];
        var unread = new List<string>();
        foreach (string column in Columns(table))
        {
            if (spec.Keys.Any(k => k.Names.Contains(column, StringComparer.OrdinalIgnoreCase)) || s_notObservableAlone.Contains(column))
            {
                continue;
            }

            if (!spec.IsMapped(column) || !Changes(table, spec, keys, column))
            {
                unread.Add(column);
            }
        }

        Assert.True(unread.Count == 0, $"{table}: spec columns the importer does not read: {string.Join(", ", unread)}");
    }

    [Theory]
    [MemberData(nameof(ObservableTables))]
    public void ColumnsNoImporterReads_DoNotChangeWhatItWrites(string table)
    {
        TableSpec spec = ContentTableSpecs.Find(table)!;
        string[] keys = [.. spec.Keys.Select(k => k.Canonical)];
        foreach (string column in s_unmappedSamples)
        {
            Assert.False(spec.IsMapped(column), $"{table}.{column} is listed as mapped");
            Assert.False(Changes(table, spec, keys, column), $"{table}.{column} changes the import but the spec calls it unmapped");
        }
    }

    private static IEnumerable<string> Columns(string table) => ContentTableSpecs.Find(table)!.MappedColumns;

    // Flag columns need bits the importer actually translates; everything else just needs a non-default value.
    private static string ValueFor(string column)
        => column.StartsWith("static_flags", StringComparison.OrdinalIgnoreCase) ? "4294967295" : "7";

    private static bool Changes(string table, TableSpec spec, string[] keys, string column)
    {
        string value = ValueFor(column);
        // A column can only be read in one dialect: compare against both baselines.
        foreach (string marker in new[] { string.Empty, MarkerFor(table, vmangos: true) })
        {
            string baseline = Import(table, keys, marker, null, value);
            string changed = Import(table, keys, marker, column, value);
            if (baseline != changed)
            {
                return true;
            }
        }

        return false;
    }

    private static string MarkerFor(string table, bool vmangos)
        => table == "creature_template" && vmangos ? "level_min" : string.Empty;

    private static string Import(string table, string[] keys, string marker, string? column, string value)
    {
        var names = new List<string>(keys);
        var values = new List<string>(keys.Select(_ => "7"));
        if (marker.Length > 0)
        {
            names.Add(marker);
            values.Add("1");
        }

        if (column is not null && !names.Contains(column, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(column);
            values.Add(value);
        }

        string sql = $"INSERT INTO `{table}` ({string.Join(",", names.Select(n => "`" + n + "`"))}) VALUES ({string.Join(",", values.Select(v => "'" + v + "'"))});";
        var json = new JsonSerializerOptions { IncludeFields = true };
        if (table.StartsWith("creature", StringComparison.Ordinal) && !table.EndsWith("_loot_template", StringComparison.Ordinal))
        {
            var creature = new CreatureDumpImporter();
            creature.Read(new StringReader(sql));
            var snapshot = creature.Snapshot();
            var loot = new GameObjectLootDumpImporter();
            loot.Read(new StringReader(sql));
            return JsonSerializer.Serialize(new object[] { snapshot.Templates, snapshot.Spawns, snapshot.Movement, snapshot.Models, snapshot.Addons, loot.BuildReport() }, json);
        }

        var importer = new GameObjectLootDumpImporter();
        importer.Read(new StringReader(sql));
        return JsonSerializer.Serialize(new object[] { importer.SpawnRows, importer.LootRows }, json);
    }
}
