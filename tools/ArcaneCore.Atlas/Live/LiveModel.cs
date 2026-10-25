using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Live;

/// <summary>What an entity of a map-feed frame is (the frame's <c>k</c> field).</summary>
public enum LiveKind
{
    /// <summary>A connected human player.</summary>
    Player,

    /// <summary>A managed bot (a player-shaped entity driven by the Playerbots pack).</summary>
    Bot,

    /// <summary>A creature spawn.</summary>
    Creature,

    /// <summary>A game object spawn.</summary>
    GameObject,
}

/// <summary>The wire names of <see cref="LiveKind"/>.</summary>
public static class LiveKinds
{
    // ref: D:\ArcaneCore-lanes\_wf\ATLAS-2026-10-18.md:217 ("k":"player|bot|creature|gameobject")
    private static readonly string[] WireNames = ["player", "bot", "creature", "gameobject"];

    /// <summary>All wire names, in <see cref="LiveKind"/> order.</summary>
    public static IReadOnlyList<string> Names => WireNames;

    /// <summary>The wire name of a kind.</summary>
    public static string Name(LiveKind kind)
        => (uint)kind < (uint)WireNames.Length ? WireNames[(int)kind] : throw new ArgumentOutOfRangeException(nameof(kind));

    /// <summary>Parses an exact (lower-case) wire name.</summary>
    public static bool TryParse(string? text, out LiveKind kind)
    {
        for (int i = 0; i < WireNames.Length; i++)
        {
            if (string.Equals(text, WireNames[i], StringComparison.Ordinal))
            {
                kind = (LiveKind)i;
                return true;
            }
        }

        kind = default;
        return false;
    }
}

/// <summary>
/// Object guids as the feed and the view write them. The feed's <c>g</c> is "a hex guid" (design section 9); the repository's own
/// text form is <c>0x</c> plus unpadded upper-case hex (src/ArcaneCore.Playerbots/Control/BotControlState.cs:23), while
/// <see cref="ViewState.FollowGuid"/> accepts bare hex digits only (Core/ViewState.cs, <c>IsGuid</c>). Both spellings are accepted
/// here and rewritten to the canonical text, bare unpadded upper-case hex, everywhere Atlas shows or compares a guid.
/// </summary>
public static class LiveGuid
{
    /// <summary>Parses 1 to 16 hex digits with an optional <c>0x</c> prefix. Zero is a valid parse (callers decide what it means).</summary>
    public static bool TryParse(string? text, out ulong guid)
    {
        guid = 0;
        if (text is null)
        {
            return false;
        }

        ReadOnlySpan<char> digits = text.AsSpan().Trim();
        if (digits.Length > 2 && digits[0] == '0' && (digits[1] == 'x' || digits[1] == 'X'))
        {
            digits = digits[2..];
        }

        return digits.Length is >= 1 and <= 16 && ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out guid);
    }

    /// <summary>The canonical text: bare, unpadded, upper-case hex.</summary>
    public static string Format(ulong guid) => guid.ToString("X", CultureInfo.InvariantCulture);
}

/// <summary>A creature movement spline as the server sent it (design section 9, <c>sp</c>).</summary>
/// <param name="Id">The spline id (0 when the server omitted it).</param>
/// <param name="Start">Where the spline began.</param>
/// <param name="Points">The points after <paramref name="Start"/>.</param>
/// <param name="StartClockMs">World clock at the start (<c>t0</c>).</param>
/// <param name="DurationMs">Total duration in milliseconds (<c>dur</c>).</param>
/// <param name="Run">True when the creature runs along it.</param>
public sealed record LiveSpline(uint Id, WorldPoint Start, IReadOnlyList<WorldPoint> Points, long StartClockMs, long DurationMs, bool Run)
{
    /// <summary>The spline as JSON, with the wire's field names.</summary>
    public JsonObject ToJson() => new()
    {
        ["id"] = Id,
        ["start"] = LiveJson.Point(Start),
        ["pts"] = LiveJson.Points(Points),
        ["t0"] = StartClockMs,
        ["dur"] = DurationMs,
        ["run"] = Run,
    };
}

