namespace ArcaneCore.Kernel.WorldData.Creatures;

/// <summary>Optional build-5875 CreatureDisplayInfo/CreatureModelData metadata.</summary>
public sealed record CreatureDisplayModelMetadata(
    uint DisplayId,
    uint ModelId,
    float DisplayScale,
    float ModelScale,
    float CollisionHeight,
    bool HasModelData = true)
{
    public float NativeScale => DisplayScale > 0 && ModelScale > 0 ? DisplayScale * ModelScale : 1.0f;
}

public sealed class CreatureDisplayModelMetadataContent(IEnumerable<CreatureDisplayModelMetadata> rows, string provenance = "synthetic")
{
    private readonly IReadOnlyDictionary<uint, CreatureDisplayModelMetadata> _rows =
        rows.Where(r => r.DisplayId != 0).ToDictionary(r => r.DisplayId);

    public static CreatureDisplayModelMetadataContent Empty { get; } = new([]);

    public string Provenance { get; } = provenance;

    public CreatureDisplayModelMetadata? Find(uint displayId) => _rows.GetValueOrDefault(displayId);
}
