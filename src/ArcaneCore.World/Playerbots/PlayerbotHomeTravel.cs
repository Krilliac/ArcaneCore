using System.Numerics;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The one seam of travel to a home (the town homes of <see cref="Population.PopulationTownProgression"/>): how a bot that is not in its
/// home gets there, and whether it can get there without a teleport. The default (<see cref="DefaultHomeTravel"/>) is what the Home
/// options say: walk, or the one return teleport (<see cref="PlayerbotHomeReturn.TryTeleport"/>) when the way is too long, crosses
/// water or another map. A travel layer that carries bots by taxi, boat, zeppelin or a portal registers its own implementation as a
/// service (<c>IPlayerbotHomeTravel</c>) and is asked by <see cref="PlayerbotHomeReturn.Judge"/> and by the population's sweep.
/// <para>Thread affinity: both members are called on the world thread (Judge) or the population pass thread (the sweep) and must be pure reads.</para>
/// </summary>
internal interface IPlayerbotHomeTravel
{
    /// <summary>Whether a bot at <paramref name="from"/> on <paramref name="fromMap"/> reaches <paramref name="to"/> on <paramref name="toMap"/> by its own means (walking, a taxi), without a teleport.</summary>
    bool CanReachWithoutTeleport(uint fromMap, Vector3 from, uint toMap, Vector3 to);

    /// <summary>
    /// How this return travels: <see cref="PlayerbotHomeTeleportReason.None"/> walks (or rides), any other reason teleports. The facts are
    /// the return rules' (<see cref="PlayerbotHomeFacts"/>; a distance of infinity: water or another obstacle lies between);
    /// <paramref name="teleportDistanceYards"/> is the Auto mode's teleport distance for this home.
    /// </summary>
    PlayerbotHomeTeleportReason Decide(in PlayerbotHomeFacts facts, PlayerbotHomeOptions options, float teleportDistanceYards);
}

/// <summary>Walk, or teleport by the Home options (<see cref="PlayerbotHomeRules.Decide(in PlayerbotHomeFacts, PlayerbotHomeOptions, float)"/>); water: Teldrassil.</summary>
internal sealed class DefaultHomeTravel : IPlayerbotHomeTravel
{
    internal static readonly DefaultHomeTravel Instance = new();

    public bool CanReachWithoutTeleport(uint fromMap, Vector3 from, uint toMap, Vector3 to)
        => fromMap == toMap && !Population.PopulationTownProgression.SeparatedByWater(fromMap, from.X, from.Y, toMap, to.X, to.Y);

    public PlayerbotHomeTeleportReason Decide(in PlayerbotHomeFacts facts, PlayerbotHomeOptions options, float teleportDistanceYards)
        => PlayerbotHomeRules.Decide(facts, options, teleportDistanceYards);
}
