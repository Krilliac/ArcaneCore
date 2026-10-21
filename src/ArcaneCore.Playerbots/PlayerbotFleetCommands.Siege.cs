using System.Globalization;
using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// <c>.playerbot siege</c>: the GM's orders for a bot-led siege (docs/areas/playbots-groups.md, "Siege"). The plan and the state are the
/// <see cref="PlayerbotGroupCoordinator"/>'s, in memory only. World thread.
/// </summary>
public sealed partial class PlayerbotFleetCommands
{
    private static ChatCommand SiegeCommand() => new("siege", AccountSecurity.Administrator,
        "Syntax: .playerbot siege waypoint add [at $map $x $y $z]|list|clear\n.playerbot siege start $name|$prefix*|all #entry [raids 1|2] [force]\n.playerbot siege stop\n.playerbot siege status [$leader]\n.playerbot siege watch [on|off]\nBot-led raids march the stored waypoints in step and kill the creature with the entry. Memory only: a restart ends it.",
        Children:
        [
            new ChatCommand("waypoint", AccountSecurity.Administrator,
                "Syntax: .playerbot siege waypoint add [at $map $x $y $z]|list|clear\nadd stores your position, or the given map and coordinates, as the next waypoint (all on one map, at most 64); list prints them; clear forgets them. Refused while a siege runs.",
                Children:
                [
                    new ChatCommand("add", AccountSecurity.Administrator, "Syntax: .playerbot siege waypoint add\n.playerbot siege waypoint add at $map $x $y $z\nStore your position, or the given map and coordinates (no player needed, so the server console and RA can use it; the height is taken as given), as the next waypoint of the siege.", SiegeWaypointAdd),
                    new ChatCommand("list", AccountSecurity.Administrator, "Syntax: .playerbot siege waypoint list\nList the stored waypoints in order.", SiegeWaypointList),
                    new ChatCommand("clear", AccountSecurity.Administrator, "Syntax: .playerbot siege waypoint clear\nForget the stored waypoints (refused while a siege runs).", SiegeWaypointClear),
                ]),
            new ChatCommand("start", AccountSecurity.Administrator,
                "Syntax: .playerbot siege start $name|$prefix*|all #entry [raids 1|2] [force]\nForm the running bots into one or two bot-led raids (tanks, healers and damage dealt round them; the best tank leads) that march the waypoints in step and kill the creature with the entry. Needs two bots per raid and at least one waypoint.",
                SiegeStart),
            new ChatCommand("stop", AccountSecurity.Administrator, "Syntax: .playerbot siege stop\nEnd the siege: the raids disband and the bots go back to their own play.", SiegeStop),
            new ChatCommand("status", AccountSecurity.Administrator,
                "Syntax: .playerbot siege status [$leader]\nThe siege under way: step, and for each raid its leader, who lives and what it is doing; with a leader's name, that raid only and the raid warnings it said last.",
                SiegeStatus),
            new ChatCommand("watch", AccountSecurity.Administrator,
                "Syntax: .playerbot siege watch [on|off]\nYou are sent a line for each raid whenever its order changes (at most one every 2 seconds), and when the siege ends. Without an argument: whether you are watching. Memory only.",
                SiegeWatch),
        ]);

    private static PlayerbotGroupCoordinator? SiegeCoordinator(CommandContext context)
    {
        if (context.Session.Services.GetService<PlayerbotGroupCoordinator>() is { } coordinator) return coordinator;
        context.Reply("Playerbot service is unavailable.");
        return null;
    }

    private static string Spot(Vector3 at) => string.Create(CultureInfo.InvariantCulture, $"{at.X:F1}, {at.Y:F1}, {at.Z:F1}");

