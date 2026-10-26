using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Host;

/// <summary>
/// <c>session.join</c>, <c>session.move</c>, <c>session.leave</c> and <c>sessions.list</c>: sessions without a browser, for
/// scripts and agents. An agent session is held by the <see cref="UiHub"/> beside the UI connections and shows up to every UI as
/// a marker (<c>ui.presence</c>, <c>ui.presence.left</c>, the hello <c>sessions</c> list, <c>status.ui.sessions</c>). It does not
/// own a camera or change the shared view. It expires after <see cref="AtlasServerOptions.AgentSessionTimeout"/> without a join
/// or move. The commands need a running <c>serve</c> instance. Nothing they return contains the instance token.
/// </summary>
internal static class SessionCommands
{
    public static void Register(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(new CommandDefinition(
            "session.join",
            "Joins as an agent session that other users see as a labelled marker, and returns its id. Give the position now or with session.move; the session expires after 10 minutes without a join or move.",
            [
                new("name", ArgType.String, "Display name (trimmed, control characters removed, at most 32 characters; empty means the default 'session xxxx').", Required: true),
                new("map", ArgType.Integer, "Map id (default 0)."),
                new("at", ArgType.Point3, "Position as [x, y, z] (or \"x,y,z\"), each within +-100000 (default 0,0,0)."),
                new("yaw", ArgType.Number, "Heading in degrees (default 0)."),
                new("mode", ArgType.String, "Camera mode shown to others (default orbit).", Choices: ["orbit", "fly", "top"]),
            ],
            Join,
            CommandSurfaces.Instance));
        registry.Register(new CommandDefinition(
            "session.move",
            "Moves an agent session; fields not given keep their values. Moves must be at least 50 ms apart, otherwise the command fails with 'unavailable' and says when to retry.",
            [
                new("id", ArgType.String, "Session id from session.join.", Required: true),
                new("map", ArgType.Integer, "Map id."),
                new("at", ArgType.Point3, "Position as [x, y, z] (or \"x,y,z\"), each within +-100000."),
                new("yaw", ArgType.Number, "Heading in degrees."),
                new("mode", ArgType.String, "Camera mode shown to others.", Choices: ["orbit", "fly", "top"]),
            ],
            Move,
            CommandSurfaces.Instance));
        registry.Register(new CommandDefinition(
            "session.leave",
            "Ends an agent session; its marker disappears for everyone.",
            [new("id", ArgType.String, "Session id from session.join.", Required: true)],
            Leave,
            CommandSurfaces.Instance));
        registry.Register(new CommandDefinition(
            "sessions.list",
            "Every session that has published a position: {sessions:[{id,name,map,at,yaw,mode,kind}]}, kind 'ui' for a browser and 'agent' for session.join.",
            [],
            List,
            CommandSurfaces.Instance));
    }

    private static UiHub Hub(CommandInvocation invocation)
    {
        UiHub? hub = invocation.Services.GetService(typeof(UiHub)) as UiHub;
        if (invocation.Headless || hub is not { Attached: true })
        {
            throw new CommandException(CommandException.Unavailable, $"{invocation.Definition.Name} needs a running 'arcane-atlas serve' instance (attach with ctl, script --attach or mcp --attach).");
        }

        return hub;
    }

    /// <summary>The optional position fields of a call as the JSON the shared presence reader validates.</summary>
    private static JsonObject Fields(CommandInvocation invocation)
    {
        var fields = new JsonObject();
        if (invocation.OptionalInt("map") is { } map)
        {
            fields["map"] = map;
        }

        if (invocation.OptionalPoint("at") is { } at)
        {
            fields["at"] = new JsonArray(at.X, at.Y, at.Z);
        }

        if (invocation.OptionalNumber("yaw") is { } yaw)
        {
            fields["yaw"] = yaw;
        }

        if (invocation.OptionalString("mode") is { } mode)
        {
            fields["mode"] = mode;
        }

        return fields;
    }

    private static UiPresence Read(CommandInvocation invocation, JsonObject fields, UiPresence? basis, string id, string name)
        => UiPresence.TryRead(fields, basis, id, name, UiPresence.KindAgent, out UiPresence? presence, out string? problem)
            ? presence
            : throw new CommandException(CommandException.BadArguments, $"{invocation.Definition.Name}: {problem}.");

    private static Task<JsonNode?> Join(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        UiHub hub = Hub(invocation);
        if (!invocation.Args.ContainsKey("name"))
        {
            throw new CommandException(CommandException.BadArguments, "session.join: argument 'name' is required (an empty string picks the default name).");
        }

        string rawName = ArgReader.Text(invocation, "name") ?? string.Empty;
        JsonObject fields = Fields(invocation);
        UiPresence joined = hub.JoinAgent((id, defaultName) =>
        {
            string name = UiPresence.CleanName(rawName) ?? defaultName;
            var start = new UiPresence(id, name, 0, 0, 0, 0, 0, "orbit", UiPresence.KindAgent);
            return Read(invocation, fields, start, id, name);
        });
        return Task.FromResult<JsonNode?>(new JsonObject { ["id"] = joined.Id, ["name"] = joined.Name });
    }

    private static Task<JsonNode?> Move(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        UiHub hub = Hub(invocation);
        string id = ArgReader.RequiredText(invocation, "id");
        JsonObject fields = Fields(invocation);
        UiPresence moved = hub.MoveAgent(id, (current, _) => Read(invocation, fields, current, id, current.Name));
        return Task.FromResult<JsonNode?>(moved.ToJson());
    }

    private static Task<JsonNode?> Leave(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        UiHub hub = Hub(invocation);
        string id = ArgReader.RequiredText(invocation, "id");
        hub.LeaveAgent(id);
        return Task.FromResult<JsonNode?>(new JsonObject { ["id"] = id, ["left"] = true });
    }

    private static Task<JsonNode?> List(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        UiHub hub = Hub(invocation);
        return Task.FromResult<JsonNode?>(new JsonObject { ["sessions"] = new JsonArray([.. hub.PresenceSnapshot().Select(p => (JsonNode)p.ToJson())]) });
    }
}
