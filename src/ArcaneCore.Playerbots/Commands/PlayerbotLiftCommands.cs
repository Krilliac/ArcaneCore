using System.Globalization;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Commands;

/// <summary>
/// <c>.playerbot lift [$bot]</c> (GameMaster, read-only): the bots riding the elevators now, or one bot's ride in detail. A bot's ride is
/// kept as its trip (<see cref="PlayerbotElevator"/>); this reads a copy of it (<see cref="ElevatorTripInfo.Active"/>).
/// The lift itself (cycle, stops, who has it in view) is <c>.debug lift</c>.
/// </summary>
public sealed class PlayerbotLiftCommands : ICommandExtension
{
    public string Path => "playerbot";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("lift", AccountSecurity.GameMaster,
            "Syntax: .playerbot lift [$name]\nList the bots riding an elevator now: stage (ToStop, Waiting, Aboard, Falling), the lift and chosen car, the time in the stage, and whether a walking route is held. With $name, that bot's ride in detail. For the lift itself see .debug lift.",
            Lift),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private static string CarText(ElevatorTripInfo trip)
        => trip.CarEntry is { } entry ? string.Create(C, $"car entry {entry} guid {trip.CarGuid}") : "no car chosen";

    private static bool Lift(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string? name = args.ExtractArg();
        if (!args.IsEmpty)
        {
            return false;
        }

        IReadOnlyList<ElevatorTripInfo> trips = ElevatorTripInfo.Active(context.World.NowMs);
        if (name is null)
        {
            foreach (ElevatorTripInfo trip in trips)
            {
                context.Reply(string.Create(C,
                    $"{trip.BotName}: {trip.Stage} {trip.From}->{trip.To} on {trip.LiftName}, {CarText(trip)}, {(trip.Stage == ElevatorStage.Waiting ? "waited" : "in stage")} {trip.StageMs} ms, route {(trip.RouteHeld ? "held" : "none")}"));
            }

            context.Reply(string.Create(C, $"{trips.Count} bot(s) riding a lift."));
            return true;
        }

        ElevatorTripInfo? found = trips.FirstOrDefault(t => t.BotName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            context.Reply($"{name} is not riding a lift.");
            return true;
        }

        context.Reply(string.Create(C, $"{found.BotName}: {found.Stage} on {found.LiftName}, {found.From} -> {found.To}"));
        context.Reply(string.Create(C, $"  {CarText(found)}, {found.StageMs} ms in this stage, {found.TripMs} ms since the trip began"));
        context.Reply(string.Create(C, $"  route {(found.RouteHeld ? "held" : "none")}, alighting at next stop {(found.Alighting ? "yes" : "no")}, missed stops {found.MissedStops}{(found.Failure is null ? string.Empty : ", failure: " + found.Failure)}"));
        return true;
    }
}
