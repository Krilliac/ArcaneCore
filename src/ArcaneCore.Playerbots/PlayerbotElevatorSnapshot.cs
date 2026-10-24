using ArcaneCore.Game.Entities;

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
    /// The lift rides under way (not done, not failed) of the bots in the world, ordered by bot name. Times are measured at <paramref name="nowMs"/>
    /// on the world clock. World thread; changes nothing.
    /// </summary>
    internal static IReadOnlyList<ElevatorTripInfo> Active(uint nowMs)
    {
        var rows = new List<ElevatorTripInfo>();
        foreach ((Player player, ElevatorTrip trip) in PlayerbotElevator.ActiveTrips())
        {
            rows.Add(new ElevatorTripInfo(
                player.Name, trip.Lift.Name, trip.Stage, trip.From, trip.To,
                trip.Car?.Entry, trip.Car?.SpawnGuid, unchecked(nowMs - trip.StageMs), unchecked(nowMs - trip.BeganMs),
                trip.Route is not null, trip.Alighting, trip.MissedStops, trip.Failure));
        }

        rows.Sort((a, b) => string.Compare(a.BotName, b.BotName, StringComparison.OrdinalIgnoreCase));
        return rows;
    }
}