    private static string Plural(int count, string noun) => string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? string.Empty : "s")}");

    private static bool SiegeWaypointAdd(CommandContext context, string text)
    {
        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool explicitSpot = words.Length == 5 && PlayerbotFleetSelection.IsAtWord(words[0]);
        if (words.Length != 0 && !explicitSpot) return false;
        if (SiegeCoordinator(context) is not { } coordinator) return true;
        uint mapId;
        Vector3 at;
        if (explicitSpot)
        {
            // "at $map $x $y $z": no player involved; the same one-map rule as the position form applies below.
            if (context.Session.Services.GetService<TeleportFeature>() is not { } tele)
            {
                context.Reply("Playerbot service is unavailable.");
                return true;
            }

            if (ResolveSpot(context, tele, "siege", words[1..]) is not { } spot) return true;
            (mapId, at) = (spot.MapId, spot.Position);
        }
        else
        {
            if (Anchor(context, null) is not { } me) return true;
            (mapId, at) = (me.MapId, new Vector3(me.X, me.Y, me.Z));
        }

        if (coordinator.AddSiegeWaypoint(mapId, at) is { } refusal)
        {
            context.Reply($"Playerbot siege: waypoint refused ({refusal}).");
            return true;
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"Playerbot siege: waypoint {coordinator.SiegeWaypoints.Count} added on map {mapId} at {Spot(at)}."));
        return true;
    }

    private static bool SiegeWaypointList(CommandContext context, string text)
    {
        if (text.Trim().Length != 0) return false;
        if (SiegeCoordinator(context) is not { } coordinator) return true;
        IReadOnlyList<Vector3> waypoints = coordinator.SiegeWaypoints;
        if (waypoints.Count == 0)
        {
            context.Reply("Playerbot siege: no waypoints.");
            return true;
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture, $"Playerbot siege: {Plural(waypoints.Count, "waypoint")} on map {coordinator.SiegeWaypointMap}."));
        for (int i = 0; i < waypoints.Count; i++) context.Reply(string.Create(CultureInfo.InvariantCulture, $"{i + 1}: {Spot(waypoints[i])}"));
        return true;
    }

    private static bool SiegeWaypointClear(CommandContext context, string text)
    {
        if (text.Trim().Length != 0) return false;
        if (SiegeCoordinator(context) is not { } coordinator) return true;
        int cleared = coordinator.ClearSiegeWaypoints();
        context.Reply(cleared < 0 ? "Playerbot siege: clear refused (siege-running)." : $"Playerbot siege: {Plural(cleared, "waypoint")} cleared.");
        return true;
    }

    private static bool SiegeStart(CommandContext context, string text)
    {
        if (!PlayerbotFleetSelection.TryParse(text, FleetVerb.Siege, out FleetRequest request)) return false;
        if (Services(context) is not { } env || SiegeCoordinator(context) is not { } coordinator) return true;
        List<PlayerbotStatus> matches = PlayerbotFleetSelection.Match(request, env.Feature.Snapshot());
        if (matches.Count == 0)
        {
            context.Reply(PlayerbotFleetSelection.NothingMatches(request));
            return true;
        }

        if (PlayerbotFleetSelection.NeedsForce(matches.Count, null, request.Force, env.Options.MaxBulkCommand))
        {
            context.Reply(PlayerbotFleetSelection.ForceMessage("siege", matches.Count, env.Options.MaxBulkCommand));
            return true;
        }

        var reasons = new List<(Guid BotId, string Reason)>();
        PlayerbotSiege? siege = coordinator.StartSiege([.. matches.Select(m => m.BotId)], request.Entry, request.Raids, reasons, out string? refusal);
        if (siege is null)
        {
            context.Reply(refusal == "no-waypoints"
                ? "Playerbot siege: no waypoints; stand at a point and .playerbot siege waypoint add."
                : $"Playerbot siege: refused ({refusal}).");
            return true;
        }

        var skipped = new List<Outcome>();
        foreach ((Guid botId, string reason) in reasons)
            skipped.Add(new Outcome(matches.FirstOrDefault(m => m.BotId == botId)?.Name ?? botId.ToString(), reason));
        int enlisted = siege.Raids.Sum(r => r.Group?.Members.Count ?? 0);
        ReportSkips(context, "siege", matches.Count, "enlisted", enlisted, skipped);
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"Siege {siege.Id} started: {Plural(siege.Raids.Count, "raid")}, objective {siege.ObjectiveEntry}, {Plural(siege.Waypoints.Count, "waypoint")}."));
        return true;
    }

    private static bool SiegeStop(CommandContext context, string text)
    {
        if (text.Trim().Length != 0) return false;
        if (SiegeCoordinator(context) is not { } coordinator) return true;
        int? id = coordinator.Siege?.Id;
        context.Reply(coordinator.StopSiege("gm") ? $"Playerbot siege: siege {id} stopped." : "Playerbot siege: none is running.");
        return true;
    }

    private static bool SiegeStatus(CommandContext context, string text)
    {
        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1) return false;
        if (SiegeCoordinator(context) is not { } coordinator) return true;
        foreach (string line in coordinator.SiegeStatus(words.Length == 1 ? words[0] : null)) context.Reply(line);
        return true;
    }

    private static bool SiegeWatch(CommandContext context, string text)
    {
        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        bool? on = words.Length == 0 ? null
            : words.Length == 1 && words[0].Equals("on", StringComparison.OrdinalIgnoreCase) ? true
            : words.Length == 1 && words[0].Equals("off", StringComparison.OrdinalIgnoreCase) ? false
            : null;
        if (words.Length > 1 || words.Length == 1 && on is null) return false;
        if (SiegeCoordinator(context) is not { } coordinator || Anchor(context, null) is not { } me) return true;
        if (coordinator.Siege is not { } siege)
        {
            context.Reply("Playerbot siege: none is running.");
            return true;
        }

        if (on is { } set)
        {
            coordinator.SetSiegeWatch(me.Guid, set);
            context.Reply($"Playerbot siege: watching {(set ? "on" : "off")}.");
        }
        else context.Reply(siege.Watchers.Contains(me.Guid) ? "Playerbot siege: you are watching." : "Playerbot siege: you are not watching.");
        return true;
    }
}
