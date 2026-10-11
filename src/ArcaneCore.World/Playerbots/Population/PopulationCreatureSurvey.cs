using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// The creature level near a placement location: what a bot landing at a graveyard meets first. The area level alone misses it: the
/// Feralas Dire Maul graveyard stands in Dire Maul (44) beside level 43-46 Longtooth Howlers, Grizzled Ironfur Bears and Rogue Vale
/// Screechers, and Morgan's Plot in Deadwind Pass (an unlevelled sub-area, so the zone's 50) beside level 58-60 undead.
/// <para>
/// The level of a location for a team is the lower median of the template <c>MinLevel</c> of the database creature spawns within the
/// radius that are hostile to the team: a bot of level L meets mostly creatures above L + margin exactly when that median is above
/// L + margin. Hostile is the FactionTemplate.dbc relation of the creature's template to the team's player template (1 Human for the
/// Alliance, 2 Orc for the Horde; vmangos <c>FactionTemplateEntry::IsHostileTo</c>), without reputation: a placement heuristic, not the
/// combat rule. Only creatures within <see cref="MaxHeightDifference"/> yards above or below the graveyard count: the 2D circle around
/// the Stonard graveyard (z 22) takes in 23 level 46-47 Nethergarde Miners and 3 others at z -13 to +7 in the valley below it, which
/// would close the Horde's level 35-45 hub. Civilians, creatures that never aggro on sight, invisible (trigger) creatures and
/// not-selectable ones do not count, nor do spawns listed under a positive game event (the AQ war effort silithids at Camp Mojache
/// and Gadgetzan are absent outside their event). Fewer than <see cref="MinHostile"/> hostile creatures give no level.
/// </para>
/// <para>Thread affinity: pure; the feature runs it on the background graveyard lookup, over immutable spawn lists.</para>
/// </summary>
internal static class PopulationCreatureSurvey
{
    /// <summary>The fewest hostile creatures near a location that give it a level (a lone patrol or rare is not the place).</summary>
    public const int MinHostile = 3;

    /// <summary>The most yards a creature spawn may stand above or below the graveyard and still count.</summary>
    public const float MaxHeightDifference = 15f;

    private const CreatureBehaviourFlags Ignored = CreatureBehaviourFlags.Civilian | CreatureBehaviourFlags.NoAggro | CreatureBehaviourFlags.Invisible;

    /// <summary>The player faction template the survey tests hostility against: 1 (Human) for the Alliance, 2 (Orc) for the Horde.</summary>
    public static uint PlayerTemplateOf(Team team) => team == Team.Alliance ? 1u : 2u;

    /// <summary>
    /// The lower median <c>MinLevel</c> of the hostile creatures within <paramref name="radius"/> yards (2D) of <paramref name="location"/>
    /// and <see cref="MaxHeightDifference"/> yards of its height,
    /// or 0 when fewer than <see cref="MinHostile"/> are there, the radius is not positive or the team's player template is unknown.
    /// </summary>
    public static int LevelNear(WorldSafeLoc location, Team team, float radius, IReadOnlyList<CreatureSpawn> spawnsOnMap,
        Func<uint, CreatureTemplate?> templateOf, FactionTemplateCatalog factions, IReadOnlySet<uint> gated)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(spawnsOnMap);
        ArgumentNullException.ThrowIfNull(templateOf);
        ArgumentNullException.ThrowIfNull(factions);
        ArgumentNullException.ThrowIfNull(gated);
        if (radius <= 0 || factions.Find(PlayerTemplateOf(team)) is not { } player) return 0;
        float r2 = radius * radius;
        var levels = new List<int>();
        foreach (CreatureSpawn spawn in spawnsOnMap)
        {
            float dx = spawn.X - location.X, dy = spawn.Y - location.Y;
            if (spawn.MapId != location.MapId || dx * dx + dy * dy > r2 || MathF.Abs(spawn.Z - location.Z) > MaxHeightDifference) continue;
            if (gated.Contains(spawn.Guid) || templateOf(spawn.Entry) is not { } template) continue;
            if ((template.Behaviour & Ignored) != 0 || (template.UnitFlags & (uint)UnitFlags.NotSelectable) != 0) continue;
            if (factions.Find(template.Faction) is not { } faction || !faction.IsHostileTo(player)) continue;
            levels.Add(template.MinLevel);
        }

        if (levels.Count < MinHostile) return 0;
        levels.Sort();
        return levels[(levels.Count - 1) / 2];
    }
}
