using System.Numerics;
using System.Text.Json;
using ArcaneCore.Game.Maps.Grid;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>The operations a client may send.</summary>
internal enum FeedOp : byte
{
    Hello,
    Subscribe,
    Unsubscribe,
    Query,
}

/// <summary>The questions a <c>query</c> may ask.</summary>
internal enum QueryKind : byte
{
    LineOfSight,
    Height,
    Path,
    UnderMap,
}

/// <summary>A refusal: a stable machine code and a sentence for a person.</summary>
internal readonly record struct FeedError(string Code, string Message);

/// <summary>One parsed client line. Only the fields of its <see cref="Op"/> are meaningful.</summary>
internal struct FeedRequest
{
    public FeedOp Op;

    /// <summary>hello: the protocol version the client speaks.</summary>
    public long Protocol;

    /// <summary>subscribe, query: the map and its instance.</summary>
    public uint MapId;

    public uint InstanceId;

    /// <summary>subscribe: requested frames per second (0: not given).</summary>
    public double Hz;

    public FeedKinds Kinds;

    public bool Paths;

    /// <summary>query: the client's correlation id (<see cref="HasId"/>: it was given and readable, also when the rest was refused).</summary>
    public long Id;

    public bool HasId;

    public QueryKind Kind;

    public Vector3 A;

    public Vector3 B;
}

/// <summary>
/// The wire protocol of the map feed (docs/areas/map-feed.md): constants, error codes and the parser of client lines. Pure; any thread.
/// A line is one UTF-8 JSON object; unknown properties are ignored (a newer client may send more), a missing or mistyped property is
/// refused with <see cref="BadArgs"/>.
/// </summary>
internal static class MapFeedProtocol
{
    /// <summary>The protocol version this daemon speaks (the <c>protocol</c> of <c>hello</c>).</summary>
    public const int Version = 1;

    /// <summary>The longest client line in bytes; a longer one closes the connection.</summary>
    public const int MaxLineBytes = 4096;

    /// <summary>Consecutive refused lines after which the connection is closed.</summary>
    public const int MaxStrikes = 5;

    /// <summary>Deepest JSON nesting a client line may have.</summary>
    public const int MaxJsonDepth = 8;

    /// <summary>The most points a spline or route carries in a frame; a longer one is cut and flagged.</summary>
    public const int MaxPathPoints = 512;

    /// <summary>The most spline and route points one frame carries in all; once spent, further paths are left out and counted.</summary>
    public const int MaxFramePoints = 262_144;

    /// <summary>The most points a path answer carries.</summary>
    public const int MaxResultPoints = 1024;

    /// <summary>The line was not a JSON object.</summary>
    public const string BadJson = "bad-json";

    /// <summary>The line is longer than <see cref="MaxLineBytes"/> (the connection is closed).</summary>
    public const string LineTooLong = "line-too-long";

    /// <summary>The <c>op</c> is missing or not one the server knows.</summary>
    public const string UnknownOp = "unknown-op";

    /// <summary>Something other than <c>hello</c> was sent first.</summary>
    public const string HelloRequired = "hello-required";

    /// <summary>The client speaks another protocol version (the connection is closed).</summary>
    public const string ProtocolMismatch = "protocol";

    /// <summary>A property is missing, has the wrong type or is out of range.</summary>
    public const string BadArgs = "bad-args";

    /// <summary>Too many refused lines in a row (the connection is closed).</summary>
    public const string TooManyErrors = "too-many-errors";

    /// <summary>The connection was silent for too long while not subscribed (the connection is closed).</summary>
    public const string IdleTimeout = "idle-timeout";

    /// <summary>The subscribed map instance is not running; frames resume when it is. Sent once per absence.</summary>
    public const string MapNotLoaded = "map-not-loaded";

    /// <summary>A query needs a grid or terrain tile that is not loaded; nothing was loaded for it.</summary>
    public const string NotLoaded = "not-loaded";

    /// <summary>The world did not run a query in time.</summary>
    public const string Timeout = "timeout";

