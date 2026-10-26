using System.Globalization;
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

    /// <summary>Packet tools: run one console line.</summary>
    Console,

    /// <summary>Packet tools: hand a client opcode to an online session's dispatcher.</summary>
    Inject,

    /// <summary>Packet tools: queue a server opcode to an online session's client.</summary>
    Send,

    /// <summary>Packet tools: start watching a session's packets.</summary>
    Tap,

    /// <summary>Packet tools: stop the connection's tap.</summary>
    Untap,

    /// <summary>An op an extension declared (<see cref="MapFeedExtensions.IsDeclared"/>: <c>role</c>, <c>action</c>, <c>caps</c>); the extension parses the line.</summary>
    Extension,
}

/// <summary>Which packets a tap passes.</summary>
[Flags]
internal enum TapDirections : byte
{
    None = 0,

    /// <summary>Client to server, injected packets included.</summary>
    In = 1,

    /// <summary>Server to client.</summary>
    Out = 2,

    Both = In | Out,
}

/// <summary>The questions a <c>query</c> may ask.</summary>
internal enum QueryKind : byte
{
    LineOfSight,
    Height,
    Path,
    UnderMap,

    /// <summary>Live insight: everything the server knows about one unit (docs/areas/atlas-insight.md).</summary>
    Inspect,

    /// <summary>Live insight: the anomalies the ledger reported for this world since a sequence number.</summary>
    Anomalies,
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

    /// <summary>console: the line to run.</summary>
    public string? Line;

    /// <summary>inject, send, tap: the target's character name (null when <see cref="TargetGuid"/> names it).</summary>
    public string? TargetName;

    /// <summary>inject, send, tap: the target's player guid (0 when <see cref="TargetName"/> names it).</summary>
    public ulong TargetGuid;

    /// <summary>inject, send: the opcode value.</summary>
    public uint Opcode;

    /// <summary>inject, send: the payload bytes (empty for none).</summary>
    public byte[]? Payload;

    /// <summary>tap: which directions pass.</summary>
    public TapDirections Directions;

    /// <summary>tap: only these opcodes pass (null: all).</summary>
    public ushort[]? TapOpcodes;

    /// <summary>tap: the tap ends after this many packets.</summary>
    public int MaxPackets;

    /// <summary>tap: the tap ends after this many seconds.</summary>
    public int Seconds;

    /// <summary>tap: the most payload bytes each packet line carries as hex.</summary>
    public int MaxBytes;

    /// <summary>extension: the op name.</summary>
    public string? ExtensionOp;

    /// <summary>extension: the whole line, valid until the connection reads the next one (the extension parses it itself).</summary>
    public ReadOnlyMemory<byte> ExtensionLine;

    /// <summary>inspect: the object guid to describe.</summary>
    public ulong InspectGuid;

    /// <summary>anomalies: only notices with a sequence number above this are returned (0: from the oldest the ring holds).</summary>
    public long Since;

    /// <summary>anomalies: the most notices returned.</summary>
    public int MaxResults;
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

    /// <summary>
    /// The feed revision (the <c>rev</c> of <c>hello</c>): 1 is the original shape, 2 adds <c>cb</c> (in combat, written only when true)
    /// and <c>d</c> (display id, written when non-zero) to unit entities. Additive, so a consumer that predates it ignores both. Player and bot entities also carry
    /// <c>r</c> (race id) and <c>cl</c> (class id), each written when non-zero; they are optional extras that did not change the revision, so a consumer
    /// treats their absence as unknown.
    /// </summary>
    public const int Revision = 2;

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

    /// <summary>The longest client line while the packet tools are on: an injected payload of the largest client packet is 20 472 hex digits.</summary>
    public const int MaxToolLineBytes = 64 * 1024;

    /// <summary>The longest console line a tool may send (the control pipe's default line limit).</summary>
    public const int MaxConsoleLineChars = 4096;

    /// <summary>The most opcodes one tap filter may list.</summary>
    public const int MaxTapOpcodes = 64;

    /// <summary>The default and the largest number of packets one tap passes.</summary>
    public const int DefaultTapPackets = 1000;

    public const int MaxTapPackets = 100_000;

    /// <summary>The default and the longest life of a tap, in seconds.</summary>
    public const int DefaultTapSeconds = 60;

    public const int MaxTapSeconds = 3600;

    /// <summary>The default and the largest number of payload bytes a <c>packet</c> line carries as hex (the rest is cut and flagged).</summary>
    public const int DefaultTapBytes = 256;

    public const int MaxTapBytes = 4096;

