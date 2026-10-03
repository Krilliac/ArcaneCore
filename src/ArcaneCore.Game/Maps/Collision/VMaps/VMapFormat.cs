using System.Numerics;

namespace ArcaneCore.Game.Maps.Collision.VMaps;

/// <summary>
/// Constants and conventions of the extracted vmap format (cMaNGOS / vmangos "VMAP_7.0",
/// written by their <c>vmap_assembler</c>). Implemented from the format description; see
/// docs/integration/vmap-los.md for the layout.
/// </summary>
public static class VMapFormat
{
    /// <summary>File magic of every final vmap file (vmtree, vmtile, vmo).</summary>
    public const string Magic = "VMAP_7.0";

    /// <summary><c>MOD_M2</c>: a doodad model (does not break line of sight, no area info).</summary>
    public const uint ModM2 = 1;

    /// <summary><c>MOD_WORLDSPAWN</c>: a WMO placed by the map's WDT (instances).</summary>
    public const uint ModWorldSpawn = 1 << 1;

    /// <summary><c>MOD_HAS_BOUND</c>: the spawn record carries its world bound.</summary>
    public const uint ModHasBound = 1 << 2;

    /// <summary>Longest model name a spawn record may carry (vmangos rejects longer as corrupt).</summary>
    public const int MaxNameLength = 500;

    /// <summary>Half the map's extent: vmap internal X/Y = this minus world X/Y (vmangos <c>convertPositionToInternalRep</c>).</summary>
    public const float MapMid = 0.5f * 64.0f * 533.33333333f;

    /// <summary>World position → vmap internal position (a half turn about Z around the map centre; Z unchanged).</summary>
    public static Vector3 ToInternal(Vector3 world) => new(MapMid - world.X, MapMid - world.Y, world.Z);

    /// <summary>vmap internal position → world position (the same half turn).</summary>
    public static Vector3 ToWorld(Vector3 internalPosition) => ToInternal(internalPosition);

    /// <summary><c>NNN.vmtree</c>.</summary>
    public static string TreeFileName(uint mapId) => $"{mapId:D3}.vmtree";

    /// <summary>
    /// The tile file for terrain tile (<paramref name="tileX"/>, <paramref name="tileY"/>)
    /// (<see cref="Terrain.TerrainTile.TileOf(float, float)"/>: X from world X). vmangos names it
    /// from its own grid pair, whose first index comes from world Y: <c>NNN_YY_XX.vmtile</c>.
    /// </summary>
    public static string TileFileName(uint mapId, int tileX, int tileY) => $"{mapId:D3}_{tileY:D2}_{tileX:D2}.vmtile";

    /// <summary>A model file: the spawn's name plus <c>.vmo</c>.</summary>
    public static string ModelFileName(string name) => name + ".vmo";

    /// <summary>
    /// Whether a spawn's model name is a plain file name (no directory parts). Names come from the
    /// data files, so anything that could walk out of the vmap directory is refused.
    /// </summary>
    public static bool IsSafeModelName(string name)
        => !string.IsNullOrEmpty(name) && name.IndexOfAny(['/', '\\', ':', '\0']) < 0 && name != "." && name != ".." && !name.StartsWith("..", StringComparison.Ordinal);
}

/// <summary>A model placement record (vmangos <c>ModelSpawn</c>); positions and bounds are vmap internal coordinates.</summary>
public sealed record ModelSpawn(uint Flags, ushort AdtId, uint Id, Vector3 Position, Vector3 Rotation, float Scale, Vector3 BoundLow, Vector3 BoundHigh, string Name)
{
    public bool IsM2 => (Flags & VMapFormat.ModM2) != 0;

    public bool HasBound => (Flags & VMapFormat.ModHasBound) != 0;

    /// <summary>Spawn record: u32 flags, u16 adt id, u32 id, f32[3] position, f32[3] rotation (degrees), f32 scale, [f32[3] bound low, f32[3] bound high when MOD_HAS_BOUND], u32 name length, name bytes.</summary>
    internal static ModelSpawn Read(ref CollisionDataReader reader)
    {
        uint flags = reader.ReadUInt32();
        ushort adtId = reader.ReadUInt16();
        uint id = reader.ReadUInt32();
        Vector3 position = reader.ReadVector3();
        Vector3 rotation = reader.ReadVector3();
        float scale = reader.ReadSingle();
        Vector3 low = Vector3.Zero;
        Vector3 high = Vector3.Zero;
        if ((flags & VMapFormat.ModHasBound) != 0)
        {
            low = reader.ReadVector3();
            high = reader.ReadVector3();
        }

        uint nameLength = reader.ReadUInt32();
        if (nameLength > VMapFormat.MaxNameLength)
        {
            throw new InvalidDataException($"model spawn {id}: name length {nameLength} exceeds {VMapFormat.MaxNameLength}");
        }

        string name = System.Text.Encoding.ASCII.GetString(reader.ReadBytes((int)nameLength));
        if (!float.IsFinite(scale) || scale <= 0 || !IsFinite(position) || !IsFinite(rotation) || !IsFinite(low) || !IsFinite(high))
        {
            throw new InvalidDataException($"model spawn {id}: non-finite or non-positive transform");
        }

        return new ModelSpawn(flags, adtId, id, position, rotation, scale, low, high, name);
    }

    internal void Write(BinaryWriter writer)
    {
        writer.Write(Flags);
        writer.Write(AdtId);
        writer.Write(Id);
        WriteVector(writer, Position);
        WriteVector(writer, Rotation);
        writer.Write(Scale);
        if (HasBound)
        {
            WriteVector(writer, BoundLow);
            WriteVector(writer, BoundHigh);
        }

        byte[] name = System.Text.Encoding.ASCII.GetBytes(Name);
        writer.Write((uint)name.Length);
        writer.Write(name);
    }

    internal static void WriteVector(BinaryWriter writer, Vector3 v)
    {
        writer.Write(v.X);
        writer.Write(v.Y);
        writer.Write(v.Z);
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
