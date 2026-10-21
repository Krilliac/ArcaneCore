using ArcaneCore.Game.Maps.Terrain;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The facts of one return decision: whether the bot is on its home map, its two-dimensional distance to the centre (NaN off the map),
/// the highest area level on the straight line home (0: none known), the bot's level, and whether its walk home was set aside as blocked.
/// </summary>
internal readonly record struct PlayerbotHomeFacts(bool OnHomeMap, float Distance, int HighestAreaLevel, int BotLevel, bool WalkBlocked);

/// <summary>The return rules of a homed bot, apart from the world so they can be tested alone (docs/areas/playbots.md, Home area).</summary>
internal static class PlayerbotHomeRules
{
    /// <summary>The straight line home is sampled for its area level every this many yards (<see cref="PlayerbotAreaLevelLookup.HighestOnLine"/>).</summary>
    internal const float DangerSampleYards = 20f;

    /// <summary>
    /// Whether to teleport, and why. Walk: never. Teleport: always. Auto: another map, a blocked walk, more than
    /// <see cref="PlayerbotHomeOptions.TeleportDistanceYards"/> away, or an area on the way more than
    /// <see cref="PlayerbotHomeOptions.DangerLevelMargin"/> levels above the bot; otherwise walk. Whether the bot may teleport at all
    /// (alive, out of combat, not grouped, the home map allowed) is decided by the caller.
    /// </summary>
    internal static PlayerbotHomeTeleportReason Decide(in PlayerbotHomeFacts facts, PlayerbotHomeOptions options)
        => Decide(facts, options, options?.TeleportDistanceYards ?? 0f);

    /// <summary>The Auto teleport distance for a home with a walk range (<see cref="PlayerbotHomeArea.WalkRangeYards"/>; 0 = none): the larger of the two.</summary>
    internal static float TeleportDistanceFor(PlayerbotHomeOptions options, float walkRangeYards)
    {
        ArgumentNullException.ThrowIfNull(options);
        return walkRangeYards > options.TeleportDistanceYards ? walkRangeYards : options.TeleportDistanceYards;
    }

    /// <summary><see cref="Decide(in PlayerbotHomeFacts, PlayerbotHomeOptions)"/> with the Auto teleport distance given (<see cref="TeleportDistanceFor"/>).</summary>
    internal static PlayerbotHomeTeleportReason Decide(in PlayerbotHomeFacts facts, PlayerbotHomeOptions options, float teleportDistanceYards)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.ReturnMode switch
        {
            PlayerbotHomeReturnMode.Teleport => facts.OnHomeMap ? PlayerbotHomeTeleportReason.Mode : PlayerbotHomeTeleportReason.OtherMap,
            PlayerbotHomeReturnMode.Auto => !facts.OnHomeMap ? PlayerbotHomeTeleportReason.OtherMap
                : facts.WalkBlocked ? PlayerbotHomeTeleportReason.WalkBlocked
                : !float.IsFinite(facts.Distance) || facts.Distance > teleportDistanceYards ? PlayerbotHomeTeleportReason.Distance
                : IsDangerous(facts.HighestAreaLevel, facts.BotLevel, options.DangerLevelMargin) ? PlayerbotHomeTeleportReason.Danger
                : PlayerbotHomeTeleportReason.None,
            _ => PlayerbotHomeTeleportReason.None,
        };
    }

    /// <summary>An area level (0: not known, never dangerous) more than <paramref name="margin"/> above the bot's level.</summary>
    internal static bool IsDangerous(int areaLevel, int botLevel, int margin) => areaLevel > 0 && areaLevel > botLevel + margin;

    /// <summary>The words a return teleport is logged and inspected with.</summary>
    internal static string Describe(PlayerbotHomeTeleportReason reason) => reason switch
    {
        PlayerbotHomeTeleportReason.Mode => "return mode teleport",
        PlayerbotHomeTeleportReason.OtherMap => "on another map",
        PlayerbotHomeTeleportReason.Distance => "too far to walk",
        PlayerbotHomeTeleportReason.Danger => "way home through a higher-level area",
        PlayerbotHomeTeleportReason.WalkBlocked => "walk home blocked",
        PlayerbotHomeTeleportReason.UnsafeLanding => "revived by the spirit healer among creatures too strong for it",
        PlayerbotHomeTeleportReason.Island => "stalled on ground no route leaves",
        _ => "none",
    };
}

