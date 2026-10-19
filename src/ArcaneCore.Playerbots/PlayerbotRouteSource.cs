using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Bots;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The pack's answer to the core's <see cref="IBotRouteSource"/> seam (docs/areas/map-feed.md): the route a managed bot is walking
/// now, read from the movement state that drives it (<see cref="PlayerbotMotion"/>) without changing it. Registered on the core's
/// <c>ManagedBotRegistry</c> through the feature's subscription bag while <see cref="ManagedPlayerbotFeature"/> is attached, so a
/// detach empties the seam. State: none of its own. Thread affinity: the world thread (the movement state is written there).
/// </summary>
internal sealed class PlayerbotRouteSource : IBotRouteSource
{
    /// <inheritdoc />
    public int CopyRoute(Player bot, Span<Vector3> destination, out BotRouteInfo info)
    {
        ArgumentNullException.ThrowIfNull(bot);
        info = default;
        if (!PlayerbotMotion.TryGetState(bot, out PlayerbotMotion.MotionState? state) || state.Route is not { } route)
        {
            return 0;
        }

        IReadOnlyList<Vector3> points = route.Points;
        int count = points.Count;
        int copy = Math.Min(count, destination.Length);
        for (int i = 0; i < copy; i++)
        {
            destination[i] = points[i];
        }

        info = new BotRouteInfo(route.NextPoint, FlagsOf(route));
        return count;
    }

    private static BotRouteFlags FlagsOf(PlayerbotRoute route)
    {
        BotRouteFlags flags = BotRouteFlags.None;
        if (route.MeshEndedShort)
        {
            flags |= BotRouteFlags.MeshEndedShort;
        }

        if (route.PartialToGoal)
        {
            flags |= BotRouteFlags.PartialToGoal;
        }

        if (route.Chase)
        {
            flags |= BotRouteFlags.Chase;
        }

        if (route.Navigated)
        {
            flags |= BotRouteFlags.Navigated;
        }

        if (route.CompletedQuestReturn)
        {
            flags |= BotRouteFlags.QuestReturn;
        }

        return flags;
    }
}
