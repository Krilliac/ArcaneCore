using System.Text.Json;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Live;

/// <summary>
/// The <c>live.*</c> commands. Each is a thin wrapper: it validates its arguments (BadArguments, before anything touches the pipe) and calls
/// <see cref="LiveFeed"/>. Any thread.
/// </summary>
internal static class LiveCommands
{
    /// <summary>Default <c>live.subscribe</c> rate (the design's example, section 9); the server clamps to its own maximum.</summary>
    public const double DefaultHz = 4;

    /// <summary>Highest rate this client will ask for.</summary>
    public const double MaxHz = 60;

    public const int DefaultEntityLimit = 100;
    public const int MaxEntityLimit = 5000;
    public const double DefaultRadius = 100;

    public const int DefaultFindLimit = 50;
    public const int MaxFindLimit = 500;
    public const float DefaultCellSize = 120f;
    public const float MinCellSize = 16f;
    public const float MaxCellSize = 2000f;
    public const int DefaultHotspotsLimit = 10;
    public const int MaxHotspotsLimit = 100;

    /// <summary>The map is 64 x 533.33 yd across, so no radius beyond its full extent can mean anything.</summary>
    // ref: src/ArcaneCore.Game/Maps/Grid/GridDefines.cs:22,25,55 (MaxNumberOfGrids = 64, SizeOfGrids = 533.33333, MapSize = their product, about 34 133 yd)
    public const double MaxRadius = 40_000;

    public static void Register(CommandRegistry registry, LiveFeed feed)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(feed);

        registry.Register(new CommandDefinition(
            "live.status",
            "Live map-feed state: connection, subscriptions, per-map frame age and sequence, entity counts, and counters of everything refused.",
            [],
            (_, _) => Task.FromResult<JsonNode?>(feed.Status())));

        registry.Register(new CommandDefinition(
            "live.connect",
            "Connect to a running world daemon's map feed (a same-user local named pipe). With no pid the only feed found is used. Replaces a current connection and re-sends its subscription. The daemon may close a connection that stays unsubscribed for a while; live.status then shows why.",
            [new CommandArg("pid", ArgType.Integer, "Process id of the world daemon. Required where feeds cannot be discovered (not Windows) or when several run.")],
            (call, ct) => ConnectAsync(feed, call, ct)));

        registry.Register(new CommandDefinition(
            "live.disconnect",
            "Tell the server to stop sending, close the connection and forget the subscriptions. Succeeds when not connected.",
            [],
            (_, ct) => DisconnectAsync(feed, ct)));

        registry.Register(new CommandDefinition(
            "live.subscribe",
            "Ask the connected feed for entity frames of one map and instance. A connection carries one subscription: this replaces the previous one, and the entities of the map it watched before are dropped. The server does not acknowledge; frames follow, and a refusal appears in live.status as lastServerError.",
            [
                new CommandArg("map", ArgType.Integer, "Map id.", Required: true),
                new CommandArg("instance", ArgType.Integer, "Instance id (0 for continents).", Default: JsonValue.Create(0)),
                new CommandArg("hz", ArgType.Number, $"Frames per second wanted, above 0 and up to {MaxHz:0}; the server clamps it to its own maximum.", Default: JsonValue.Create(DefaultHz)),
                new CommandArg("kinds", ArgType.StringList, $"Entity kinds wanted: any of {string.Join(", ", LiveKinds.Names)}. Default all."),
                new CommandArg("paths", ArgType.Boolean, "Also send creature splines and bot routes.", Default: JsonValue.Create(true)),
            ],
            (call, ct) => SubscribeAsync(feed, call, ct)));