/// <summary>
/// The AreaTable level (<c>AreaLevel</c>, the 1.12.1 AreaTable.dbc exploration level; vmangos <c>AreaTableEntry::area_level</c>) at a
/// position of one map, for the danger check of a walk home. Over the terrain it is the level of the area
/// <see cref="AreaTable.GetByAreaFlagAndMap"/> answers for the tile's area flag (the lookup <see cref="PlayerbotZoneLookup"/> uses), cached
/// per flag. 0 where the terrain knows no area or the area carries no level (most zone rows: the levels sit on the sub-areas).
/// <para>
/// Inside a WMO group that <c>wmo_area_template</c> gives an area, the answer is that area's (<see cref="TerrainInfo.GetAreaFlag"/>,
/// vmangos <c>TerrainInfo::GetAreaFlag</c>); where the world has no such rows, no model data or no loaded model tile for the place (a cave
/// that is not a WMO never has one), it is the outdoor area above, and the danger of a way through such a place can be under-reported.
/// </para>
/// <para>Thread affinity: world thread (the terrain loads tiles on demand; the cache is a plain dictionary).</para>
/// </summary>
internal sealed class PlayerbotAreaLevelLookup
{
    private readonly Func<float, float, float, int> _levelAt;

    /// <summary>A lookup over any function (tests: a fixture of a dangerous strip).</summary>
    internal PlayerbotAreaLevelLookup(Func<float, float, float, int> levelAt) => _levelAt = levelAt;

    /// <summary>The terrain's lookup of <paramref name="mapId"/>.</summary>
    internal static PlayerbotAreaLevelLookup FromTerrain(TerrainManager terrain, uint mapId)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        var levels = new Dictionary<ushort, int>();
        AreaTable? cachedAreas = null;
        return new PlayerbotAreaLevelLookup((x, y, z) =>
        {
            // Asked for each lookup, not kept: a map id's terrain goes once its last map is unloaded (TerrainManager.Release).
            TerrainInfo info = terrain.For(mapId);
            AreaTable areas = terrain.Areas;
            if (!ReferenceEquals(areas, cachedAreas)) { levels.Clear(); cachedAreas = areas; }
            ushort flag = info.GetAreaFlag(x, y, z);
            if (!levels.TryGetValue(flag, out int level))
                levels[flag] = level = flag == 0 ? 0 : Math.Max(0, areas.GetByAreaFlagAndMap(flag, mapId)?.AreaLevel ?? 0);
            return level;
        });
    }

    /// <summary>The area level at a position (0: not known).</summary>
    internal int LevelAt(float x, float y, float z) => float.IsFinite(x) && float.IsFinite(y) ? _levelAt(x, y, z) : 0;

    /// <summary>
    /// The highest area level on the straight line from <paramref name="from"/> to <paramref name="to"/>, sampled every
    /// <see cref="PlayerbotHomeRules.DangerSampleYards"/> (both ends included; the height is interpolated),
    /// leaving out the points <paramref name="skip"/> names (the home area itself, <see cref="PlayerbotHomeReturn.Judge"/>).
    /// </summary>
    internal int HighestOnLine(System.Numerics.Vector3 from, System.Numerics.Vector3 to, Func<System.Numerics.Vector3, bool>? skip = null)
    {
        float length = System.Numerics.Vector2.Distance(new(from.X, from.Y), new(to.X, to.Y));
        if (!float.IsFinite(length)) return 0;
        int samples = Math.Max(1, (int)MathF.Ceiling(length / PlayerbotHomeRules.DangerSampleYards));
        int highest = 0;
        for (int i = 0; i <= samples; i++)
        {
            System.Numerics.Vector3 at = System.Numerics.Vector3.Lerp(from, to, i / (float)samples);
            if (skip?.Invoke(at) == true) continue;
            highest = Math.Max(highest, LevelAt(at.X, at.Y, at.Z));
        }

        return highest;
    }
}