/// <summary>A bot's navmesh route (design section 9, <c>route</c>).</summary>
/// <param name="Points">The route corners.</param>
/// <param name="Next">Index of the corner the bot is walking to.</param>
/// <param name="Flags">Server flags such as <c>meshEndedShort</c> (a partial route).</param>
/// <param name="Cut">True when the server shipped only the first corners of a longer route; <paramref name="Next"/> is then clamped to the shipped corners.</param>
public sealed record LiveRoute(IReadOnlyList<WorldPoint> Points, int Next, IReadOnlyList<string> Flags, bool Cut = false)
{
    /// <summary>The route as JSON, with the wire's field names.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["pts"] = LiveJson.Points(Points),
            ["next"] = Next,
            ["flags"] = new JsonArray([.. Flags.Select(f => (JsonNode?)JsonValue.Create(f))]),
        };
        if (Cut)
        {
            json["cut"] = true;
        }

        return json;
    }
}

/// <summary>One entity of a frame, validated: every coordinate is finite and within <see cref="FrameParser.MaxCoordinate"/>.</summary>
/// <param name="Id">The object guid (never zero).</param>
/// <param name="Kind">What it is.</param>
/// <param name="Entry">The creature, game object or player entry (0 when the server omitted it).</param>
/// <param name="Name">Display name, when the server sent one.</param>
/// <param name="Map">The map of the frame this entity came from.</param>
/// <param name="Instance">The instance of the frame this entity came from.</param>
/// <param name="Position">World position.</param>
/// <param name="Orientation">Facing in radians as the server reports it.</param>
/// <param name="HealthPercent">Health percentage, when the entity has health.</param>
/// <param name="Motion">The motion type text (<c>mt</c>), for example <c>idle</c> or <c>chase</c>.</param>
/// <param name="Target">The target guid, when it has one.</param>
/// <param name="Home">The creature's home point.</param>
/// <param name="Spline">The movement spline in progress.</param>
/// <param name="Route">The bot route in progress.</param>
/// <param name="Race">The player's race id (<c>r</c>, an optional feed field; the feed revision is unchanged); 0 when the server did not send one. Players and bots only.</param>
/// <param name="Class">The player's class id (<c>cl</c>, an optional feed field; the feed revision is unchanged); 0 when the server did not send one. Players and bots only.</param>
public sealed record LiveEntity(
    ulong Id,
    LiveKind Kind,
    uint Entry,
    string? Name,
    uint Map,
    uint Instance,
    WorldPoint Position,
    double Orientation,
    double? HealthPercent,
    string? Motion,
    ulong? Target,
    WorldPoint? Home,
    LiveSpline? Spline,
    LiveRoute? Route,
    byte Race = 0,
    byte Class = 0)
{
    /// <summary>The canonical guid text.</summary>
    public string GuidText => LiveGuid.Format(Id);

    /// <summary>The entity as the <c>live.entities</c> command reports it; <paramref name="trail"/> is added when given.</summary>
    public JsonObject ToJson(IReadOnlyList<WorldPoint>? trail = null)
    {
        var json = new JsonObject
        {
            ["guid"] = GuidText,
            ["kind"] = LiveKinds.Name(Kind),
            ["entry"] = Entry,
            ["name"] = Name,
            ["map"] = Map,
            ["instance"] = Instance,
            ["at"] = LiveJson.Point(Position),
            ["orientation"] = Orientation,
        };
        if (HealthPercent is { } hp)
        {
            json["hp"] = hp;
        }

        if (Motion is not null)
        {
            json["motion"] = Motion;
        }

        if (Target is { } target)
        {
            json["target"] = LiveGuid.Format(target);
        }

        if (Home is { } home)
        {
            json["home"] = LiveJson.Point(home);
        }

        if (Spline is not null)
        {
            json["spline"] = Spline.ToJson();
        }

        if (Route is not null)
        {
            json["route"] = Route.ToJson();
        }

        if (trail is not null)
        {
            json["trail"] = LiveJson.Points(trail);
        }

        return json;
    }
}

