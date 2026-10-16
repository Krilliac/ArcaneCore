using System.Numerics;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The home progression's travel seam (<see cref="IPlayerbotHomeTravel"/>) answered by authentic travel (docs/areas/playbots.md, Travel):
/// a town home across the sea (another continent, or Teldrassil from the mainland) is reachable without a teleport when a boat or zeppelin
/// route joins the two (<see cref="PlayerbotTravel.CanReachStatic(ReadOnlySpan{ShipRoute}, bool, uint, uint, PlayerbotTravelOptions)"/>),
/// so the population may give a bot such a home with <c>TownProgressionTeleport</c> off. The return itself is begun by the brain as a
/// journey (<see cref="IPlayerbotTravel.TryBegin"/>, PlayerbotBrain.TryReturnHome) before the return rules are asked; <see cref="Decide"/>
/// stays the Home options' rules, so the config-gated teleport remains the fallback when no journey can begin.
/// <para>
/// The interface carries no faction, so a route counts when a bot of either faction may ride it: every continent pair is joined for both
/// (the neutral Booty Bay - Ratchet boat), and Teldrassil only by an Alliance boat, where no Horde bot is homed. Before the world thread
/// published the routes (<see cref="PlayerbotTravel.PublishStaticRoutes"/>, at a bot's first home return) the answer is the default's.
/// </para>
/// <para>Thread affinity: any thread; pure reads of the published immutable routes and the live options.</para>
/// </summary>
internal sealed class PlayerbotTravelHomeTravel(PlayerbotOptions options) : IPlayerbotHomeTravel
{
    public bool CanReachWithoutTeleport(uint fromMap, Vector3 from, uint toMap, Vector3 to)
    {
        if (DefaultHomeTravel.Instance.CanReachWithoutTeleport(fromMap, from, toMap, to)) return true;
        PlayerbotTravelOptions travel = options.Travel;
        if (!travel.Enabled) return false;
        IReadOnlyList<ShipRoute> published = PlayerbotTravel.StaticRoutes;
        ShipRoute[] routes = published as ShipRoute[] ?? [.. published];
        if (fromMap != toMap)
            return PlayerbotTravel.CanReachStatic(routes, alliance: true, fromMap, toMap, travel)
                || PlayerbotTravel.CanReachStatic(routes, alliance: false, fromMap, toMap, travel);

        // One map, water between (Teldrassil): a route with a port on each side of it.
        foreach (ShipRoute route in routes)
        {
            if (route.PortA.MapId != fromMap || route.PortB.MapId != fromMap) continue;
            if (!PlayerbotShipNetwork.MayRide(route, alliance: true, travel) && !PlayerbotShipNetwork.MayRide(route, alliance: false, travel)) continue;
            if (Population.PopulationTownProgression.SeparatedByWater(fromMap, route.PortA.Position.X, route.PortA.Position.Y,
                    fromMap, route.PortB.Position.X, route.PortB.Position.Y))
                return true;
        }

        return false;
    }

    public PlayerbotHomeTeleportReason Decide(in PlayerbotHomeFacts facts, PlayerbotHomeOptions homeOptions, float teleportDistanceYards)
        => DefaultHomeTravel.Instance.Decide(facts, homeOptions, teleportDistanceYards);
}
