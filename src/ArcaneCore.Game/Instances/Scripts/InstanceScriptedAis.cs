using ArcaneCore.Game.Instances.Scripts.BlackwingLair;

namespace ArcaneCore.Game.Instances.Scripts;

/// <summary>
/// The creature entries (that carry a ScriptName in the world database) whose script AI an instance script of the map gives them when the
/// instance runs: a <c>RegisterEntryAi</c> from its hooks, or a <c>RaidBossAI.Create</c> selection (summoned entries included, not only spawned ones). That happens at run time, so
/// <see cref="Creatures.ScriptCatalog"/> cannot see it without a live map; this table is what it consults instead. Data only: every pair is proven
/// behaviourally by <c>ScriptCoverageTests</c> (the instance is built, the creature spawned, its AI must not be a default one), and the fixture
/// coverage test fails for a ScriptName that is in neither this table nor its known-missing list.
/// </summary>
public static class InstanceScriptedAis
{
    private static readonly Dictionary<uint, uint[]> Entries = new()
    {
        [33] = [3849, 3850, 4275, 4444, 4627],
        [36] = [646],
        [43] = [3678],
        [47] = [4508, 4781],
        [70] = [2748],
        [90] = [7800, 7850, 7998],
        [129] = [8516],
        [189] = [3975, 3976, 3977, 6487],
        [209] = [7271],
        [230] = [8929, 8982, 9018, 9019, 9023, 9033, 9156, 9499, 9500, 9502, 9503, 10096],
        [249] = [10184],
        [309] = [11347, 11348, 11359, 11382, 14507, 14509, 14510, 14515, 14517, 14750, 14834, 15084],
        [349] = [13282],
        [409] = [11502, 11662, 11672, 11982, 11988, 12018, 12056, 12057, 12098, 12099, 12118, 12259, 12264],
        [469] = [10162, 11583, BlackwingDrakeAI.FlamegorEntry, BlackwingDrakeAI.FiremawEntry, 12017, 12435, 13020, 14020, 14449, 14601],
        [509] = [15339, 15340, 15348, 15355, 15369, 15370, 15471, 15473, 15514],
        [531] = [15263, 15264, 15275, 15276, 15277, 15299, 15509, 15510, 15511, 15516, 15517, 15543, 15544, 15589, 15712, 15718, 15727, 15728, 15957],
        [533] = [15928, 15929, 15930, 15931, 15932, 15936, 15952, 15953, 15954, 15956, 15989, 15990, 16011, 16027, 16028, 16056, 16057, 16060, 16061, 16062, 16063, 16064, 16065, 16168, 16441],
    };

    /// <summary>Whether the instance script of <paramref name="mapId"/> gives the creature <paramref name="entry"/> a script AI.</summary>
    public static bool Contains(uint mapId, uint entry) => Entries.TryGetValue(mapId, out uint[]? entries) && Array.IndexOf(entries, entry) >= 0;

    /// <summary>Every (map, entry) pair of the table.</summary>
    public static IEnumerable<(uint MapId, uint Entry)> All => Entries.SelectMany(pair => pair.Value.Select(entry => (pair.Key, entry)));
}
