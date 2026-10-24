using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.World.Playerbots;

/// <summary>A read-only copy of one bot's lift ride (<c>.playerbot lift</c>), so the command never touches the live <see cref="ElevatorTrip"/>.</summary>
internal sealed record ElevatorTripInfo(
    string BotName,
    string LiftName,
    ElevatorStage Stage,
    ElevatorLevel From,
    ElevatorLevel To,
    uint? CarEntry,
    uint? CarGuid,
    uint StageMs,
    uint TripMs,
    bool RouteHeld,
    bool Alighting,
    int MissedStops,
    string? Failure)
{
    /// <summary>
    /// The lift rides under way (not done, not failed) of the bots online in <paramref name="world"/>, ordered by bot name. Times are measured
    /// on the world clock. World thread; changes nothing.
    /// </summary>
    internal static IReadOnlyList<ElevatorTripInfo> Active(WorldRuntime world)
    {
        var rows = new List<ElevatorTripInfo>();
        foreach ((Player player, ElevatorTrip trip) in PlayerbotElevator.ActiveTrips(world))
        {
            rows.Add(new ElevatorTripInfo(
                player.Name, trip.Lift.Name, trip.Stage, trip.From, trip.To,
                trip.Car?.Entry, trip.Car?.SpawnGuid, unchecked(world.NowMs - trip.StageMs), unchecked(world.NowMs - trip.BeganMs),
                trip.Route is not null, trip.Alighting, trip.MissedStops, trip.Failure));
        }

        rows.Sort((a, b) => string.Compare(a.BotName, b.BotName, StringComparison.OrdinalIgnoreCase));
        return rows;
    }
}
