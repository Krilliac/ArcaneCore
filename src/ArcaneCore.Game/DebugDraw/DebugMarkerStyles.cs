using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.WorldData.GameObjects;

namespace ArcaneCore.Game.DebugDraw;

/// <summary>What a debug marker shows; each kind has its own model, colour, scale and hover name (<see cref="DebugMarkerStyles"/>).</summary>
public enum DebugMarkerKind : byte
{
    Generic = 0,

    /// <summary>A cell corner of the map grid (33.33-yard cells).</summary>
    Cell = 1,

    /// <summary>A cell corner that is also a grid corner (533.33-yard grids).</summary>
    GridCorner = 2,

    /// <summary>Line of sight: a clear segment.</summary>
    LosClear = 3,

    /// <summary>Line of sight: the segment up to the first model hit.</summary>
    LosBlocked = 4,

    /// <summary>Line of sight: the hidden part of a blocked line, beyond the hit.</summary>
    LosOccluded = 5,

    /// <summary>The impact point of a blocked line or a collision ray.</summary>
    HitPoint = 6,

    /// <summary>A corner of a complete path.</summary>
    PathCorner = 7,

    /// <summary>A corner of an incomplete, missing or straight-line (no navmesh) path.</summary>
    PathCornerBad = 8,

    /// <summary>The dots between path corners.</summary>
    PathFill = 9,

    /// <summary>A creature waypoint node (<c>creature_movement</c> / <c>creature_movement_template</c>).</summary>
    Waypoint = 10,

    /// <summary>The dots between waypoint nodes.</summary>
    WaypointFill = 11,

    /// <summary>A forward collision ray.</summary>
    Collision = 12,

    /// <summary>A floor height sample.</summary>
    Height = 13,

    /// <summary>A point on a range circle (visibility distance).</summary>
    Range = 14,

    /// <summary>A creature's spawn (home) point.</summary>
    Spawn = 15,
}

/// <summary>
/// How one <see cref="DebugMarkerKind"/> looks. <paramref name="DisplayId"/> and <paramref name="GlowDisplayId"/> are
/// GameObjectDisplayInfo.dbc ids of the 1.12.1 client (model paths in the comments of <see cref="DebugMarkerStyles"/>, read from the
/// 5875 DBC); <paramref name="GlowDisplayId"/> 0 means no glow companion.
/// </summary>
public sealed record DebugMarkerStyle(DebugMarkerKind Kind, string Name, uint DisplayId, uint GlowDisplayId, float Scale)
{
    /// <summary>The reserved game object entry of this kind (<see cref="DebugMarkerStyles.EntryBase"/> + kind).</summary>
    public uint Entry => DebugMarkerStyles.EntryBase + (uint)Kind;
}

/// <summary>
/// The marker look-up table and the synthetic <c>gameobject_template</c> rows the client asks for. The markers never exist on the
/// server: their create packets go to one client only (<see cref="DebugMarkerPackets"/>), and the entries
/// <see cref="EntryBase"/>..<see cref="EntryBase"/>+255 are reserved so a marker GUID can never equal a real object's (the entry is part of a
/// game object GUID). The templates are goobers (type 10) because the 1.12 client only shows the hover name of an interactive object; a
/// goober without data does nothing when used, and the server answers a use of a marker with its label instead.
/// <para>
/// The models start from the fork's palette (Krilliac/server-Zero feature/debug-visualizers, DebugVis.cpp ColorDisplayId / GlowDisplayId),
/// with path corners moved to blue so a path does not look like a clear line of sight, and the occluded part of a line, waypoints, ranges
/// and spawns added.
/// </para>
/// </summary>
public static class DebugMarkerStyles
{
    /// <summary>First reserved entry (0xFFFF00): above every 1.12 content entry and inside the 24 entry bits of a GUID.</summary>
    public const uint EntryBase = 0x00FFFF00;

    /// <summary>Number of reserved entries.</summary>
    public const uint EntryCount = 0x100;

