using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Live;

/// <summary>
/// The <c>action.*</c> and <c>devui.*</c> commands. Every dev UI action is one of them, so the browser, the command line and MCP run the same
/// code. Each validates its arguments (BadArguments, before anything is sent) and calls <see cref="LiveActions"/>. Any thread.
/// </summary>
internal static class LiveActionCommands
{
    public const int DefaultLogLimit = 50;
    public const int MaxLogLimit = ActionLog.Capacity;

    public static void Register(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register(new CommandDefinition(
            "action.catalog",
            "The dev UI actions the connected world daemon offers this Atlas (the context menu, the palette and the shortcuts are built from this), with its capabilities and the shared selection. Empty with the reason when the daemon's action channel is off.",
            [new CommandArg("filter", ArgType.String, "Only actions whose id, label or group contains this text.")],
            async (call, ct) => await call.Service<LiveActions>().CatalogAsync(call.OptionalString("filter"), ct).ConfigureAwait(false)));

        registry.Register(new CommandDefinition(
            "action.run",
            "Run one GM command line on the connected world daemon as its audited local actor (the same line you would type in chat; the leading dot is optional). targets are guids the command acts on (the first is its selected unit); at and map give the clicked point.",
            [
                new CommandArg("command", ArgType.String, "The command line, for example 'creature kill'.", Required: true),
                new CommandArg("targets", ArgType.StringList, "Guids (hex) the command acts on, at most 50."),
                new CommandArg("at", ArgType.Point3, "[x, y, z] click point."),
                new CommandArg("map", ArgType.Integer, "Map of the point. Default: the view's map."),
                new CommandArg("instance", ArgType.Integer, "Instance of the point (default 0)."),
                new CommandArg("confirm", ArgType.String, "A confirmation code the daemon issued."),
            ],
            async (call, ct) => await call.Service<LiveActions>().RunCommandAsync(
                call.GetString("command"), Targets(call), Point(call, required: false), call.OptionalString("confirm"), ct).ConfigureAwait(false)));

        registry.Register(new CommandDefinition(
            "action.at",
            "Run a point action of the catalog at a spot of the map (spawn a creature or object, teleport the operator's character or the selection here, move the selection here, list spawn points). See action.catalog for the ids and their args.",
            [
                new CommandArg("template", ArgType.String, "The action id, for example 'place.creature'.", Required: true),
                new CommandArg("at", ArgType.Point3, "[x, y, z] of the spot.", Required: true),
                new CommandArg("map", ArgType.Integer, "Map of the spot. Default: the view's map."),
                new CommandArg("instance", ArgType.Integer, "Instance of the spot (default 0)."),
                new CommandArg("o", ArgType.Number, "Facing in radians (default 0)."),
                new CommandArg("args", ArgType.Object, "The action's arguments, for example {\"entry\":1234}."),
                new CommandArg("targets", ArgType.StringList, "Guids (hex) to act on. Default: the shared selection (devui.selection)."),
                new CommandArg("confirm", ArgType.String, "A confirmation code the daemon issued."),
            ],
            async (call, ct) => await call.Service<LiveActions>().RunActionAsync(
                call.GetString("template"), Targets(call), Point(call, required: true), call.OptionalObject("args"), call.OptionalString("confirm"), ct).ConfigureAwait(false)));

        registry.Register(new CommandDefinition(
            "action.on",
            "Run an entity action of the catalog on one or more live entities (kill, heal, respawn, set level, bot orders, AI pause, debug overlay, ...). Several targets need action.confirm with the code the answer carries.",
            [
                new CommandArg("template", ArgType.String, "The action id, for example 'kill'.", Required: true),
                new CommandArg("targets", ArgType.StringList, "Guids (hex) to act on, at most 50. Default: the shared selection (devui.selection)."),
                new CommandArg("guid", ArgType.String, "One guid (hex); same as targets with one item."),
                new CommandArg("args", ArgType.Object, "The action's arguments, for example {\"level\":10}."),
                new CommandArg("at", ArgType.Point3, "[x, y, z] for actions that take a destination."),
                new CommandArg("map", ArgType.Integer, "Map of the point. Default: the view's map."),
                new CommandArg("instance", ArgType.Integer, "Instance of the point (default 0)."),
                new CommandArg("o", ArgType.Number, "Facing in radians (default 0)."),
                new CommandArg("confirm", ArgType.String, "A confirmation code the daemon issued."),
            ],
            async (call, ct) => await call.Service<LiveActions>().RunActionAsync(
                call.GetString("template"), OnTargets(call), Point(call, required: false), call.OptionalObject("args"), call.OptionalString("confirm"), ct).ConfigureAwait(false)));

        registry.Register(new CommandDefinition(
            "action.confirm",
            "Resubmit the pending request that was answered confirm-required, with its code (valid 60 seconds, once).",
            [new CommandArg("code", ArgType.String, "The confirmation code from the answer.", Required: true)],
            async (call, ct) => await call.Service<LiveActions>().ConfirmAsync(call.GetString("code"), ct).ConfigureAwait(false)));

        registry.Register(new CommandDefinition(
            "action.log",
            "The actions this Atlas ran, newest last: time, actor, lines, outcome, replies, the daemon's old and new values, and whether undo is possible.",
            [new CommandArg("limit", ArgType.Integer, $"Most entries (default {DefaultLogLimit}, at most {MaxLogLimit}).", Default: JsonValue.Create(DefaultLogLimit))],
            (call, _) => Task.FromResult<JsonNode?>(Log(call))));

        registry.Register(new CommandDefinition(
            "action.undo",
            "Undo a logged action by running its inverse (delete a placed creature, move back, respawn after kill, release after take, resume after pause). Undo is itself an audited action. Refused when the action has no inverse.",
            [new CommandArg("id", ArgType.Integer, "The log id from action.log.", Required: true)],
            async (call, ct) => await call.Service<LiveActions>().UndoAsync(call.GetInt("id"), ct).ConfigureAwait(false)));

        registry.Register(new CommandDefinition(
            "devui.select",
            "Change the shared selection the dev UI and action.on use. set replaces, add and remove edit it, clear empties it. At most 50 guids.",
            [
                new CommandArg("guids", ArgType.StringList, "Guids (hex).", Default: new JsonArray()),
                new CommandArg("mode", ArgType.String, "set, add, remove or clear (default set).", Default: JsonValue.Create("set"), Choices: ["set", "add", "remove", "clear"]),
            ],
            (call, _) => Task.FromResult<JsonNode?>(Select(call))));

        registry.Register(new CommandDefinition(
            "devui.selection",
            "The shared selection: guids, count and a version that changes with every edit.",
            [],
            (call, _) => Task.FromResult<JsonNode?>(call.Service<LiveActions>().Selection.ToJson())));

        registry.Register(new CommandDefinition(
            "devui.character",
            "Set or read the operator's own character name, used by 'Teleport me here' and 'Bot: follow me'. It must be online in the live data when used. Empty name clears it.",
            [new CommandArg("name", ArgType.String, "The character name; omit to read it.")],
            (call, _) => Task.FromResult<JsonNode?>(Character(call))));
    }

