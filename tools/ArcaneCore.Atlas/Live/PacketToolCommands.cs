using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Protocol;

namespace ArcaneCore.Atlas.Live;

/// <summary>
/// The packet-tools commands (docs/areas/atlas.md, "Driving the server headlessly"; docs/areas/map-feed.md, "Packet tools"):
/// <c>server.console</c>, <c>live.inject</c>, <c>live.send</c>, <c>live.tap.start</c>, <c>live.tap.read</c>, <c>live.tap.stop</c> and
/// <c>opcodes.find</c>. Every one but <c>opcodes.find</c> drives a connected world daemon through <see cref="LiveFeed"/>, so it needs a
/// running <c>arcane-atlas serve</c> instance (whose live connection persists between calls): a headless <c>run</c> refuses it with
/// Unavailable. Arguments are checked here (BadArguments) before anything reaches the pipe; the daemon checks them again. Any thread.
/// </summary>
internal static class PacketToolCommands
{
    /// <summary>How long a console line may take: the daemon waits up to two minutes for a command that holds itself open.</summary>
    public static readonly TimeSpan ConsoleTimeout = TimeSpan.FromSeconds(150);

    public const int DefaultReadMax = 500;
    public const int MaxReadMax = 5000;
    public const double MaxReadWait = 30;
    public const int DefaultFindLimit = 50;
    public const int MaxFindLimit = 1000;

    private static readonly IReadOnlyList<string> Directions = ["in", "out", "both"];
    private static readonly IReadOnlyList<string> OpcodeSides = ["client", "server", "any"];
    private static readonly TimeSpan ReadPoll = TimeSpan.FromMilliseconds(25);

    public static void Register(CommandRegistry registry, LiveFeed feed)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(feed);
        string fieldHelp = $"Typed payload fields, written little-endian one after another, each type:value with type one of {string.Join(", ", PacketFields.Types)} (for example [\"u32:1\", \"guid:0x1A2B\", \"cstring:hello\"]). Not with payload.";
        CommandArg target = new("target", ArgType.String, "The online character (player or playerbot) by name. Give target or guid, not both.");
        CommandArg guid = new("guid", ArgType.String, "The online character by player guid, a quoted hex string. Give target or guid, not both.");

        registry.Register(new CommandDefinition(
            "server.console",
            "Run one console line on the connected world daemon (as the server console: Administrator authority, logged and GM-audited) and return its reply lines. Needs the daemon's packet tools on. A command that holds itself open (.botctl wait) is waited for up to 2 minutes; lines it prints later come back in live.tap.read as consoleLines.",
            [new CommandArg("line", ArgType.String, "The console line, with or without a leading dot (for example \".reload status\").", Required: true)],
            (call, ct) => ConsoleAsync(feed, call, ct),
            CommandSurfaces.Instance));

        registry.Register(new CommandDefinition(
            "live.inject",
            "Hand a client opcode to an online session as if its client had sent it: the same dispatcher, queue and thread as the client's own packets, so the real handler runs and the client gets its real reply. Authentication and session-lifecycle opcodes are refused by the daemon. Needs the daemon's packet tools on.",
            [
                target,
                guid,
                new CommandArg("opcode", ArgType.String, "A client opcode (CMSG_ or MSG_): its name in any case, a decimal number, or 0x hex.", Required: true),
                new CommandArg("payload", ArgType.String, "The payload as hex digits (spaces ignored). Not with fields; omit both for an empty payload."),
                new CommandArg("fields", ArgType.StringList, fieldHelp),
            ],
            (call, ct) => PacketAsync(feed, call, LiveProtocol.OpInject, ct),
            CommandSurfaces.Instance));

        registry.Register(new CommandDefinition(
            "live.send",
            "Queue a server opcode to an online session's client through the session's own send path (header cipher and all). Authentication and session-lifecycle opcodes are refused by the daemon. Needs the daemon's packet tools on.",
            [
                target,
                guid,
                new CommandArg("opcode", ArgType.String, "A server opcode (SMSG_ or MSG_): its name in any case, a decimal number, or 0x hex.", Required: true),
                new CommandArg("payload", ArgType.String, "The payload as hex digits (spaces ignored). Not with fields; omit both for an empty payload."),
                new CommandArg("fields", ArgType.StringList, fieldHelp),
            ],
            (call, ct) => PacketAsync(feed, call, LiveProtocol.OpSend, ct),
            CommandSurfaces.Instance));