        registry.Register(new CommandDefinition(
            "live.entities",
            "List entities from the latest frames. Filters combine; with near the result is nearest first, otherwise ordered by guid.",
            [
                new CommandArg("kind", ArgType.String, "Only this kind.", Choices: LiveKinds.Names),
                new CommandArg("near", ArgType.Point3, "[x, y, z] centre of a distance filter (straight-line yards). Applies to the view's map unless map is given."),
                // No schema default: a client that fills schema defaults into every call would send radius without near, which is refused.
                new CommandArg("radius", ArgType.Number, $"Radius in yards for near; only valid together with near (when omitted, {DefaultRadius:0}; at most {MaxRadius:0})."),
                new CommandArg("name", ArgType.String, "Only entities whose name contains this text, ignoring case."),
                new CommandArg("limit", ArgType.Integer, $"Most entities to return (default {DefaultEntityLimit}, at most {MaxEntityLimit}).", Default: JsonValue.Create(DefaultEntityLimit)),
                new CommandArg("map", ArgType.Integer, "Only this map."),
                new CommandArg("instance", ArgType.Integer, "Only this instance."),
                new CommandArg("guid", ArgType.String, "Only this guid, a quoted hex string."),
                new CommandArg("trails", ArgType.Boolean, $"Include each entity's last {LiveStore.TrailLength} distinct positions, oldest first.", Default: JsonValue.Create(false)),
            ],
            (call, _) => Task.FromResult<JsonNode?>(Entities(feed, call))));

        registry.Register(new CommandDefinition(
            "live.follow",
            "Follow an entity: sets the view's follow and moves the camera target to it with every frame that moves it. With no guid, stops following.",
            [new CommandArg("guid", ArgType.String, "Entity guid, a quoted hex string. Omit or null to stop following.")],
            (call, _) => Task.FromResult<JsonNode?>(Follow(feed, call))));

        registry.Register(new CommandDefinition(
            "live.query",
            "Ask the world daemon a question on its own loaded maps (it never loads tiles for this): line of sight, ground height, a path, or whether a point is under the map. Answers within 5 s or fails.",
            [
                new CommandArg("kind", ArgType.String, "What to ask: los and path need a and b; height and undermap need a only.", Required: true, Choices: LiveQuery.Kinds),
                new CommandArg("a", ArgType.Point3, "[x, y, z] first point.", Required: true),
                new CommandArg("b", ArgType.Point3, "[x, y, z] second point (los and path)."),
                new CommandArg("map", ArgType.Integer, "Map id. Default: the subscribed map, else the view's map."),
                new CommandArg("instance", ArgType.Integer, "Instance id. Default: the subscription's when it is for this map, else 0."),
            ],
            (call, ct) => QueryAsync(feed, call, ct)));

        registry.Register(new CommandDefinition(
            "live.find",
            "Search active entities in the live store by substring name, exact guid/entry, kind, map, and combat state.",
            [
                new CommandArg("map", ArgType.Integer, "Only this map."),
                new CommandArg("query", ArgType.String, "Text to match against entity name (substring, case-insensitive) or exact guid/entry."),
                new CommandArg("kind", ArgType.String, "Only this entity kind.", Choices: LiveKinds.Names),
                new CommandArg("inCombat", ArgType.Boolean, "Only entities in (true) or out of (false) combat."),
                new CommandArg("limit", ArgType.Integer, $"Most entities to return (1..{MaxFindLimit}, default {DefaultFindLimit}).", Default: JsonValue.Create(DefaultFindLimit)),
            ],
            (call, _) => Task.FromResult<JsonNode?>(Find(feed, call))));

        registry.Register(new CommandDefinition(
            "live.hotspots",
            "Bucket active entities into spatial cells and return the busiest combat and player clusters.",
            [
                new CommandArg("map", ArgType.Integer, "Only this map."),
                new CommandArg("cellSize", ArgType.Number, $"Spatial cell edge in yards (default {DefaultCellSize:0}, clamped to {MinCellSize:0}..{MaxCellSize:0}).", Default: JsonValue.Create((double)DefaultCellSize)),
                new CommandArg("limit", ArgType.Integer, $"Most hotspot cells to return (1..{MaxHotspotsLimit}, default {DefaultHotspotsLimit}).", Default: JsonValue.Create(DefaultHotspotsLimit)),
            ],
            (call, _) => Task.FromResult<JsonNode?>(Hotspots(feed, call))));

