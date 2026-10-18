using System.Numerics;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.World.Bots;

/// <summary>
/// How the walk of a managed bot's current route was planned (<see cref="BotRouteInfo.Flags"/>). The bits are the contract: new ones are
/// only ever added, with a <see cref="Contracts.ContractsVersion"/> bump.
/// </summary>
[Flags]
public enum BotRouteFlags : byte
{
    None = 0,

    /// <summary>The navigation mesh's own answer ended short of the destination: the route is the part of the way the mesh knows.</summary>
    MeshEndedShort = 1,

    /// <summary>The route ends short of its goal because the mesh found no way, and this is as near as it gets.</summary>
    PartialToGoal = 2,

    /// <summary>The route follows a moving target and is re-planned as it moves.</summary>
    Chase = 4,

    /// <summary>Every leg came from the navigation mesh (so each is walkable by construction).</summary>
    Navigated = 8,

    /// <summary>A completed quest's walk back to its ender.</summary>
    QuestReturn = 16,
}

/// <summary>The state of a bot's route besides its points: the index of the point it walks toward, and how the route was planned.</summary>
/// <param name="Next">Index into the route's points of the point the bot is walking toward (the points before it are behind the bot).</param>
/// <param name="Flags">How the route was planned.</param>
public readonly record struct BotRouteInfo(int Next, BotRouteFlags Flags);

/// <summary>
/// Read-only view of the route a managed bot is walking (docs/areas/map-feed.md), for tools that draw it. The playerbot pack registers
/// one on the core's <c>ManagedBotRegistry</c> while it is attached (the same one-slot seam pattern as <see cref="IManagedBotSessionPolicy"/>);
/// without one (the pack detached) the feed reports no routes. The core never touches the pack's route type.
/// </summary>
public interface IBotRouteSource
{
    /// <summary>
    /// Copy the route <paramref name="bot"/> is walking now into <paramref name="destination"/> (the points in walking order, the first
    /// where the route started). Returns the route's number of points, or 0 when the bot walks no route (then nothing is written and
    /// <paramref name="info"/> is default). When the route has more points than <paramref name="destination"/> holds, only the first
    /// <c>destination.Length</c> are written and the return value says how many there are in all, so the caller can retry with room.
    /// World thread only; allocates nothing; never throws for a bot without a route.
    /// </summary>
    int CopyRoute(Player bot, Span<Vector3> destination, out BotRouteInfo info);
}