        registry.Register(new CommandDefinition(
            "live.tap.start",
            "Watch an online session's packets (both directions by default) for a bounded count and time; read them with live.tap.read. One tap per connection. The daemon never waits for the tap: packets it cannot queue are dropped and counted.",
            [
                target,
                guid,
                new CommandArg("dir", ArgType.String, "Which packets: in (from the client, injected ones included), out (to it) or both.", Default: JsonValue.Create("both"), Choices: Directions),
                new CommandArg("opcodes", ArgType.StringList, "Only these opcodes (names or numbers, at most 64). Default all."),
                new CommandArg("maxPackets", ArgType.Integer, "The tap ends after this many packets (1 to 100000, default 1000).", Default: JsonValue.Create(1000)),
                new CommandArg("seconds", ArgType.Integer, "The tap ends after this many seconds (1 to 3600, default 60).", Default: JsonValue.Create(60)),
                new CommandArg("bytes", ArgType.Integer, "Payload bytes shown per packet as hex (0 to 4096, default 256); a longer payload is cut and flagged.", Default: JsonValue.Create(256)),
            ],
            (call, ct) => TapStartAsync(feed, call, ct),
            CommandSurfaces.Instance));

        registry.Register(new CommandDefinition(
            "live.tap.read",
            "Take the tapped packets received so far (oldest first: seq, t in unix ms, dir, opcode, name, len, hex, cut, inj), the tap's end once every packet before it was read (reason count, time, stopped or closed; packets sent and dropped by the daemon), and late console lines.",
            [
                new CommandArg("max", ArgType.Integer, $"Most packets to return (1 to {MaxReadMax}, default {DefaultReadMax}).", Default: JsonValue.Create(DefaultReadMax)),
                new CommandArg("wait", ArgType.Number, $"Seconds to wait for a packet or the end when none is waiting (0 to {MaxReadWait:0}, default 0).", Default: JsonValue.Create(0)),
            ],
            (call, ct) => TapReadAsync(feed, call, ct),
            CommandSurfaces.Instance));

        registry.Register(new CommandDefinition(
            "live.tap.stop",
            "Stop the connection's tap; its end (reason stopped) follows in live.tap.read.",
            [],
            (_, ct) => TapStopAsync(feed, ct),
            CommandSurfaces.Instance));