    private static JsonObject Character(CommandInvocation call)
    {
        LiveActions actions = call.Service<LiveActions>();
        if (call.Has("name"))
        {
            string name = call.GetString("name").Trim();
            if (name.Length > 0 && !DevUiCatalog.IsPlainName(name))
            {
                throw Bad(call, $"argument 'name' must be 2 to {DevUiCatalog.MaxNameLength} letters.");
            }

            actions.Character = name;
        }

        return new JsonObject { ["character"] = actions.Character };
    }

    private static JsonObject Log(CommandInvocation call)
    {
        long limit = call.OptionalInt("limit") ?? DefaultLogLimit;
        if (limit is < 1 or > MaxLogLimit)
        {
            throw Bad(call, $"argument 'limit' must be between 1 and {MaxLogLimit}.");
        }

        IReadOnlyList<ActionEntry> entries = call.Service<LiveActions>().Log.Recent((int)limit);
        return new JsonObject { ["entries"] = new JsonArray([.. entries.Select(e => (JsonNode)e.ToJson())]), ["count"] = entries.Count };
    }

    private static JsonObject Select(CommandInvocation call)
    {
        string mode = call.OptionalString("mode") ?? "set";
        if (mode is not ("set" or "add" or "remove" or "clear"))
        {
            throw Bad(call, "argument 'mode' must be set, add, remove or clear.");
        }

        ulong[] guids = mode == "clear" ? [] : GuidList(call, "guids");
        LiveActions actions = call.Service<LiveActions>();
        actions.Selection.Apply(mode, guids);
        return actions.Selection.ToJson();
    }

    private static ulong[] Targets(CommandInvocation call) => GuidList(call, "targets");

    private static ulong[] OnTargets(CommandInvocation call)
    {
        ulong[] list = GuidList(call, "targets");
        if (LiveCommands.OptionalGuid(call, "guid") is { } one)
        {
            return list.Contains(one) ? list : [one, .. list];
        }

        return list;
    }

    private static ulong[] GuidList(CommandInvocation call, string name)
    {
        var guids = new List<ulong>();
        foreach (string text in call.GetStringList(name))
        {
            if (!LiveGuid.TryParse(text, out ulong guid) || guid == 0)
            {
                throw Bad(call, $"argument '{name}' holds '{text}', which is not a non-zero guid in hex.");
            }

            if (!guids.Contains(guid))
            {
                guids.Add(guid);
            }
        }

        return [.. guids];
    }

    private static ActionPoint? Point(CommandInvocation call, bool required)
    {
        WorldPoint? at = required ? call.GetPoint("at") : call.OptionalPoint("at");
        if (at is null)
        {
            return null;
        }

        long map = call.OptionalInt("map") ?? call.Service<LiveActions>().ViewMap;
        long instance = call.OptionalInt("instance") ?? 0;
        if (map is < 0 or > uint.MaxValue || instance is < 0 or > uint.MaxValue)
        {
            throw Bad(call, "arguments 'map' and 'instance' must be ids (0 or more).");
        }

        double facing = call.OptionalNumber("o") ?? 0;
        return new ActionPoint((uint)map, (uint)instance, at.Value.X, at.Value.Y, at.Value.Z, facing);
    }

    private static CommandException Bad(CommandInvocation call, string message)
        => new(CommandException.BadArguments, $"{call.Definition.Name}: {message}");
}
