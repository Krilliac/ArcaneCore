using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Live;

namespace ArcaneCore.Atlas.Insight;

/// <summary><c>live.inspect</c>: one entity's state from the world daemon, with aura names from the client's Spell.dbc.</summary>
internal static class InspectCommands
{
    public const string RingColor = "#FFFFFF";

    public static void Register(CommandRegistry registry, InsightHub hub)
    {
        registry.Register(new CommandDefinition(
            "live.inspect",
            "Inspect one live entity on the world daemon (read-only, on its world thread): level, hp, power, faction, unit flags, stand state, target and victim, in-combat, the top 10 of its threat list, auras (spell id and name, stacks, remaining ms, caster), the motion generator stack, AI name and the bot route and goal when the server exposes them. Draws a ring on the entity. Needs a world daemon with live insight; an older one answers unavailable.",
            [
                new CommandArg("guid", ArgType.String, "Entity guid, a quoted hex string.", Required: true),
                new CommandArg("map", ArgType.Integer, "Map id. Default: the subscribed map, else the view's map."),
                new CommandArg("instance", ArgType.Integer, "Instance id. Default: the subscription's when it is for this map, else 0."),
            ],
            (call, ct) => InspectAsync(hub, call, ct)));
    }

    private static async Task<JsonNode?> InspectAsync(InsightHub hub, CommandInvocation call, CancellationToken cancellationToken)
    {
        ulong guid = InsightArgs.RequiredGuid(call, "guid");
        (uint map, uint instance) = hub.Feed.QueryTarget(InsightArgs.OptionalUInt(call, "map"), InsightArgs.OptionalUInt(call, "instance"));
        var query = new JsonObject
        {
            ["kind"] = "inspect",
            ["map"] = map,
            ["instance"] = instance,
            ["guid"] = LiveGuid.Format(guid),
        };
        JsonObject data = await hub.Feed.RequestAsync(query, cancellationToken).ConfigureAwait(false);

        if (data["auras"] is JsonArray auras)
        {
            SpellNames? names = hub.Spells();
            foreach (JsonNode? aura in auras)
            {
                if (aura is JsonObject row)
                {
                    row["name"] = row["spell"] is JsonValue v && v.TryGetValue(out long id) && id is >= 0 and <= uint.MaxValue
                        ? names?.Name((uint)id)
                        : null;
                }
            }
        }

        if (InsightArgs.TryPoint(data["p"], out (double X, double Y, double Z) at))
        {
            string label = data["name"] is JsonValue n && n.TryGetValue(out string? name) && !string.IsNullOrEmpty(name) ? name : LiveGuid.Format(guid);
            data["overlay"] = new JsonObject
            {
                ["points"] = new JsonArray(new JsonObject { ["at"] = InsightArgs.Point(at.X, at.Y, at.Z), ["label"] = label, ["color"] = RingColor }),
            };
        }

        return data;
    }
}
