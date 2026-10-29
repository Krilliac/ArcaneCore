using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Playerbots;

/// <summary>The means of one leg of a planned trip (<see cref="PlayerbotTravelPlanner"/>).</summary>
internal enum TravelMeans : byte
{
    /// <summary>On foot (the bot's ordinary navigation, which takes the Deeprun Tram, lifts and known flight paths on the way).</summary>
    Walk,

    /// <summary>A flight path between two known nodes (<see cref="PlayerbotTaxi"/>).</summary>
    FlightPath,

    /// <summary>A boat between two ports (<see cref="PlayerbotShip"/>).</summary>
    Boat,

    /// <summary>A zeppelin between two towers (<see cref="PlayerbotShip"/>).</summary>
    Zeppelin,

    /// <summary>The bot's own hearthstone (item 6948, spell 8690) to its bind point (<see cref="PlayerbotHearthstone"/>).</summary>
    Hearthstone,

    /// <summary>A mage bot's own Teleport spell to a capital (<see cref="PlayerbotMageService"/>).</summary>
    MageTeleport,

    /// <summary>A portal a mage bot opened on request, clicked by the requester (<see cref="PlayerbotMageService"/>).</summary>
    MagePortal,

    /// <summary>A same-map teleport trigger (an <c>areatrigger_teleport</c> row whose target is the trigger's own map) walked into: Teldrassil's Darnassus and Rut'theran portals.</summary>
    AreaTrigger,
}

/// <summary>Which faction may use a transport route.</summary>
internal enum TravelAccess : byte { Both, Alliance, Horde }

/// <summary>A place on a map.</summary>
internal readonly record struct TravelPlace(uint MapId, Vector3 Position)
{
    public static TravelPlace Of(Player player) => new(player.MapId, new Vector3(player.X, player.Y, player.Z));

    public float DistanceTo(TravelPlace other) => other.MapId == MapId ? Vector3.Distance(Position, other.Position) : float.PositiveInfinity;
}

/// <summary>
/// One leg of a planned trip: how, from where, to where, the estimated time and the copper it costs. <see cref="TransportEntry"/> names
/// the ship of a boat or zeppelin leg; <see cref="SpellId"/> the Teleport or Portal spell of a mage leg (the portal's own spell for a
/// requester); <see cref="MageGuid"/> the mage a portal leg asked;
/// <see cref="TriggerId"/> the area trigger of an <see cref="TravelMeans.AreaTrigger"/> leg.
/// </summary>
internal sealed record TravelLeg(TravelMeans Means, TravelPlace From, TravelPlace To, float Seconds, uint Copper)
{
    public uint TransportEntry { get; init; }
    public uint TriggerId { get; init; }
    public uint SpellId { get; init; }
    public ulong MageGuid { get; init; }
}

/// <summary>A whole planned trip: its legs in order, their total time and copper.</summary>
internal sealed record TravelRoute(IReadOnlyList<TravelLeg> Legs)
{
    public float Seconds { get; } = Legs.Sum(l => l.Seconds);
    public uint Copper { get; } = (uint)Math.Min(uint.MaxValue, Legs.Sum(l => (long)l.Copper));
}

/// <summary>
/// One boat or zeppelin route as the world's data has it (<see cref="PlayerbotShipNetwork"/>): the ship's gameobject entry, which kind it
/// is, who may ride it, the two ports (where the ship stands while it waits there, from its TaxiPathNode stop frames) and its round trip.
/// </summary>
internal sealed record ShipRoute(uint Entry, TravelMeans Means, TravelAccess Access, TravelPlace PortA, TravelPlace PortB, float PeriodSeconds);

/// <summary>A portal some mage of the bot's faction near it can open (<see cref="PlayerbotPortalBoard"/>): where it leads, where the mage stands, and its fee.</summary>
internal readonly record struct PortalOffer(ulong MageGuid, uint PortalSpellId, TravelPlace MagePlace, TravelPlace Destination, uint FeeCopper);

/// <summary>
/// A same-map teleport trigger a bot may walk into (<see cref="PlayerbotAreaTriggers.SameMapPortals"/>): the trigger's id, where its volume
/// is (<paramref name="Entrance"/>) and where its teleport puts the bot (<paramref name="Exit"/>). One way, entrance to exit.
/// </summary>
internal sealed record TriggerPortal(uint TriggerId, TravelPlace Entrance, TravelPlace Exit);

