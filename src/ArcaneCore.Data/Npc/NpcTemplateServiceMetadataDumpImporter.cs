using System.Globalization;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.Npc;

namespace ArcaneCore.Data.Npc;

public sealed record NpcTemplateServiceMetadataImportReport(
    int Rows, int Replaced, int Skipped, IReadOnlyList<string> Diagnostics);

/// <summary>Imports direct service fields from creature_template without flattening vendor/trainer tables.</summary>
public sealed class NpcTemplateServiceMetadataDumpImporter
{
    private static readonly HashSet<uint> PlayableClasses = [0, 1, 2, 3, 4, 5, 7, 8, 9, 11];
    private readonly Dictionary<uint, NpcTemplateServiceMetadata> _rows = [];
    private readonly List<string> _diagnostics = [];
    private int _replaced;
    private int _skipped;

    public IReadOnlyCollection<NpcTemplateServiceMetadata> Snapshot() => [.. _rows.Values];

    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is DumpRow row && row.Table.Equals("creature_template", StringComparison.OrdinalIgnoreCase))
            {
                ReadRow(row);
            }
        }
    }

    public NpcTemplateServiceMetadataImportReport BuildReport()
        => new(_rows.Count, _replaced, _skipped, [.. _diagnostics]);

    private void ReadRow(DumpRow row)
    {
        string? entry = Value(row, "entry", "Entry");
        if (entry is null)
        {
            _skipped++;
            Skip("creature_template entry <missing>: missing entry");
            return;
        }

        // If neither dialect exposes service metadata, this importer has no work to do.
        if (!Has(row, "gossip_menu_id", "GossipMenuId", "trainer_type", "TrainerType", "trainer_class", "TrainerClass",
            "trainer_race", "TrainerRace", "trainer_spell", "TrainerSpell"))
        {
            return;
        }

        try
        {
            uint id = UInt(entry, "creature_template.entry");
            var metadata = new NpcTemplateServiceMetadata
            {
                Entry = Positive(id, "entry"),
                GossipMenuId = UInt(Value(row, "gossip_menu_id", "GossipMenuId") ?? "0", "gossip_menu_id"),
                TrainerType = Domain(UInt(Value(row, "trainer_type", "TrainerType") ?? "0", "trainer_type"), 3, "trainer_type"),
                TrainerClass = checked((byte)ClassDomain(UInt(Value(row, "trainer_class", "TrainerClass") ?? "0", "trainer_class"))),
                TrainerRace = checked((byte)Domain(UInt(Value(row, "trainer_race", "TrainerRace") ?? "0", "trainer_race"), 8, "trainer_race")),
                TrainerSpell = UInt(Value(row, "trainer_spell", "TrainerSpell") ?? "0", "trainer_spell"),
            };

            if (_rows.ContainsKey(id)) _replaced++;
            _rows[id] = metadata;
        }
        catch (InvalidDataException)
        {
            _skipped++;
            Skip($"creature_template entry {EntryMarker(entry)}: invalid NPC service metadata");
        }
        catch (OverflowException)
        {
            _skipped++;
            Skip($"creature_template entry {EntryMarker(entry)}: NPC service metadata overflow");
        }
    }

    private static bool Has(DumpRow row, params string[] names) => names.Any(name => row.Columns.Any(c => Normalize(c) == Normalize(name)));

    private static string? Value(DumpRow row, params string[] names)
    {
        foreach (string name in names)
        {
            int index = -1;
            for (int i = 0; i < row.Columns.Count; i++)
            {
                if (Normalize(row.Columns[i]) == Normalize(name))
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0 && index < row.Values.Count) return row.Values[index];
        }

        return null;
    }

    private static string Normalize(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static uint UInt(string value, string name)
        => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint result)
            ? result : throw new InvalidDataException($"{name} must be an unsigned integer");

    private static uint Positive(uint value, string name)
        => value == 0 ? throw new InvalidDataException($"{name} must be greater than zero") : value;

    private static uint Domain(uint value, uint max, string name)
        => value > max ? throw new InvalidDataException($"{name} must be in 0..{max}") : value;

    private static uint ClassDomain(uint value)
        => !PlayableClasses.Contains(value) ? throw new InvalidDataException("trainer_class is not a vanilla playable class") : value;

    private static string EntryMarker(string value)
        => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out uint id) ? id.ToString(CultureInfo.InvariantCulture) : "<invalid>";

    private void Skip(string diagnostic)
    {
        if (_diagnostics.Count < 32) _diagnostics.Add(diagnostic);
    }
}
