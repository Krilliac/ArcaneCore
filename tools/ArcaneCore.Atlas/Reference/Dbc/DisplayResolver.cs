using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Reference.Dbc;

/// <summary>
/// The CreatureDisplayInfoExtra row of a humanoid creature display (columns measured on build 5875: [1] race, [2] sex, [3] skin,
/// [4] face, [5] hair style, [6] hair colour, [7] facial hair, [8..17] the ten NPC equipment slots as ItemDisplayInfo ids, [18] bake name).
/// </summary>
/// <param name="Equipment">ItemDisplayInfo ids in column order: head, shoulder, shirt, chest, belt, legs, boots, wrist, gloves, tabard (0 = none).</param>
/// <param name="BakedTexture">In-archive path of the pre-baked body texture (<c>Textures\BakedNpcTextures\&lt;bake name&gt;</c>), or null.</param>
public sealed record DisplayExtra(
    int Race,
    int Sex,
    int Skin,
    int Face,
    int HairStyle,
    int HairColor,
    int FacialHair,
    IReadOnlyList<int> Equipment,
    string? BakedTexture);

/// <summary>
/// An ItemDisplayInfo row (23 fields on build 5875, confirmed against the real client's 29604 rows: [1] and [2] model names, [3] and [4]
/// their texture names, [5] icon, [6..8] geoset groups (plain integers, 0..5, never string offsets: the later-build ground model string
/// is not in this layout), [9] flags, [10] spell visual, [11] group sound, [12] and [13] helmet geoset visibility ids (245..368),
/// [14..21] the eight armour region textures, [22] item visual).
/// </summary>
/// <param name="Models">Up to two models as in-archive paths (<c>.m2</c>) found under <c>Item\ObjectComponents</c>; null where the row names none or no folder holds it.</param>
/// <param name="ModelTextures">The texture of each model as an in-archive path next to it; null where there is none.</param>
/// <param name="Icon">The inventory icon name.</param>
/// <param name="GeosetGroups">The three geoset group values.</param>
/// <param name="HelmetGeosetVis">The two helmet geoset visibility ids.</param>
/// <param name="RegionTextures">Texture names of the arm upper, arm lower, hand, torso upper, torso lower, leg upper, leg lower and foot regions; null where none.</param>
public sealed record ItemDisplay(
    IReadOnlyList<string?> Models,
    IReadOnlyList<string?> ModelTextures,
    string? Icon,
    IReadOnlyList<int> GeosetGroups,
    IReadOnlyList<int> HelmetGeosetVis,
    IReadOnlyList<string?> RegionTextures);

/// <summary>A ChrRaces row resolved for one gender.</summary>
/// <param name="RaceId">The ChrRaces id.</param>
/// <param name="Gender">0 male, 1 female.</param>
/// <param name="ClientFile">The folder name under <c>Character</c> (column [15]).</param>
/// <param name="MaleDisplayId">CreatureDisplayInfo id of the male model (column [4]).</param>
/// <param name="FemaleDisplayId">CreatureDisplayInfo id of the female model (column [5]).</param>
public sealed record RaceDisplay(int RaceId, int Gender, string ClientFile, uint MaleDisplayId, uint FemaleDisplayId);

/// <summary>What a display id resolves to. A break in the chain is a <see cref="Problems"/> entry, never an exception.</summary>
/// <param name="ModelPath">In-archive model path with an <c>.m2</c> extension, or null when no row led to a model.</param>
/// <param name="ModelDataId">The CreatureModelData id (creatures only).</param>
/// <param name="Textures">Up to three skin textures as in-archive paths; null where the row names none.</param>
/// <param name="Problems"><c>no-row</c>, <c>model-missing</c>, <c>texture-missing</c> or <c>extra-missing</c>.</param>
public sealed record DisplayResolution(string? ModelPath, int? ModelDataId, IReadOnlyList<string?> Textures, IReadOnlyList<string> Problems)
{
    /// <summary>Creature: the display's scale times the model data's scale (a factor of 0 or less counts as 1). Null for other kinds.</summary>
    public double? Scale { get; init; }