/// <summary>A Teleport spell the bot (a mage) knows and can cast now, with the place it leads to (<c>spell_target_position</c>).</summary>
internal readonly record struct OwnTeleport(uint SpellId, TravelPlace Destination);

/// <summary>
/// Everything the planner (<see cref="PlayerbotTravelPlanner.Plan"/>) knows about one bot: what it is, what it has, which routes it may
/// take. Built on the world thread from the bot's real state (<see cref="PlayerbotTravel"/>); the planner itself is a pure function of it.
/// </summary>
internal sealed record TravelFacts
{
    public required bool Alliance { get; init; }
    public required uint Level { get; init; }
    public required uint Money { get; init; }

    /// <summary>The most copper the trip may spend (<see cref="PlayerbotTravelOptions.MaxTravelSpendCopper"/>, and never more than <see cref="Money"/>).</summary>
    public required uint SpendCap { get; init; }

    /// <summary>The bot carries its hearthstone, it is off cooldown, and hearthstone use is on: it leads to <see cref="Bind"/>.</summary>
    public bool HearthReady { get; init; }

    public TravelPlace? Bind { get; init; }

    /// <summary>The bot's own Teleport spells it can cast now (known, rune carried, off cooldown; empty for a non-mage or with the service off).</summary>
    public IReadOnlyList<OwnTeleport> OwnTeleports { get; init; } = [];

    /// <summary>Portals mages near the bot offer now (empty with the service off).</summary>
    public IReadOnlyList<PortalOffer> Portals { get; init; } = [];

    /// <summary>The boat and zeppelin routes the bot may ride now (faction and the Boats/Zeppelins switches applied).</summary>
    public IReadOnlyList<ShipRoute> Ships { get; init; } = [];

    /// <summary>The same-map teleport triggers the bot may walk into on its map (policy applied, the journey's failed ones left out).</summary>
    public IReadOnlyList<TriggerPortal> TriggerPortals { get; init; } = [];

    /// <summary>
    /// Whether a bot can walk (or ride, or fly a known path) from one place to another on the same map: the planner never walks between maps.
    /// Null: any two places on one map are taken as joined.
    /// </summary>
    public Func<TravelPlace, TravelPlace, bool>? Walkable { get; init; }

    /// <summary>A known, affordable flight that shortens a same-map stretch: its time and fare, or null (no such flight, or flight paths off).</summary>
    public Func<TravelPlace, TravelPlace, (float Seconds, uint Copper)?>? Flight { get; init; }
}

/// <summary>What a leg driver (<see cref="PlayerbotShip"/>, <see cref="PlayerbotHearthstone"/>, <see cref="PlayerbotMageService"/>) did this think.</summary>
internal enum TravelLegStep : byte
{
    /// <summary>No leg of this driver: the caller goes on.</summary>
    Idle,

    /// <summary>The leg is under way; nothing else moves the bot this think.</summary>
    Traveling,

    /// <summary>The leg ended where it should (reported once).</summary>
    Arrived,

    /// <summary>The leg failed (reported once, with a reason): only this leg is deferred, never the goal behind it.</summary>
    Failed,
}

/// <summary>
/// The one seam of far travel (registered as a service; <see cref="PlayerbotTravel"/> is the implementation). A caller with a far goal
/// asks whether the bot can get there by its own means and begins the trip; the brain drives a trip under way before anything else. The
/// home progression's <c>IPlayerbotHomeTravel</c> adapts onto this (<see cref="PlayerbotTravel.CanReachStatic(bool, uint, System.Numerics.Vector3, uint, System.Numerics.Vector3, PlayerbotTravelOptions)"/> for its off-thread check).
/// Thread affinity: world thread.
/// </summary>
internal interface IPlayerbotTravel
{
    /// <summary>Whether the bot could reach <paramref name="to"/> on <paramref name="toMap"/> now by its own means (no trip begun).</summary>
    bool CanReach(WorldSession session, Player player, uint toMap, Vector3 to);

    /// <summary>Plan and begin a trip; false (with why) when no route exists or a trip cannot begin now. A trip under way answers true.</summary>
    bool TryBegin(WorldSession session, Player player, uint toMap, Vector3 to, out string? why);

    /// <summary>Whether a trip is under way for the bot.</summary>
    bool IsTraveling(Player player);
}