        registry.Register(new CommandDefinition(
            "opcodes.find",
            "Search the build-5875 world opcode table (the server's own, src/ArcaneCore.Protocol/WorldOpcode.g.cs) by name or number. Works offline.",
            [
                new CommandArg("query", ArgType.String, "Part of a name in any case (QUERY_TIME), or a number (decimal or 0x hex). Omit to list all."),
                new CommandArg("side", ArgType.String, "client (CMSG_ and MSG_: what live.inject takes), server (SMSG_ and MSG_: what live.send takes) or any.", Default: JsonValue.Create("any"), Choices: OpcodeSides),
                new CommandArg("limit", ArgType.Integer, $"Most opcodes to return (1 to {MaxFindLimit}, default {DefaultFindLimit}).", Default: JsonValue.Create(DefaultFindLimit)),
            ],
            (call, _) => Task.FromResult<JsonNode?>(FindOpcodes(call))));
    }

    private static async Task<JsonNode?> ConsoleAsync(LiveFeed feed, CommandInvocation call, CancellationToken cancellationToken)
    {
        string line = call.GetString("line");
        if (string.IsNullOrWhiteSpace(line) || line.Length > 4096)
        {
            throw Bad(call, "argument 'line' must be a console command of 1 to 4096 characters.");
        }

        return await feed.ToolAsync(new JsonObject { ["op"] = LiveProtocol.OpConsole, ["line"] = line }, ConsoleTimeout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonNode?> PacketAsync(LiveFeed feed, CommandInvocation call, string op, CancellationToken cancellationToken)
    {
        JsonObject message = Target(call, op);
        message["opcode"] = (int)PacketFields.ParseOpcode(call.Definition.Name, OpcodeText(call, "opcode"));
        byte[] payload = PacketFields.Build(call.Definition.Name, call.OptionalString("payload"), call.GetStringList("fields"));
        message["payload"] = Convert.ToHexString(payload);
        return await feed.ToolAsync(message, null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonNode?> TapStartAsync(LiveFeed feed, CommandInvocation call, CancellationToken cancellationToken)
    {
        JsonObject message = Target(call, LiveProtocol.OpTap);
        message["dir"] = call.OptionalString("dir") ?? "both";
        IReadOnlyList<string> opcodes = call.GetStringList("opcodes");
        if (opcodes.Count > 64)
        {
            throw Bad(call, "argument 'opcodes' may list at most 64 opcodes.");
        }

        if (opcodes.Count > 0)
        {
            message["opcodes"] = new JsonArray([.. opcodes.Select(o => (JsonNode?)JsonValue.Create((int)PacketFields.ParseOpcode(call.Definition.Name, o)))]);
        }

        message["maxPackets"] = Bounded(call, "maxPackets", 1, 100_000, 1000);
        message["seconds"] = Bounded(call, "seconds", 1, 3600, 60);
        message["bytes"] = Bounded(call, "bytes", 0, 4096, 256);
        return await feed.ToolAsync(message, null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonNode?> TapStopAsync(LiveFeed feed, CancellationToken cancellationToken)
        => await feed.ToolAsync(new JsonObject { ["op"] = LiveProtocol.OpUntap }, null, cancellationToken).ConfigureAwait(false);

    private static async Task<JsonNode?> TapReadAsync(LiveFeed feed, CommandInvocation call, CancellationToken cancellationToken)
    {
        int max = Bounded(call, "max", 1, MaxReadMax, DefaultReadMax);
        double wait = call.OptionalNumber("wait") ?? 0;
        if (!(wait >= 0 && wait <= MaxReadWait))
        {
            throw Bad(call, string.Create(CultureInfo.InvariantCulture, $"argument 'wait' must be 0 to {MaxReadWait:0} seconds."));
        }

        if (wait > 0 && !feed.HasToolTraffic)
        {
            long deadline = Environment.TickCount64 + (long)(wait * 1000);
            while (!feed.HasToolTraffic && Environment.TickCount64 < deadline && feed.IsConnected)
            {
                await Task.Delay(ReadPoll, cancellationToken).ConfigureAwait(false);
            }
        }

        return feed.ReadToolTraffic(max);
    }

    private static JsonObject FindOpcodes(CommandInvocation call)
    {
        string? query = call.OptionalString("query")?.Trim();
        string side = call.OptionalString("side") ?? "any";
        int limit = Bounded(call, "limit", 1, MaxFindLimit, DefaultFindLimit);
        uint? number = null;
        if (query is { Length: > 0 })
        {
            if (query.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(query.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint hex))
            {
                number = hex;
            }
            else if (uint.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out uint dec))
            {
                number = dec;
            }
        }

        var found = new JsonArray();
        int matched = 0;
        foreach (WorldOpcode opcode in Enum.GetValues<WorldOpcode>().Distinct().Order())
        {
            string name = WorldOpcodeNames.GetName(opcode);
            bool client = name.StartsWith("CMSG_", StringComparison.Ordinal) || name.StartsWith("MSG_", StringComparison.Ordinal);
            bool server = name.StartsWith("SMSG_", StringComparison.Ordinal) || name.StartsWith("MSG_", StringComparison.Ordinal);
            if ((side == "client" && !client) || (side == "server" && !server))
            {
                continue;
            }

            bool hit = query is not { Length: > 0 }
                || (number is { } n ? (uint)opcode == n : name.Contains(query, StringComparison.OrdinalIgnoreCase));
            if (!hit)
            {
                continue;
            }

            matched++;
            if (found.Count < limit)
            {
                found.Add(new JsonObject
                {
                    ["name"] = name,
                    ["opcode"] = (int)opcode,
                    ["hex"] = "0x" + ((int)opcode).ToString("X3", CultureInfo.InvariantCulture),
                    ["side"] = client && server ? "both" : client ? "client" : "server",
                });
            }
        }

        return new JsonObject
        {
            ["count"] = found.Count,
            ["matched"] = matched,
            ["opcodes"] = found,
        };
    }

    /// <summary>The <c>target</c> or <c>guid</c> of a request, exactly one of them.</summary>
    private static JsonObject Target(CommandInvocation call, string op)
    {
        string? name = call.OptionalString("target")?.Trim();
        ulong? guid = LiveCommands.OptionalGuid(call, "guid");
        if ((name is { Length: > 0 }) == guid.HasValue)
        {
            throw Bad(call, "name the session with exactly one of 'target' (a character name) and 'guid' (its player guid in hex).");
        }

        var message = new JsonObject { ["op"] = op };
        if (guid is { } g)
        {
            message["guid"] = g.ToString("X16", CultureInfo.InvariantCulture);
        }
        else
        {
            message["target"] = name;
        }

        return message;
    }

    /// <summary>An opcode argument given as a string or a bare JSON number.</summary>
    private static string OpcodeText(CommandInvocation call, string name)
    {
        JsonNode? node = call.Args[name];
        if (JsonScalars.TryGetInt64(node, out long number))
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        return call.GetString(name);
    }

    private static int Bounded(CommandInvocation call, string name, int min, int max, int fallback)
    {
        long value = call.OptionalInt(name) ?? fallback;
        return value >= min && value <= max
            ? (int)value
            : throw Bad(call, string.Create(CultureInfo.InvariantCulture, $"argument '{name}' must be {min} to {max}."));
    }

    private static CommandException Bad(CommandInvocation call, string message)
        => new(CommandException.BadArguments, $"{call.Definition.Name}: {message}");
}