    /// <summary>Creature: CreatureDisplayInfo scale (column [4]) as stored.</summary>
    public double? DisplayScale { get; init; }

    /// <summary>Creature: CreatureModelData scale (column [4]) as stored.</summary>
    public double? ModelScale { get; init; }

    /// <summary>Creature with a CreatureDisplayInfoExtra row (humanoid NPCs).</summary>
    public DisplayExtra? Extra { get; init; }

    /// <summary>Item display.</summary>
    public ItemDisplay? Item { get; init; }

    /// <summary>Race display.</summary>
    public RaceDisplay? Race { get; init; }
}

/// <summary>
/// Display id to model path and textures (design section 6). Creature: CreatureDisplayInfo ([1] model data id, [3] extra id, [4] scale,
/// [6..8] skin names) to CreatureModelData ([2] model path, [4] scale) and CreatureDisplayInfoExtra. Game object: GameObjectDisplayInfo
/// [1] is the model name. Item: ItemDisplayInfo. Race: ChrRaces ([4]/[5] display ids, [15] client file folder) and a gender.
/// Skin names resolve to the model's directory plus the name plus ".blp". Thread-safe (read-only).
/// </summary>
public sealed class DisplayResolver(DisplayTables tables, IClientFileSource files)
{
    /// <summary>The kinds <see cref="Resolve"/> accepts.</summary>
    public static IReadOnlyList<string> Kinds { get; } = ["creature", "gameobject", "item", "race"];

    private const int SkinFirstField = 6;
    private const int SkinCount = 3;
    private const string BakedNpcTextures = "Textures\\BakedNpcTextures\\";
    private const string ItemObjectComponents = "Item\\ObjectComponents\\";

    // Folders under Item\ObjectComponents that hold item models; the display row does not say which one applies, so the first
    // folder that has the model wins.
    private static readonly string[] ItemModelFolders = ["Weapon", "Shield", "Head", "Shoulder", "Ammo", "Quiver", "Pouch", "Cape"];

    /// <summary>Resolves <paramref name="id"/> of <paramref name="kind"/> (one of <see cref="Kinds"/>); <paramref name="gender"/> (0 male, 1 female) applies to races.</summary>
    /// <exception cref="ArgumentException">The kind is not one of <see cref="Kinds"/>.</exception>
    public DisplayResolution Resolve(string kind, long id, int gender = 0)
        => kind switch
        {
            "creature" => ResolveCreature(id),
            "gameobject" => ResolveGameObject(id),
            "item" => ResolveItem(id),
            "race" => ResolveRace(id, gender),
            _ => throw new ArgumentException($"Unknown display kind '{kind}'.", nameof(kind)),
        };

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

        WdbcFile data = tables.Table("CreatureModelData");
        string? model = ToM2(data.GetString(dataRow, 2));
        var problems = new List<string>();
        CheckModel(model, problems);
        string[] names = new string[SkinCount];
        for (int i = 0; i < SkinCount; i++)
        {
            names[i] = display.GetString(row, SkinFirstField + i);
        }

        string?[] skins = SkinPaths(model, names, problems);
        double displayScale = Float(display, row, 4);
        double modelScale = Float(data, dataRow, 4);
        DisplayExtra? extra = ReadExtra(display.GetUInt32(row, 3), problems);
        return new DisplayResolution(model, (int)modelDataId, skins, [.. problems.Distinct()])
        {
            Scale = Factor(displayScale) * Factor(modelScale),
            DisplayScale = displayScale,
            ModelScale = modelScale,
            Extra = extra,
        };
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