/// <summary>One capture of a map/instance as the server sent it (design section 9, <c>frame</c>).</summary>
/// <param name="Seq">Server frame sequence number (0 when omitted).</param>
/// <param name="ClockMs">World clock at capture, milliseconds.</param>
/// <param name="Map">Map id.</param>
/// <param name="Instance">Instance id (0 for continents).</param>
/// <param name="Grids">Loaded grids as (gx, gy) pairs on the 64 x 64 grid.</param>
/// <param name="Counters">The server's path/collision counters, passed through; null when absent.</param>
/// <param name="Entities">The validated entities.</param>
/// <param name="ReceivedTimestamp"><see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> when the frame was read.</param>
public sealed record LiveFrame(
    long Seq,
    long ClockMs,
    uint Map,
    uint Instance,
    IReadOnlyList<(int X, int Y)> Grids,
    JsonObject? Counters,
    IReadOnlyList<LiveEntity> Entities,
    long ReceivedTimestamp);

/// <summary>A map/instance the server listed in its hello reply.</summary>
/// <param name="Map">Map id.</param>
/// <param name="Instance">Instance id.</param>
/// <param name="Players">Players on it.</param>
/// <param name="Creatures">Creatures on it.</param>
public sealed record ServerMap(uint Map, uint Instance, int Players, int Creatures)
{
    /// <summary>The entry as JSON.</summary>
    public JsonObject ToJson() => new() { ["map"] = Map, ["instance"] = Instance, ["players"] = Players, ["creatures"] = Creatures };
}

/// <summary>What <c>live.subscribe</c> asked for; remembered so a reconnect can ask again.</summary>
/// <param name="Map">Map id.</param>
/// <param name="Instance">Instance id.</param>
/// <param name="Hz">Requested frame rate; the server clamps it to its own maximum.</param>
/// <param name="Kinds">Wire names of the entity kinds wanted.</param>
/// <param name="Paths">Whether splines and routes are wanted.</param>
public sealed record LiveSubscription(uint Map, uint Instance, double Hz, IReadOnlyList<string> Kinds, bool Paths)
{
    /// <summary>The <c>subscribe</c> message.</summary>
    public JsonObject ToMessage() => new()
    {
        ["op"] = LiveProtocol.OpSubscribe,
        ["map"] = Map,
        ["instance"] = Instance,
        ["hz"] = Hz,
        ["kinds"] = new JsonArray([.. Kinds.Select(k => (JsonNode?)JsonValue.Create(k))]),
        ["paths"] = Paths,
    };

    /// <summary>The subscription as JSON for <c>live.status</c>.</summary>
    public JsonObject ToJson() => new()
    {
        ["map"] = Map,
        ["instance"] = Instance,
        ["hz"] = Hz,
        ["kinds"] = new JsonArray([.. Kinds.Select(k => (JsonNode?)JsonValue.Create(k))]),
        ["paths"] = Paths,
    };
}

/// <summary>A server-side query (<c>live.query</c>).</summary>
/// <param name="Kind">One of <see cref="Kinds"/>.</param>
/// <param name="Map">Map id.</param>
/// <param name="Instance">Instance id.</param>
/// <param name="A">First point (the only one for height and undermap).</param>
/// <param name="B">Second point (line of sight and path).</param>
public sealed record LiveQuery(string Kind, uint Map, uint Instance, WorldPoint A, WorldPoint? B)
{
    /// <summary>The query kinds the protocol defines.</summary>
    // ref: D:\ArcaneCore-lanes\_wf\ATLAS-2026-10-18.md:220 ("kind":"los|height|path|undermap")
    public static IReadOnlyList<string> Kinds { get; } = ["los", "height", "path", "undermap"];

    /// <summary>Whether <paramref name="kind"/> needs the second point.</summary>
    public static bool NeedsSecondPoint(string kind) => kind is "los" or "path";

    /// <summary>The <c>query</c> message without its <c>id</c> (the connection assigns it).</summary>
    public JsonObject ToMessage()
    {
        var json = new JsonObject
        {
            ["op"] = LiveProtocol.OpQuery,
            ["kind"] = Kind,
            ["map"] = Map,
            ["instance"] = Instance,
            ["a"] = LiveJson.Point(A),
        };
        if (B is { } b)
        {
            json["b"] = LiveJson.Point(b);
        }

        return json;
    }
}

internal static class LiveJson
{
    public static JsonArray Point(WorldPoint p) => new(p.X, p.Y, p.Z);

    public static JsonArray Points(IReadOnlyList<WorldPoint> points)
    {
        var array = new JsonArray();
        foreach (WorldPoint p in points)
        {
            array.Add(Point(p));
        }

        return array;
    }
}