        PacketToolCommands.Register(registry, feed);
    }

    private static async Task<JsonNode?> ConnectAsync(LiveFeed feed, CommandInvocation call, CancellationToken cancellationToken)
    {
        long? pid = call.OptionalInt("pid");
        if (pid is < 1 or > int.MaxValue)
        {
            throw Bad(call, "argument 'pid' must be a process id (1 or more).");
        }

        return await feed.ConnectAsync((int?)pid, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonNode?> DisconnectAsync(LiveFeed feed, CancellationToken cancellationToken)
        => await feed.DisconnectAsync(cancellationToken).ConfigureAwait(false);

    private static async Task<JsonNode?> SubscribeAsync(LiveFeed feed, CommandInvocation call, CancellationToken cancellationToken)
    {
        uint map = call.GetMapId();
        uint instance = OptionalUInt(call, "instance") ?? 0;
        double hz = call.OptionalNumber("hz") ?? DefaultHz;
        if (!(hz > 0 && hz <= MaxHz))
        {
            throw Bad(call, $"argument 'hz' must be above 0 and at most {MaxHz:0}.");
        }

        var kinds = new List<string>();
        foreach (string kind in call.GetStringList("kinds"))
        {
            if (!LiveKinds.TryParse(kind, out _))
            {
                throw Bad(call, $"unknown kind '{kind}' (expected {string.Join(", ", LiveKinds.Names)}).");
            }

            if (!kinds.Contains(kind))
            {
                kinds.Add(kind);
            }
        }

        if (kinds.Count == 0)
        {
            kinds.AddRange(LiveKinds.Names);
        }

        var subscription = new LiveSubscription(map, instance, hz, kinds, call.GetBool("paths", true));
        return await feed.SubscribeAsync(subscription, cancellationToken).ConfigureAwait(false);
    }

    private static JsonObject Entities(LiveFeed feed, CommandInvocation call)
    {
        LiveKind? kind = null;
        if (call.OptionalString("kind") is { } kindText)
        {
            kind = LiveKinds.TryParse(kindText, out LiveKind parsed)
                ? parsed
                : throw Bad(call, $"argument 'kind' must be one of {string.Join(", ", LiveKinds.Names)}.");
        }

        WorldPoint? near = call.OptionalPoint("near");
        double? radius = call.OptionalNumber("radius");
        if (radius is not null && near is null)
        {
            throw Bad(call, "argument 'radius' needs 'near'.");
        }

        if (radius is { } r && !(r > 0 && r <= MaxRadius))
        {
            throw Bad(call, $"argument 'radius' must be above 0 and at most {MaxRadius:0}.");
        }

        long limit = call.OptionalInt("limit") ?? DefaultEntityLimit;
        if (limit is < 1 or > MaxEntityLimit)
        {
            throw Bad(call, $"argument 'limit' must be between 1 and {MaxEntityLimit}.");
        }

        uint? map = OptionalUInt(call, "map");
        if (near is not null && map is null)
        {
            map = feed.ViewMap;
        }

        var filter = new LiveEntityFilter(
            kind,
            map,
            OptionalUInt(call, "instance"),
            near,
            radius ?? DefaultRadius,
            call.OptionalString("name"),
            OptionalGuid(call, "guid"),
            (int)limit,
            call.GetBool("trails", false));
        return feed.ListEntities(filter);
    }

    private static JsonObject Follow(LiveFeed feed, CommandInvocation call)
        => feed.Follow(OptionalGuid(call, "guid"), Host.HostOrigin.Label(call.Origin));

    private static JsonObject Find(LiveFeed feed, CommandInvocation call)
    {
        uint? map = OptionalUInt(call, "map");
        string? query = OptionalQueryText(call, "query");
        LiveKind? kind = null;
        if (call.OptionalString("kind") is { } kindText)
        {
            kind = LiveKinds.TryParse(kindText, out LiveKind parsed)
                ? parsed
                : throw Bad(call, $"argument 'kind' must be one of {string.Join(", ", LiveKinds.Names)}.");
        }

        bool? inCombat = call.OptionalBool("inCombat");
        long limit = call.OptionalInt("limit") ?? DefaultFindLimit;
        if (limit is < 1 or > MaxFindLimit)
        {
            throw Bad(call, $"argument 'limit' must be between 1 and {MaxFindLimit}.");
        }

        LiveFindResult result = feed.Store.FindEntities(new LiveFindFilter(map, query, kind, inCombat, (int)limit));
        return result.ToJson(feed.IsConnected);
    }

    private static JsonObject Hotspots(LiveFeed feed, CommandInvocation call)
    {
        uint? map = OptionalUInt(call, "map");
        double rawCellSize = call.OptionalNumber("cellSize") ?? DefaultCellSize;
        if (!double.IsFinite(rawCellSize) || rawCellSize <= 0)
        {
            throw Bad(call, $"argument 'cellSize' must be a positive finite number (clamped to {MinCellSize:0}..{MaxCellSize:0}).");
        }

        float cellSize = Math.Clamp((float)rawCellSize, MinCellSize, MaxCellSize);
        long limit = call.OptionalInt("limit") ?? DefaultHotspotsLimit;
        if (limit is < 1 or > MaxHotspotsLimit)
        {
            throw Bad(call, $"argument 'limit' must be between 1 and {MaxHotspotsLimit}.");
        }

        IReadOnlyList<LiveHotspot> hotspots = feed.Store.Hotspots(map, cellSize, (int)limit);
        return new JsonObject
        {
            ["connected"] = feed.IsConnected,
            ["map"] = map is { } m ? JsonValue.Create(m) : null,
            ["cellSize"] = Math.Round((double)cellSize, 3),
            ["count"] = hotspots.Count,
            ["hotspots"] = new JsonArray([.. hotspots.Select(h => (JsonNode)h.ToJson())]),
        };
    }

    private static string? OptionalQueryText(CommandInvocation call, string name)
    {
        if (!call.Args.TryGetPropertyValue(name, out JsonNode? node) || node is null)
        {
            return null;
        }

        if (node is JsonValue v)
        {
            if (v.TryGetValue(out string? s))
            {
                return s;
            }

            if (JsonScalars.TryGetInt64(node, out long n) && n >= 0)
            {
                return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        throw Bad(call, $"argument '{name}' must be a string.");
    }

    private static async Task<JsonNode?> QueryAsync(LiveFeed feed, CommandInvocation call, CancellationToken cancellationToken)
    {
        string kind = call.GetString("kind");
        if (!LiveQuery.Kinds.Contains(kind, StringComparer.Ordinal))
        {
            throw Bad(call, $"argument 'kind' must be one of {string.Join(", ", LiveQuery.Kinds)}.");
        }

        WorldPoint a = call.GetPoint("a");
        WorldPoint? b = call.OptionalPoint("b");
        if (LiveQuery.NeedsSecondPoint(kind) && b is null)
        {
            throw Bad(call, $"a '{kind}' query needs argument 'b' as well as 'a'.");
        }

        if (!LiveQuery.NeedsSecondPoint(kind) && b is not null)
        {
            throw Bad(call, $"a '{kind}' query takes one point; remove argument 'b'.");
        }

        (uint map, uint instance) = feed.QueryTarget(OptionalUInt(call, "map"), OptionalUInt(call, "instance"));
        return await feed.QueryAsync(new LiveQuery(kind, map, instance, a, b), cancellationToken).ConfigureAwait(false);
    }

    private static uint? OptionalUInt(CommandInvocation call, string name)
    {
        long? value = call.OptionalInt(name);
        return value switch
        {
            null => null,
            >= 0 and <= uint.MaxValue => (uint)value,
            _ => throw Bad(call, $"argument '{name}' must be 0 or more."),
        };
    }

    /// <summary>
    /// A guid argument, read as a string through the invocation's getter: hex digits with an optional 0x prefix, never decimal. A JSON
    /// number is refused with a hint to quote it (its source text cannot be told from a decimal or exponent). Absent or null gives null.
    /// </summary>
    internal static ulong? OptionalGuid(CommandInvocation call, string name)
    {
        string? text;
        try
        {
            text = call.OptionalString(name);
        }
        catch (CommandException)
        {
            throw Bad(call, $"argument '{name}' must be a guid string in hex; quote it, for example \"1A2B\" (a bare number is refused because it would be ambiguous).");
        }

        if (text is null)
        {
            return null;
        }

        if (!LiveGuid.TryParse(text, out ulong guid) || guid == 0)
        {
            throw Bad(call, $"argument '{name}' must be a non-zero guid in hex, for example \"1A2B\" or \"0x1A2B\".");
        }

        return guid;
    }

    private static CommandException Bad(CommandInvocation call, string message)
        => new(CommandException.BadArguments, $"{call.Definition.Name}: {message}");
}
