using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Dbc;

namespace ArcaneCore.Atlas.Insight;

/// <summary>
/// Spell id to English name from the client's Spell.dbc (build 5875: 173 fields, ID in field 0, enUS name in field 120). A table with another
/// field count is refused rather than read with guessed columns. Immutable; any thread.
/// </summary>
internal sealed class SpellNames
{
    /// <summary>In-archive path of the spell table.</summary>
    public const string TablePath = "DBFilesClient\\Spell.dbc";

    /// <summary>Cells per Spell.dbc row in build 5875.</summary>
    // ref: src/ArcaneCore.Data/Content/Spells/SpellDbcImporter.cs:14 (SpellFieldCount = 173), :217 (SpellName = field 120)
    public const int FieldCount = 173;

    private const int IdField = 0;
    private const int NameField = 120;

    private readonly Dictionary<uint, string> _names;

    private SpellNames(Dictionary<uint, string> names)
    {
        _names = names;
    }

    /// <summary>Spells that have a name.</summary>
    public int Count => _names.Count;

    /// <summary>Reads a Spell.dbc image.</summary>
    /// <exception cref="InvalidDataException">The image is not a WDBC or does not have the 1.12.1 layout.</exception>
    public static SpellNames FromBytes(byte[] dbc)
    {
        WdbcFile table = WdbcFile.Parse(dbc);
        if (table.FieldCount != FieldCount)
        {
            throw new InvalidDataException($"{TablePath} has {table.FieldCount} fields; build 5875 has {FieldCount}, so the spell names are not read.");
        }

        var names = new Dictionary<uint, string>(table.RecordCount);
        for (int r = 0; r < table.RecordCount; r++)
        {
            string name = table.GetString(r, NameField);
            if (name.Length > 0)
            {
                names[table.GetUInt32(r, IdField)] = name;
            }
        }

        return new SpellNames(names);
    }

    /// <summary>The names from <paramref name="files"/>, or null when the client has no Spell.dbc.</summary>
    /// <exception cref="InvalidDataException">The table is damaged or has another layout.</exception>
    public static SpellNames? TryLoad(IClientFileSource files)
    {
        ArgumentNullException.ThrowIfNull(files);
        byte[]? bytes = files.Read(TablePath);
        return bytes is null ? null : FromBytes(bytes);
    }

    /// <summary>The name of <paramref name="spellId"/>, or null when the table has none.</summary>
    public string? Name(uint spellId) => _names.GetValueOrDefault(spellId);
}
