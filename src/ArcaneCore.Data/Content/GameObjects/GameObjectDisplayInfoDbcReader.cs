using ArcaneCore.Data.Content.Spells;

namespace ArcaneCore.Data.Content.GameObjects;

/// <summary>One GameObjectDisplayInfo.dbc row: a game object display id and the model the client loads for it.</summary>
/// <param name="Id">The display id (field 0), what <c>gameobject_template.displayId</c> and GAMEOBJECT_DISPLAYID name.</param>
/// <param name="ModelName">m_modelName (field 1), e.g. <c>World\Goober\G_JewelBlack.mdx</c> (the client loads the .m2 of that name).</param>
public sealed record GameObjectDisplayInfoEntry(uint Id, string ModelName);

/// <summary>
/// Reads the developer's build-5875 GameObjectDisplayInfo.dbc (no client data is shipped): 12 four-byte fields (vmangos and
/// mangoszero DBCfmt.h GameObjectDisplayInfofmt "nsxxxxxxxxxx": id 0, model name 1, ten sound ids 2-11). Another layout, a
/// zero or repeated id is refused.
/// </summary>
public static class GameObjectDisplayInfoDbcReader
{
    public const string FileName = "GameObjectDisplayInfo.dbc";

    public const int FieldCount = 12;

    public static DbcTable<GameObjectDisplayInfoEntry> Load(string path) => Read(DbcFile.Load(path));

    public static DbcTable<GameObjectDisplayInfoEntry> Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FieldCount != FieldCount)
        {
            throw new InvalidDataException($"{FileName} of build 5875 has {FieldCount} fields, this file has {file.FieldCount}");
        }

        var rows = new List<GameObjectDisplayInfoEntry>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            rows.Add(new GameObjectDisplayInfoEntry(file.GetUInt32(row, 0), file.GetString(row, 1)));
        }

        return new DbcTable<GameObjectDisplayInfoEntry>(FileName, rows, r => r.Id);
    }
}
