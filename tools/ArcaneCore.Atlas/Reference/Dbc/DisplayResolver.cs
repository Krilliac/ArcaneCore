using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Dbc;

namespace ArcaneCore.Atlas.Reference.Display;

/// <summary>What a display id resolves to. A break in the chain is a <see cref="Problems"/> entry, never an exception.</summary>
/// <param name="ModelPath">In-archive model path with an <c>.m2</c> extension, or null when no row led to a model.</param>
/// <param name="ModelDataId">The CreatureModelData id (creatures only).</param>
/// <param name="Textures">Up to three skin textures as in-archive paths; null where the row names none.</param>
/// <param name="Problems"><c>no-row</c>, <c>model-missing</c> or <c>texture-missing</c>.</param>
public sealed record DisplayResolution(string? ModelPath, int? ModelDataId, IReadOnlyList<string?> Textures, IReadOnlyList<string> Problems);

/// <summary>
/// Display id to model path and skin textures (design section 2.6). Creature: CreatureDisplayInfo row ([1] model data id,
/// [6..8] skin names) to the CreatureModelData row ([2] model path). Game object: GameObjectDisplayInfo [1] is the model name.
/// Skin names resolve to the model's directory plus the name plus ".blp". Thread-safe (read-only).
/// </summary>
public sealed class DisplayResolver(DisplayTables tables, IClientFileSource files)
{
    private const int SkinFirstField = 6;
    private const int SkinCount = 3;

    public DisplayResolution ResolveCreature(long displayId)
    {
        if (displayId is < 0 or > uint.MaxValue || !tables.Has("CreatureDisplayInfo") || !tables.Has("CreatureModelData"))
        {
            return NoRow();
        }

        int row = tables.FindRow("CreatureDisplayInfo", (uint)displayId);
        if (row < 0)
        {
            return NoRow();
        }

        WdbcFile display = tables.Table("CreatureDisplayInfo");
        uint modelDataId = display.GetUInt32(row, 1);
        int dataRow = tables.FindRow("CreatureModelData", modelDataId);
        if (dataRow < 0 || modelDataId > int.MaxValue)
        {
            return NoRow();
        }

        string? model = ToM2(tables.Table("CreatureModelData").GetString(dataRow, 2));
        var problems = new List<string>();
        CheckModel(model, problems);
        string[] names = new string[SkinCount];
        for (int i = 0; i < SkinCount; i++)
        {
            names[i] = display.GetString(row, SkinFirstField + i);
        }

        return new DisplayResolution(model, (int)modelDataId, SkinPaths(model, names, problems), problems);
    }

    public DisplayResolution ResolveGameObject(long displayId)
    {
        if (displayId is < 0 or > uint.MaxValue || !tables.Has("GameObjectDisplayInfo"))
        {
            return NoRow();
        }

        int row = tables.FindRow("GameObjectDisplayInfo", (uint)displayId);
        if (row < 0)
        {
            return NoRow();
        }

        string? model = ToM2(tables.Table("GameObjectDisplayInfo").GetString(row, 1));
        var problems = new List<string>();
        CheckModel(model, problems);
        return new DisplayResolution(model, null, new string?[SkinCount], problems);
    }

    private static DisplayResolution NoRow() => new(null, null, new string?[SkinCount], ["no-row"]);

    private static string? ToM2(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string path = name.Replace('/', '\\');
        int dot = path.LastIndexOf('.');
        int slash = path.LastIndexOf('\\');
        if (dot > slash && (path.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)))
        {
            return path[..dot] + ".m2";
        }

        return path;
    }

    private void CheckModel(string? model, List<string> problems)
    {
        if (model is null || !files.Exists(model))
        {
            problems.Add("model-missing");
        }
    }

    private string?[] SkinPaths(string? model, string[] names, List<string> problems)
    {
        string directory = "";
        if (model is not null)
        {
            int slash = model.LastIndexOf('\\');
            directory = slash >= 0 ? model[..(slash + 1)] : "";
        }

        var paths = new string?[names.Length];
        bool missing = false;
        for (int i = 0; i < names.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(names[i]))
            {
                continue;
            }

            string name = names[i].Replace('/', '\\');
            paths[i] = directory + (name.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? name : name + ".blp");
            missing |= !files.Exists(paths[i]!);
        }

        if (missing)
        {
            problems.Add("texture-missing");
        }

        return paths;
    }
}
