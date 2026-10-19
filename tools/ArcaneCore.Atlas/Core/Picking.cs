using System.Text.Json.Nodes;

namespace ArcaneCore.Atlas.Core;

/// <summary>A ray in world coordinates for <c>query.pick</c>; <paramref name="Direction"/> need not be normalized.</summary>
public readonly record struct PickRay(uint MapId, WorldPoint Origin, WorldPoint Direction, double MaxDistance);

/// <summary>The nearest primitive a pick ray met: its layer, tile, chunk attribute and the attribute's description.</summary>
public sealed record PickHit(string LayerId, TileKey Tile, double Distance, WorldPoint Point, string Primitive, uint Attribute, JsonObject Details)
{
    public JsonObject ToJson() => new()
    {
        ["layer"] = LayerId,
        ["tile"] = new JsonArray(Tile.MapId, Tile.X, Tile.Y),
        ["distance"] = Distance,
        ["point"] = new JsonArray(Point.X, Point.Y, Point.Z),
        ["primitive"] = Primitive,
        ["attribute"] = Attribute,
        ["details"] = Details.DeepClone(),
    };
}
