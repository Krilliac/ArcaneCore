using System.Numerics;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots.Groups;

namespace ArcaneCore.World.Playerbots.Combat;

/// <summary>The questions a bot asks of a creature before it picks it as a target, in one place so the siege scan and the others agree.</summary>
internal static class PlayerbotTargetFilter
{
    /// <summary>A civilian: the template says so (<see cref="CreatureTemplate.Civilian"/>) or carries the Civilian behaviour flag. The rule of the siege scan; a city is full of them and they are left alone.</summary>
    public static bool IsCivilian(Creature creature)
        => creature.Template.Civilian || (creature.Template.Behaviour & CreatureBehaviourFlags.Civilian) != 0;

    /// <summary>
    /// Whether the creature attacks what comes into its sight (<see cref="CreatureAI.AggroesOnSight"/>). A creature without an AI yet is taken
    /// to aggro: the answer is the cautious one for a bot that walks past it.
    /// </summary>
    public static bool AggroesOnSight(Creature creature) => creature.AI?.AggroesOnSight ?? true;

    /// <summary>
    /// The two points are on different levels: their heights differ by more than <paramref name="yards"/> (exactly that is the same level).
    /// The Undercity floors are about 20 yards apart, a lift top stands over the floor below it in the plane.
    /// </summary>
    public static bool OnOtherLevel(Vector3 a, Vector3 b, float yards = PlayerbotSiegeCommander.WaypointLevelYards)
        => MathF.Abs(a.Z - b.Z) > yards;
}