    /// <summary>The packet tools are off (World:Control:PacketTools), or refused in this environment.</summary>
    public const string ToolsOff = "tools-off";

    /// <summary>No online character has that name or guid.</summary>
    public const string Offline = "offline";

    /// <summary>The value names no opcode of the build-5875 table.</summary>
    public const string UnknownOpcode = "unknown-opcode";

    /// <summary>A server opcode given to inject, or a client opcode given to send.</summary>
    public const string WrongDirection = "wrong-direction";

    /// <summary>An authentication or session-lifecycle opcode, which the tools never inject or send.</summary>
    public const string LifecycleOpcode = "lifecycle-opcode";

    /// <summary>The payload is longer than the tools accept for that direction.</summary>
    public const string Oversize = "oversize";

    /// <summary>The server has no handler for the opcode.</summary>
    public const string NoHandler = "no-handler";

    /// <summary>The opcode's handler does not run in the session's current state.</summary>
    public const string WrongState = "wrong-state";

    /// <summary>The session cannot take an injection of that opcode (a playerbot and a session-task opcode, or a session older than the tools).</summary>
    public const string NotInjectable = "not-injectable";

    /// <summary>The session's dispatcher stayed busy.</summary>
    public const string Busy = "busy";

    /// <summary>The most notices one <c>anomalies</c> answer carries (the ring's capacity).</summary>
    public const int MaxAnomalyResults = 4096;

    /// <summary>The notices an <c>anomalies</c> query returns when it gives no <c>max</c>.</summary>
    public const int DefaultAnomalyResults = 256;

    /// <summary>The most threat entries an <c>inspect</c> answer lists.</summary>
    public const int MaxInspectThreat = 10;

    /// <summary>An <c>inspect</c> names no object this world holds on that map (nothing is loaded to look for it).</summary>
    public const string NotFound = "not-found";

    /// <summary>The connection already has a tap.</summary>
    public const string TapBusy = "tap-busy";