    public DisplayResolution ResolveItem(long displayId)
    {
        if (displayId is < 0 or > uint.MaxValue || !tables.Has("ItemDisplayInfo"))
        {
            return NoRow();
        }

        int row = tables.FindRow("ItemDisplayInfo", (uint)displayId);
        if (row < 0)
        {
            return NoRow();
        }

        WdbcFile table = tables.Table("ItemDisplayInfo");
        var problems = new List<string>();
        string?[] models = new string?[2];
        string?[] modelTextures = new string?[2];
        for (int i = 0; i < 2; i++)
        {
            string modelName = ToM2(table.GetString(row, 1 + i)) ?? "";
            if (modelName.Length == 0)
            {
                continue;
            }

            string? folder = ItemModelFolders.Select(f => $"{ItemObjectComponents}{f}\\").FirstOrDefault(f => files.Exists(f + modelName));
            if (folder is null)
            {
                problems.Add("model-missing");
                continue;
            }

            models[i] = folder + modelName;
            string textureName = table.GetString(row, 3 + i);
            if (textureName.Length > 0)
            {
                string texture = folder + (textureName.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? textureName : textureName + ".blp");
                modelTextures[i] = texture;
                if (!files.Exists(texture))
                {
                    problems.Add("texture-missing");
                }
            }
        }

        string?[] regions = new string?[8];
        for (int i = 0; i < regions.Length; i++)
        {
            string name = table.GetString(row, 14 + i);
            regions[i] = name.Length == 0 ? null : name;
        }

        string icon = table.GetString(row, 5);
        var item = new ItemDisplay(
            models,
            modelTextures,
            icon.Length == 0 ? null : icon,
            [Signed(table, row, 6), Signed(table, row, 7), Signed(table, row, 8)],
            [Signed(table, row, 12), Signed(table, row, 13)],
            regions);
        return new DisplayResolution(models[0] ?? models[1], null, new string?[SkinCount], [.. problems.Distinct()]) { Item = item };
    }

    public DisplayResolution ResolveRace(long raceId, int gender)
    {
        if (raceId is < 0 or > int.MaxValue || gender is < 0 or > 1 || !tables.Has("ChrRaces"))
        {
            return NoRow();
        }

        int row = tables.FindRow("ChrRaces", (uint)raceId);
        if (row < 0)
        {
            return NoRow();
        }

        WdbcFile races = tables.Table("ChrRaces");
        string clientFile = races.GetString(row, 15);
        var problems = new List<string>();
        string? model = null;
        if (clientFile.Length > 0)
        {
            string sex = gender == 0 ? "Male" : "Female";
            model = $"Character\\{clientFile}\\{sex}\\{clientFile}{sex}.m2";
        }

        CheckModel(model, problems);
        var race = new RaceDisplay((int)raceId, gender, clientFile, races.GetUInt32(row, 4), races.GetUInt32(row, 5));
        return new DisplayResolution(model, null, new string?[SkinCount], problems) { Race = race };
    }

    private static DisplayResolution NoRow() => new(null, null, new string?[SkinCount], ["no-row"]);

    private DisplayExtra? ReadExtra(uint extraId, List<string> problems)
    {
        if (extraId == 0 || !tables.Has("CreatureDisplayInfoExtra"))
        {
            return null;
        }

        int row = tables.FindRow("CreatureDisplayInfoExtra", extraId);
        if (row < 0)
        {
            problems.Add("extra-missing");
            return null;
        }

        WdbcFile extra = tables.Table("CreatureDisplayInfoExtra");
        string bake = extra.GetString(row, 18);
        string? baked = null;
        if (bake.Length > 0)
        {
            baked = BakedNpcTextures + bake;
            if (!files.Exists(baked))
            {
                problems.Add("texture-missing");
            }
        }

        var equipment = new int[10];
        for (int i = 0; i < equipment.Length; i++)
        {
            equipment[i] = Signed(extra, row, 8 + i);
        }

        return new DisplayExtra(
            Signed(extra, row, 1),
            Signed(extra, row, 2),
            Signed(extra, row, 3),
            Signed(extra, row, 4),
            Signed(extra, row, 5),
            Signed(extra, row, 6),
            Signed(extra, row, 7),
            equipment,
            baked);
    }

    private static int Signed(WdbcFile table, int row, int field) => unchecked((int)table.GetUInt32(row, field));

    private static double Float(WdbcFile table, int row, int field)
    {
        float value = BitConverter.UInt32BitsToSingle(table.GetUInt32(row, field));
        return float.IsFinite(value) ? value : 0;
    }

    private static double Factor(double scale) => scale > 0 ? scale : 1;

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
