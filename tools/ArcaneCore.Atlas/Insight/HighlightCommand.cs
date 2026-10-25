using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Live;

namespace ArcaneCore.Atlas.Insight;

/// <summary><c>live.highlight</c>: rings on the live entities that match every given filter.</summary>
internal static class HighlightCommand
{
    public const int DefaultLimit = 256;
    public const int MaxLimit = 5000;
    public const string RingColor = "#FF3DF2";

    public static void Register(CommandRegistry registry, InsightHub hub)
    {
        registry.Register(new CommandDefinition(
            "live.highlight",
            $"Ring the live entities that match all the given filters (at least one is required). Run directly it returns up to {MaxLimit} entities; watched or run from a script it is capped at {InsightArgs.MaxWatchedRows} and says so with truncated and total.",
            [
                new CommandArg("kind", ArgType.String, "Only this kind.", Choices: LiveKinds.Names),
                new CommandArg("name", ArgType.String, "Only entities whose name contains this text, ignoring case."),
                new CommandArg("hpBelow", ArgType.Number, "Only entities with health below this percent (0..100, strictly below)."),
                new CommandArg("motion", ArgType.String, "Only entities whose motion is exactly this (ignoring case), for example chase."),
                new CommandArg("routeFlag", ArgType.String, "Only bots whose route carries this flag."),
                new CommandArg("inCombat", ArgType.Boolean, "Only entities in (true) or out of (false) combat."),
                new CommandArg("map", ArgType.Integer, "Map id. Default: the subscribed map, else the view's map."),
                new CommandArg("limit", ArgType.Integer, $"Most entities to return (1..{MaxLimit}, default {DefaultLimit}).", Default: JsonValue.Create(DefaultLimit)),
            ],
            (call, _) => Task.FromResult<JsonNode?>(Highlight(hub, call))));
    }

    private static JsonObject Highlight(InsightHub hub, CommandInvocation call)
    {
        LiveKind? kind = null;
        if (call.OptionalString("kind") is { } kindText)
        {
            kind = LiveKinds.TryParse(kindText, out LiveKind parsed) ? parsed : throw InsightArgs.Bad(call, $"argument 'kind' must be one of {string.Join(", ", LiveKinds.Names)}.");
        }

        string? name = call.OptionalString("name");
        double? hpBelow = call.OptionalNumber("hpBelow");
        string? motion = call.OptionalString("motion");
        string? routeFlag = call.OptionalString("routeFlag");
        bool? inCombat = call.OptionalBool("inCombat");
        if (kind is null && name is null && hpBelow is null && motion is null && routeFlag is null && inCombat is null)
        {
            throw InsightArgs.Bad(call, "give at least one filter (kind, name, hpBelow, motion, routeFlag or inCombat); without one every entity would match.");
        }

        if (hpBelow is { } below && (!double.IsFinite(below) || below < 0 || below > 100))
        {
            throw InsightArgs.Bad(call, "argument 'hpBelow' must be between 0 and 100.");
        }

        int limit = (int)InsightArgs.IntInRange(call, "limit", 1, MaxLimit, DefaultLimit);
        (uint map, _) = hub.Feed.QueryTarget(InsightArgs.OptionalUInt(call, "map"), null);
        bool capped = call.Origin == CommandOrigin.Script;

        HashSet<ulong> flaggedCombat = [];
        if (hub.Tail.Latest() is { } latest && latest.Map == map)
        {
            foreach (RecordedEntity e in latest.Entities)
            {
                if ((e.Flags & InsightRecorder.FlagCombat) != 0)
                {
                    flaggedCombat.Add(e.Guid);
                }
            }
        }

        IReadOnlyList<StoredEntity> found = hub.Feed.Store.Snapshot(e =>
            e.Map == map
            && (kind is null || e.Kind == kind)
            && (name is null || (e.Name is not null && e.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
            && (hpBelow is null || (e.HealthPercent is { } hp && hp < hpBelow))
            && (motion is null || string.Equals(e.Motion, motion, StringComparison.OrdinalIgnoreCase))
            && (routeFlag is null || (e.Route is { } route && route.Flags.Contains(routeFlag, StringComparer.OrdinalIgnoreCase)))
            && (inCombat is null || (LiveStore.IsInCombat(e) || flaggedCombat.Contains(e.Id)) == inCombat));

        var entities = new JsonArray();
        var points = new JsonArray();
        int nodes = 40;
        int rowCap = capped ? Math.Min(limit, InsightArgs.MaxWatchedRows) : limit;
        foreach (StoredEntity stored in found)
        {
            if (entities.Count >= rowCap)
            {
                break;
            }

            LiveEntity e = stored.Entity;
            var row = new JsonObject { ["g"] = e.GuidText, ["n"] = e.Name, ["hp"] = e.HealthPercent };
            var point = new JsonObject { ["at"] = InsightArgs.Point(e.Position.X, e.Position.Y, e.Position.Z), ["color"] = RingColor };
            int cost = InsightArgs.Nodes(row) + InsightArgs.Nodes(point);
            if (capped && nodes + cost > InsightArgs.NodeBudget)
            {
                break;
            }

            nodes += cost;
            entities.Add(row);
            points.Add(point);
        }

        return new JsonObject
        {
            ["map"] = map,
            ["total"] = found.Count,
            ["returned"] = entities.Count,
            ["truncated"] = entities.Count < found.Count,
            ["entities"] = entities,
            ["overlay"] = new JsonObject { ["points"] = points },
        };
    }
}
