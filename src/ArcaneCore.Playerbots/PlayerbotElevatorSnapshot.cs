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
    /// The lift rides under way (not done, not failed) of the bots on <paramref name="map"/>, ordered by bot name. Times are measured on the
    /// world clock. Call it from the thread that owns <paramref name="map"/> (the live trips of other maps are not read); changes nothing.
    /// </summary>
    internal static IReadOnlyList<ElevatorTripInfo> Active(WorldRuntime world, Map map)
    {
        var rows = new List<ElevatorTripInfo>();
        uint now = world.NowMs; // one reading for every row: the stamps are World readings of an earlier tick or of this one (see AgeMs)
        foreach ((Player player, ElevatorTrip trip) in PlayerbotElevator.ActiveTrips(map))
        {
            rows.Add(new ElevatorTripInfo(
                player.Name, trip.Lift.Name, trip.Stage, trip.From, trip.To,
                trip.Car?.Entry, trip.Car?.SpawnGuid, AgeMs(now, trip.StageMs), AgeMs(now, trip.BeganMs),
                trip.Route is not null, trip.Alighting, trip.MissedStops, trip.Failure));
        }

        rows.Sort((a, b) => string.Compare(a.BotName, b.BotName, StringComparison.OrdinalIgnoreCase));
        return rows;
    }

    /// <summary>
    /// The milliseconds from a stamp to <paramref name="now"/>, both wrapped World (<c>WorldRuntime.NowMs</c>) readings: the modular difference is the true age across the
    /// 49.7-day wrap, and 0 for a stamp taken this tick. <c>PlayerbotElevator</c> stamps <c>StageMs</c> and <c>BeganMs</c> only from the tick-start <c>session.World.NowMs</c>
    /// (directly or through the <c>now</c> its callers pass), and that clock never runs backwards, so the stamp is never later than <paramref name="now"/>.
    /// </summary>
    internal static uint AgeMs(uint now, uint stampMs) => unchecked(now - stampMs);
}