    /// <summary>The world is stopping.</summary>
    public const string WorldStopping = "world-stopping";

    /// <summary>A query failed inside the server; the details are in the server log.</summary>
    public const string QueryFailed = "query-failed";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = MaxJsonDepth,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    /// <summary>
    /// Parse one client line. False with <paramref name="error"/> set when it is refused; <paramref name="request"/> then still carries
    /// the <see cref="FeedRequest.Op"/> and query id when those were readable, so a refused query is answered as a result.
    /// </summary>
    public static bool TryParse(ReadOnlyMemory<byte> line, out FeedRequest request, out FeedError error)
    {
        request = default;
        error = default;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line, DocumentOptions);
        }
        catch (JsonException)
        {
            error = new FeedError(BadJson, "the line is not valid JSON");
            return false;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = new FeedError(BadJson, "the line must be a JSON object");
                return false;
            }

            if (!root.TryGetProperty("op"u8, out JsonElement op) || op.ValueKind != JsonValueKind.String)
            {
                error = new FeedError(UnknownOp, "the line has no \"op\" string");
                return false;
            }

            if (op.ValueEquals("hello"u8))
            {
                request.Op = FeedOp.Hello;
                return ParseHello(root, ref request, out error);
            }

            if (op.ValueEquals("subscribe"u8))
            {
                request.Op = FeedOp.Subscribe;
                return ParseSubscribe(root, ref request, out error);
            }

            if (op.ValueEquals("unsubscribe"u8))
            {
                request.Op = FeedOp.Unsubscribe;
                return true;
            }

            if (op.ValueEquals("query"u8))
            {
                request.Op = FeedOp.Query;
                return ParseQuery(root, ref request, out error);
            }

            error = new FeedError(UnknownOp, "unknown op; the ops are hello, subscribe, unsubscribe and query");
            return false;
        }
    }

    private static bool ParseHello(JsonElement root, ref FeedRequest request, out FeedError error)
    {
        error = default;
        if (!root.TryGetProperty("protocol"u8, out JsonElement protocol) || protocol.ValueKind != JsonValueKind.Number || !protocol.TryGetInt64(out long version))
        {
            error = new FeedError(BadArgs, "hello needs an integer \"protocol\"");
            return false;
        }

        request.Protocol = version;
        return true;
    }

    private static bool ParseSubscribe(JsonElement root, ref FeedRequest request, out FeedError error)
    {
        error = default;
        if (!TryMap(root, ref request, out error))
        {
            return false;
        }

        request.Hz = 0;
        if (root.TryGetProperty("hz"u8, out JsonElement hz))
        {
            if (hz.ValueKind != JsonValueKind.Number || !hz.TryGetDouble(out double rate) || !double.IsFinite(rate) || rate <= 0)
            {
                error = new FeedError(BadArgs, "\"hz\" must be a number above 0");
                return false;
            }

            request.Hz = rate;
        }

        request.Kinds = FeedKinds.All;
        if (root.TryGetProperty("kinds"u8, out JsonElement kinds))
        {
            if (kinds.ValueKind != JsonValueKind.Array)
            {
                error = new FeedError(BadArgs, "\"kinds\" must be an array of player, bot, creature and gameobject");
                return false;
            }

            FeedKinds chosen = FeedKinds.None;
            foreach (JsonElement kind in kinds.EnumerateArray())
            {
                if (kind.ValueKind == JsonValueKind.String && kind.ValueEquals("player"u8))
                {
                    chosen |= FeedKinds.Player;
                }
                else if (kind.ValueKind == JsonValueKind.String && kind.ValueEquals("bot"u8))
                {
                    chosen |= FeedKinds.Bot;
                }
                else if (kind.ValueKind == JsonValueKind.String && kind.ValueEquals("creature"u8))
                {
                    chosen |= FeedKinds.Creature;
                }
                else if (kind.ValueKind == JsonValueKind.String && kind.ValueEquals("gameobject"u8))
                {
                    chosen |= FeedKinds.GameObject;
                }
                else
                {
                    error = new FeedError(BadArgs, "\"kinds\" may hold player, bot, creature and gameobject only");
                    return false;
                }
            }

            request.Kinds = chosen;
        }

        request.Paths = true;
        if (root.TryGetProperty("paths"u8, out JsonElement paths))
        {
            if (paths.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                error = new FeedError(BadArgs, "\"paths\" must be true or false");
                return false;
            }

            request.Paths = paths.ValueKind == JsonValueKind.True;
        }

        return true;
    }

    private static bool ParseQuery(JsonElement root, ref FeedRequest request, out FeedError error)
    {
        error = default;
        if (root.TryGetProperty("id"u8, out JsonElement id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long value))
        {
            request.Id = value;
            request.HasId = true;
        }
        else
        {
            error = new FeedError(BadArgs, "a query needs an integer \"id\"");
            return false;
        }

        if (!root.TryGetProperty("kind"u8, out JsonElement kind) || kind.ValueKind != JsonValueKind.String)
        {
            error = new FeedError(BadArgs, "a query needs a \"kind\": los, height, path or undermap");
            return false;
        }

        if (kind.ValueEquals("los"u8))
        {
            request.Kind = QueryKind.LineOfSight;
        }
        else if (kind.ValueEquals("height"u8))
        {
            request.Kind = QueryKind.Height;
        }
        else if (kind.ValueEquals("path"u8))
        {
            request.Kind = QueryKind.Path;
        }
        else if (kind.ValueEquals("undermap"u8))
        {
            request.Kind = QueryKind.UnderMap;
        }
        else
        {
            error = new FeedError(BadArgs, "unknown query kind; the kinds are los, height, path and undermap");
            return false;
        }

        if (!TryMap(root, ref request, out error))
        {
            return false;
        }

        if (!TryPoint(root, "a"u8, "a", out request.A, out error))
        {
            return false;
        }

        if (request.Kind is QueryKind.LineOfSight or QueryKind.Path)
        {
            return TryPoint(root, "b"u8, "b", out request.B, out error);
        }

        return true;
    }

    private static bool TryMap(JsonElement root, ref FeedRequest request, out FeedError error)
    {
        error = default;
        if (!root.TryGetProperty("map"u8, out JsonElement map) || map.ValueKind != JsonValueKind.Number || !map.TryGetUInt32(out uint mapId))
        {
            error = new FeedError(BadArgs, "\"map\" must be a map id (a non-negative integer)");
            return false;
        }

        uint instanceId = 0;
        if (root.TryGetProperty("instance"u8, out JsonElement instance)
            && (instance.ValueKind != JsonValueKind.Number || !instance.TryGetUInt32(out instanceId)))
        {
            error = new FeedError(BadArgs, "\"instance\" must be an instance id (a non-negative integer)");
            return false;
        }

        request.MapId = mapId;
        request.InstanceId = instanceId;
        return true;
    }

    /// <summary>A point <c>[x, y, z]</c> of finite numbers inside the map: the collision code is never given anything else.</summary>
    private static bool TryPoint(JsonElement root, ReadOnlySpan<byte> utf8Name, string name, out Vector3 point, out FeedError error)
    {
        point = default;
        error = default;
        if (!root.TryGetProperty(utf8Name, out JsonElement element)
            || element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() != 3)
        {
            error = new FeedError(BadArgs, $"\"{name}\" must be [x, y, z]");
            return false;
        }

        Span<float> parts = stackalloc float[3];
        int index = 0;
        foreach (JsonElement part in element.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Number || !part.TryGetDouble(out double number) || !float.IsFinite((float)number))
            {
                error = new FeedError(BadArgs, $"\"{name}\" must hold three finite numbers");
                return false;
            }

            parts[index++] = (float)number;
        }

        if (!GridDefines.IsValidMapCoord(parts[0]) || !GridDefines.IsValidMapCoord(parts[1]) || !GridDefines.IsValidZCoord(parts[2]))
        {
            error = new FeedError(BadArgs, $"\"{name}\" lies outside the map");
            return false;
        }

        point = new Vector3(parts[0], parts[1], parts[2]);
        return true;
    }
}
