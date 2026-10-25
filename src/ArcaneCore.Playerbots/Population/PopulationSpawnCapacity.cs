using ArcaneCore.Game.Entities;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// The creature spawns a bot of each team can fight on the surface of each zone, by template level (<see cref="PopulationCreatureSurvey.CountSurface"/>):
/// the capacity of a zone. Indoor and underground spawns (a crypt, a cave: <c>PlayerbotNavigation.IsOutdoorsOnSurface</c> says no) are
/// not in it, since a bot walking the surface never reaches them.
/// <para>
/// A spawn <em>fits</em> a bot level range when its template level is within <see cref="LevelsBelow"/> levels below the range's lowest
/// level and <see cref="LevelsAbove"/> above its highest (an ArcaneCore choice: lower creatures give no experience, higher ones kill).
/// </para>
/// <para>Thread affinity: immutable after construction (the arrays are never written again); safe to read from any thread.</para>
/// </summary>
internal sealed class PopulationSpawnCapacity
{
    /// <summary>How many levels below a bot's range a creature still counts as worth fighting.</summary>
    public const int LevelsBelow = 5;

    /// <summary>How many levels above a bot's range a creature still counts as fightable (the placement safety gap).</summary>
    public const int LevelsAbove = PopulationPlacementSafety.MaxLevelGap;

    private readonly IReadOnlyDictionary<(uint ZoneId, Team Team), int[]> _levels;

    /// <param name="levels">The template levels of the surface spawns by zone and team, each array sorted ascending (not copied).</param>
    public PopulationSpawnCapacity(IReadOnlyDictionary<(uint ZoneId, Team Team), int[]> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        _levels = levels;
    }

    /// <summary>Surface spawns counted over all zones and both teams (diagnostics).</summary>
    public int Total => _levels.Values.Sum(l => l.Length);

    /// <summary>
    /// The surface spawns of <paramref name="zoneId"/> a bot of <paramref name="team"/> with a level in
    /// <paramref name="minLevel"/>..<paramref name="maxLevel"/> fights; 0 for a zone with none.
    /// </summary>
    public int Fitting(uint zoneId, Team team, int minLevel, int maxLevel)
    {
        if (!_levels.TryGetValue((zoneId, team), out int[]? levels)) return 0;
        return UpperBound(levels, maxLevel + LevelsAbove) - LowerBound(levels, minLevel - LevelsBelow);
    }

    private static int LowerBound(int[] sorted, int value)
    {
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (sorted[mid] < value) low = mid + 1; else high = mid;
        }

        return low;
    }

    private static int UpperBound(int[] sorted, int value)
    {
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (sorted[mid] <= value) low = mid + 1; else high = mid;
        }

        return low;
    }
}
