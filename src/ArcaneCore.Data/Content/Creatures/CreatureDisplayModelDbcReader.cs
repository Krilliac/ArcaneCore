using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using System.Security.Cryptography;

namespace ArcaneCore.Data.Content.Creatures;

/// <summary>Strict optional build-5875 CreatureDisplayInfo/CreatureModelData readers.</summary>
public static class CreatureDisplayModelDbcReader
{
    public const int DisplayInfoFieldCount = 12;
    public const int ModelDataFieldCount = 16;
    public const int MaxFileBytes = 64 * 1024 * 1024;
    public const int MaxRecords = 1_000_000;

    public static CreatureDisplayModelMetadataContent Load(string displayInfoPath, string modelDataPath)
    {
        var displays = LoadImage(displayInfoPath);
        var models = LoadImage(modelDataPath);
        return Read(displays.File, models.File,
            $"CreatureDisplayInfo={displays.Hash};CreatureModelData={models.Hash}");
    }

    public static CreatureDisplayModelMetadataContent Read(DbcFile displayInfo, DbcFile modelData, string provenance = "synthetic")
    {
        ArgumentNullException.ThrowIfNull(displayInfo);
        ArgumentNullException.ThrowIfNull(modelData);
        Require(displayInfo, DisplayInfoFieldCount, "CreatureDisplayInfo.dbc");
        Require(modelData, ModelDataFieldCount, "CreatureModelData.dbc");

        var models = new Dictionary<uint, (float Scale, float Height)>();
        for (int row = 0; row < modelData.RecordCount; row++)
        {
            uint id = modelData.GetUInt32(row, 0);
            if (id == 0) continue;
            float scale = modelData.GetFloat(row, 4);
            float height = modelData.GetFloat(row, 15);
            // vmangos/mangos-classic CheckValidScale treats a finite non-positive
            // scale as the runtime default. Preserve zero here so the consumer
            // can still take its model-data collision-height fallback path.
            if (!float.IsFinite(scale) || scale < 0) throw new InvalidDataException("CreatureModelData.dbc has an invalid model scale");
            if (!float.IsFinite(height) || height < 0) throw new InvalidDataException("CreatureModelData.dbc has an invalid collision height");
            if (!models.TryAdd(id, (scale, height))) throw new InvalidDataException("duplicate creature model id");
        }

        var result = new List<CreatureDisplayModelMetadata>(displayInfo.RecordCount);
        var displayIds = new HashSet<uint>();
        for (int row = 0; row < displayInfo.RecordCount; row++)
        {
            uint display = displayInfo.GetUInt32(row, 0);
            uint model = displayInfo.GetUInt32(row, 1);
            if (display == 0) continue;
            bool hasModel = models.TryGetValue(model, out var modelDataRow);
            if (!hasModel) modelDataRow = (1.0f, 0.0f);
            float displayScale = displayInfo.GetFloat(row, 4);
            // Zero is a client placeholder and is normalized by NativeScale;
            // negative and non-finite values are malformed source data.
            if (!float.IsFinite(displayScale) || displayScale < 0) throw new InvalidDataException("CreatureDisplayInfo.dbc has an invalid display scale");
            if (!float.IsFinite(displayScale * modelDataRow.Scale)) throw new InvalidDataException("creature native scale overflows");
            if (!displayIds.Add(display)) throw new InvalidDataException("duplicate creature display id");
            result.Add(new CreatureDisplayModelMetadata(display, model, displayScale, modelDataRow.Scale, modelDataRow.Height, hasModel));
        }

        return new CreatureDisplayModelMetadataContent(result, provenance);
    }

    private static void Require(DbcFile file, int fields, string name)
    {
        if (file.FieldCount != fields || file.RecordSize != fields * 4)
            throw new InvalidDataException($"{name} requires {fields} four-byte fields, found {file.FieldCount}/{file.RecordSize}");
        if (file.RecordCount > MaxRecords) throw new InvalidDataException($"{name} exceeds the record limit");
    }

    private static (DbcFile File, string Hash) LoadImage(string path)
    {
        using FileStream stream = File.OpenRead(path);
        if (stream.Length > MaxFileBytes) throw new InvalidDataException("creature metadata file exceeds the size limit");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return (DbcFile.Parse(bytes), Convert.ToHexString(SHA256.HashData(bytes)));
    }
}