    private static readonly DebugMarkerStyle[] Styles =
    [
        new(DebugMarkerKind.Generic, "DebugDraw: marker", 5811, 0, 1.0f),                 // World\Goober\G_JewelBlack.mdx
        new(DebugMarkerKind.Cell, "DebugDraw: cell corner", 5912, 0, 1.0f),               // ...\PVP\CTFflags\AllianceCTFflag.mdx
        new(DebugMarkerKind.GridCorner, "DebugDraw: grid corner", 5913, 0, 1.5f),         // ...\PVP\CTFflags\HordeCTFflag.mdx
        new(DebugMarkerKind.LosClear, "DebugDraw: line of sight clear", 2972, 3993, 0.5f), // UngoroCrystal_Green01 / AuraGreenShort
        new(DebugMarkerKind.LosBlocked, "DebugDraw: line of sight blocked", 2973, 1308, 0.5f), // UngoroCrystal_Red01 / AuraRedShort
        new(DebugMarkerKind.LosOccluded, "DebugDraw: hidden beyond the hit", 327, 0, 0.6f), // World\Goober\G_JewelRed.mdx
        new(DebugMarkerKind.HitPoint, "DebugDraw: hit point", 5746, 6430, 0.6f),          // CorruptedCrystalShard / Carni_CannonTarget
        new(DebugMarkerKind.PathCorner, "DebugDraw: path corner", 2971, 263, 0.6f),       // UngoroCrystal_Blue01 / AuraBlueShort
        new(DebugMarkerKind.PathCornerBad, "DebugDraw: path corner (incomplete)", 2973, 1308, 0.6f), // UngoroCrystal_Red01 / AuraRedShort
        new(DebugMarkerKind.PathFill, "DebugDraw: path", 2770, 0, 0.6f),                  // World\Goober\G_JewelBlue.mdx
        new(DebugMarkerKind.Waypoint, "DebugDraw: waypoint", 2974, 1268, 0.6f),           // UngoroCrystal_Yellow01 / AuraYellowShort
        new(DebugMarkerKind.WaypointFill, "DebugDraw: waypoint path", 5811, 0, 0.6f),     // World\Goober\G_JewelBlack.mdx
        new(DebugMarkerKind.Collision, "DebugDraw: collision ray", 1667, 0, 0.4f),        // ...\Silithus\...\FloatingPurpleCrystal01.mdx
        new(DebugMarkerKind.Height, "DebugDraw: floor height", 2974, 266, 0.6f),          // UngoroCrystal_Yellow01 / AuraYellowVeryTall
        new(DebugMarkerKind.Range, "DebugDraw: range", 5811, 0, 1.0f),                    // World\Goober\G_JewelBlack.mdx
        new(DebugMarkerKind.Spawn, "DebugDraw: spawn point", 6431, 363, 0.6f),            // ...\Silithus\...\GlyphedCrystal / AuraPurpleShort
    ];

    /// <summary>Every style, indexed by kind.</summary>
    public static IReadOnlyList<DebugMarkerStyle> All => Styles;

    /// <summary>The style of <paramref name="kind"/>.</summary>
    public static DebugMarkerStyle Of(DebugMarkerKind kind) => (int)kind < Styles.Length ? Styles[(int)kind] : Styles[0];

    /// <summary>Whether <paramref name="entry"/> is in the reserved marker range.</summary>
    public static bool IsMarkerEntry(uint entry) => entry >= EntryBase && entry < EntryBase + EntryCount;

    /// <summary>Whether <paramref name="guid"/> names a debug marker (a game object GUID carrying a reserved entry).</summary>
    public static bool IsMarkerGuid(ObjectGuid guid) => guid.High == HighGuid.GameObject && IsMarkerEntry(guid.Entry);

    /// <summary>
    /// The template the client is told about for a marker entry (CMSG_GAMEOBJECT_QUERY), or null for an entry outside the range or a
    /// reserved entry no kind uses. Safe on any thread (immutable).
    /// </summary>
    public static GameObjectTemplate? FindTemplate(uint entry)
    {
        if (!IsMarkerEntry(entry) || entry - EntryBase >= Styles.Length)
        {
            return null;
        }

        return Templates[(int)(entry - EntryBase)];
    }

    private static readonly GameObjectTemplate[] Templates = [.. Styles.Select(s => new GameObjectTemplate
    {
        Entry = s.Entry,
        Type = (uint)GameObjectType.Goober,
        DisplayId = s.DisplayId,
        Name = s.Name,
        Size = s.Scale,
    })];
}