    /// <summary>The connection has no tap to stop.</summary>
    public const string NoTap = "no-tap";

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
        try
        {
            using JsonDocument document = JsonDocument.Parse(line, DocumentOptions);
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

            if (TryToolOp(op, out FeedOp toolOp))
            {
                request.Op = toolOp;
                return ParseTool(root, ref request, out error);
            }

            if (op.GetString() is { } declared && MapFeedExtensions.IsDeclared(declared))
            {
                request.Op = FeedOp.Extension;
                request.ExtensionOp = declared;
                request.ExtensionLine = line;
                return true;
            }

            error = new FeedError(UnknownOp, "unknown op; the ops are hello, subscribe, unsubscribe and query (and console, inject, send, tap and untap with the packet tools)");
            return false;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            request = default;
            error = new FeedError(BadJson, "the line is not valid JSON");
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
            error = new FeedError(BadArgs, "a query needs a \"kind\": los, height, path, undermap, inspect or anomalies");
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
        else if (kind.ValueEquals("inspect"u8))
        {
            request.Kind = QueryKind.Inspect;
        }
        else if (kind.ValueEquals("anomalies"u8))
        {
            request.Kind = QueryKind.Anomalies;
        }
        else
        {
            error = new FeedError(BadArgs, "unknown query kind; the kinds are los, height, path, undermap, inspect and anomalies");
            return false;
        }

        if (request.Kind == QueryKind.Anomalies)
        {
            return TryAnomalyArgs(root, ref request, out error);
        }

        if (!TryMap(root, ref request, out error))
        {
            return false;
        }

        if (request.Kind == QueryKind.Inspect)
        {
            return TryGuid(root, out request.InspectGuid, out error);
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

    /// <summary><c>since</c> (a sequence number, default 0) and <c>max</c> (1 to <see cref="MaxAnomalyResults"/>, default <see cref="DefaultAnomalyResults"/>) of an anomalies query.</summary>
    private static bool TryAnomalyArgs(JsonElement root, ref FeedRequest request, out FeedError error)
    {
        error = default;
        request.Since = 0;
        request.MaxResults = DefaultAnomalyResults;
        if (root.TryGetProperty("since"u8, out JsonElement since))
        {
            if (since.ValueKind != JsonValueKind.Number || !since.TryGetInt64(out long sinceValue) || sinceValue < 0)
            {
                error = new FeedError(BadArgs, "\"since\" must be a sequence number (a non-negative integer)");
                return false;
            }

            request.Since = sinceValue;
        }

        if (root.TryGetProperty("max"u8, out JsonElement max))
        {
            if (max.ValueKind != JsonValueKind.Number || !max.TryGetInt32(out int maxValue) || maxValue < 1 || maxValue > MaxAnomalyResults)
            {
                error = new FeedError(BadArgs, string.Create(CultureInfo.InvariantCulture, $"\"max\" must be 1 to {MaxAnomalyResults}"));
                return false;
            }

            request.MaxResults = maxValue;
        }

        return true;
    }

    /// <summary>A <c>guid</c> of an inspect query: 1 to 16 hex digits (an optional 0x), not zero.</summary>
    private static bool TryGuid(JsonElement root, out ulong guid, out FeedError error)
    {
        guid = 0;
        error = default;
        if (root.TryGetProperty("guid"u8, out JsonElement element) && element.ValueKind == JsonValueKind.String)
        {
            string? text = element.GetString();
            if (text is not null && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text[2..];
            }

            if (!string.IsNullOrEmpty(text) && text.Length <= 16
                && ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out guid) && guid != 0)
            {
                return true;
            }
        }

        guid = 0;
        error = new FeedError(BadArgs, "\"guid\" must be a non-zero GUID as 1 to 16 hex digits");
        return false;
    }

    private static bool TryToolOp(JsonElement op, out FeedOp toolOp)
    {
        toolOp = op.ValueEquals("console"u8) ? FeedOp.Console
            : op.ValueEquals("inject"u8) ? FeedOp.Inject
            : op.ValueEquals("send"u8) ? FeedOp.Send
            : op.ValueEquals("tap"u8) ? FeedOp.Tap
            : op.ValueEquals("untap"u8) ? FeedOp.Untap
            : FeedOp.Hello;
        return toolOp != FeedOp.Hello;
    }

    /// <summary>
    /// A packet-tools line (docs/areas/map-feed.md, "Packet tools"): every one carries an integer <c>id</c>, answered by a <c>result</c> of
    /// the same id. Whether the tools are on is decided by the connection, not here.
    /// </summary>
    private static bool ParseTool(JsonElement root, ref FeedRequest request, out FeedError error)
    {
        error = default;
        if (root.TryGetProperty("id"u8, out JsonElement id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long value))
        {
            request.Id = value;
            request.HasId = true;
        }
        else
        {
            error = new FeedError(BadArgs, "a packet-tools line needs an integer \"id\"");
            return false;
        }

        switch (request.Op)
        {
            case FeedOp.Console:
                if (!root.TryGetProperty("line"u8, out JsonElement line) || line.ValueKind != JsonValueKind.String
                    || line.GetString() is not { Length: > 0 and <= MaxConsoleLineChars } text || string.IsNullOrWhiteSpace(text))
                {
                    error = new FeedError(BadArgs, $"console needs a \"line\": a command of 1 to {MaxConsoleLineChars} characters");
                    return false;
                }

                request.Line = text;
                return true;
            case FeedOp.Inject:
            case FeedOp.Send:
                return TryTarget(root, ref request, out error)
                    && TryOpcode(root, out request.Opcode, out error)
                    && TryPayload(root, out request.Payload, out error);
            case FeedOp.Tap:
                return TryTarget(root, ref request, out error) && TryTapFilter(root, ref request, out error);
            default:
                return true;
        }
    }

    /// <summary>Exactly one of <c>target</c> (a character name) and <c>guid</c> (the player guid as a hex string).</summary>
    private static bool TryTarget(JsonElement root, ref FeedRequest request, out FeedError error)
    {
        error = default;
        bool hasName = root.TryGetProperty("target"u8, out JsonElement name);
        bool hasGuid = root.TryGetProperty("guid"u8, out JsonElement guid);
        if (hasName == hasGuid)
        {
            error = new FeedError(BadArgs, "name the session with exactly one of \"target\" (a character name) and \"guid\" (its player guid in hex)");
            return false;
        }

        if (hasName)
        {
            if (name.ValueKind != JsonValueKind.String || name.GetString() is not { Length: > 0 and <= 64 } text || string.IsNullOrWhiteSpace(text))
            {
                error = new FeedError(BadArgs, "\"target\" must be a character name");
                return false;
            }

            request.TargetName = text.Trim();
            return true;
        }

        string? hex = guid.ValueKind == JsonValueKind.String ? guid.GetString() : null;
        if (hex is not null && hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[2..];
        }

        if (hex is not { Length: > 0 and <= 16 }
            || !ulong.TryParse(hex, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out ulong value)
            || value == 0)
        {
            error = new FeedError(BadArgs, "\"guid\" must be a non-zero player guid as a hex string, for example \"0000000000000002\"");
            return false;
        }

        request.TargetGuid = value;
        return true;
    }

    /// <summary>An <c>opcode</c>: a number 0 to 65535 or a reference name (<c>CMSG_QUERY_TIME</c>); whether it is a known opcode is the tools' check.</summary>
    private static bool TryOpcode(JsonElement root, out uint opcode, out FeedError error)
    {
        opcode = 0;
        error = default;
        if (!root.TryGetProperty("opcode"u8, out JsonElement element))
        {
            error = new FeedError(BadArgs, "an \"opcode\" is needed: a number or a name such as CMSG_QUERY_TIME");
            return false;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetUInt32(out uint number) && number <= ushort.MaxValue)
        {
            opcode = number;
            return true;
        }

        if (element.ValueKind == JsonValueKind.String && element.GetString() is { } text)
        {
            if (PacketToolOpcodes.TryParse(text, out ushort parsed))
            {
                opcode = parsed;
                return true;
            }

            error = new FeedError(UnknownOpcode, $"'{(text.Length > 64 ? text[..64] : text)}' is not an opcode of the build-5875 table");
            return false;
        }

        error = new FeedError(BadArgs, "\"opcode\" must be a number from 0 to 65535 or an opcode name");
        return false;
    }

    /// <summary>A <c>payload</c> of hex digits (two per byte, no separators); absent or empty for none.</summary>
    private static bool TryPayload(JsonElement root, out byte[] payload, out FeedError error)
    {
        payload = [];
        error = default;
        if (!root.TryGetProperty("payload"u8, out JsonElement element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } hex)
        {
            error = new FeedError(BadArgs, "\"payload\" must be a string of hex digits");
            return false;
        }

        try
        {
            payload = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            error = new FeedError(BadArgs, "\"payload\" must be an even number of hex digits with nothing between them");
            return false;
        }
    }

    private static bool TryTapFilter(JsonElement root, ref FeedRequest request, out FeedError error)
    {
        error = default;
        request.Directions = TapDirections.Both;
        if (root.TryGetProperty("dir"u8, out JsonElement dir))
        {
            request.Directions = dir.ValueKind != JsonValueKind.String ? TapDirections.None
                : dir.ValueEquals("in"u8) ? TapDirections.In
                : dir.ValueEquals("out"u8) ? TapDirections.Out
                : dir.ValueEquals("both"u8) ? TapDirections.Both
                : TapDirections.None;
            if (request.Directions == TapDirections.None)
            {
                error = new FeedError(BadArgs, "\"dir\" must be in, out or both");
                return false;
            }
        }

        if (root.TryGetProperty("opcodes"u8, out JsonElement opcodes))
        {
            if (opcodes.ValueKind != JsonValueKind.Array || opcodes.GetArrayLength() is 0 or > MaxTapOpcodes)
            {
                error = new FeedError(BadArgs, $"\"opcodes\" must be an array of 1 to {MaxTapOpcodes} opcodes (numbers or names)");
                return false;
            }

            var list = new List<ushort>(opcodes.GetArrayLength());
            foreach (JsonElement item in opcodes.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetUInt16(out ushort number))
                {
                    list.Add(number);
                }
                else if (item.ValueKind == JsonValueKind.String && item.GetString() is { } name && PacketToolOpcodes.TryParse(name, out ushort parsed))
                {
                    list.Add(parsed);
                }
                else
                {
                    error = new FeedError(UnknownOpcode, "every entry of \"opcodes\" must be a number from 0 to 65535 or an opcode name of the build-5875 table");
                    return false;
                }
            }

            request.TapOpcodes = [.. list.Distinct()];
        }

        return TryBounded(root, "maxPackets"u8, "maxPackets", 1, MaxTapPackets, DefaultTapPackets, out request.MaxPackets, out error)
            && TryBounded(root, "seconds"u8, "seconds", 1, MaxTapSeconds, DefaultTapSeconds, out request.Seconds, out error)
            && TryBounded(root, "bytes"u8, "bytes", 0, MaxTapBytes, DefaultTapBytes, out request.MaxBytes, out error);
    }

    private static bool TryBounded(JsonElement root, ReadOnlySpan<byte> utf8Name, string name, int min, int max, int fallback, out int value, out FeedError error)
    {
        value = fallback;
        error = default;
        if (!root.TryGetProperty(utf8Name, out JsonElement element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out value) || value < min || value > max)
        {
            value = fallback;
            error = new FeedError(BadArgs, $"\"{name}\" must be a whole number from {min} to {max}");
            return false;
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
