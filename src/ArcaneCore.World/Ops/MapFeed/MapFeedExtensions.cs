using System.Buffers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// An extension of the map feed protocol (docs/areas/atlas-actions.md): a handler for ops the core parser does not know. The core
/// recognizes only the names in <see cref="MapFeedExtensions.IsDeclared"/>, and hands the whole line to the feed's extension, if it has one;
/// a feed without one answers those names <c>unknown-op</c> exactly as before, so the core protocol and its <c>hello</c> do not change.
/// Handlers run on the connection's reader task, one request at a time per connection; any thread-safety across connections is theirs.
/// </summary>
internal interface IFeedExtension
{
    /// <summary>Whether this extension serves <paramref name="op"/> (one of <see cref="MapFeedExtensions.IsDeclared"/>).</summary>
    bool Handles(string op);

    /// <summary>The feed started (called once from <see cref="WorldMapFeed.StartAsync"/>, before the pipe is created): decide what is on.</summary>
    void FeedStarted(WorldMapFeed feed);

    /// <summary>
    /// Serve one request of <paramref name="op"/> on <paramref name="connection"/>. True when the line was answered with a <c>result</c> (a
    /// served line, whatever the answer), false when it could only be refused with an uncorrelated <c>error</c> (a strike).
    /// </summary>
    Task<bool> HandleAsync(MapFeedConnection connection, string op, ReadOnlyMemory<byte> line);

    /// <summary>The connection's reader has ended (called once, after the last <see cref="HandleAsync"/> returned): release what was kept per connection.</summary>
    ValueTask ConnectionEndedAsync(MapFeedConnection connection);
}

/// <summary>The extension registry: the declared op names and the hook the feed calls when it starts. Pure apart from the hook; any thread.</summary>
internal static class MapFeedExtensions
{
    /// <summary>Bind the connection's role (first extension op on the shared pipe).</summary>
    public const string RoleOp = "role";

    /// <summary>Run a command or template as an actor.</summary>
    public const string ActionOp = "action";

    /// <summary>Ask what the action channel offers.</summary>
    public const string CapsOp = "caps";

    /// <summary>Verify a web login and issue a ticket (docs/areas/atlas-public.md; <see cref="MapFeedLogin"/>).</summary>
    public const string LoginOp = "login";

    /// <summary>End a ticket (web connection) or every ticket of an account (local connection).</summary>
    public const string LogoutOp = "logout";

    /// <summary>The online characters of a ticket's own account (<see cref="MapFeedLogin"/>).</summary>
    public const string CharactersOp = "characters";

    /// <summary>The entity guids of a map that are linked to a real player (<see cref="MapFeedPublicLinks"/>).</summary>
    public const string LinksOp = "links";

    private static readonly HashSet<string> Declared = new(StringComparer.Ordinal) { RoleOp, ActionOp, CapsOp, LoginOp, LogoutOp, CharactersOp, LinksOp };

    /// <summary>Whether <paramref name="op"/> is an op an extension may serve (the parser then hands it on instead of refusing it).</summary>
    public static bool IsDeclared(string op) => Declared.Contains(op);

    /// <summary>The feed is starting: resolve its extension from <paramref name="services"/> and attach the web pipe, if the host has one.</summary>
    public static void FeedStarted(WorldMapFeed feed, IServiceProvider services)
    {
        IFeedExtension[] extensions = [.. services.GetServices<IFeedExtension>()];
        feed.Extension = extensions.Length switch
        {
            0 => null,
            1 => extensions[0],
            _ => new CompositeFeedExtension(extensions),
        };
        feed.Extension?.FeedStarted(feed);
        services.GetService<WebFeedPipe>()?.Attach(feed);
    }
}

/// <summary>Reading and writing the lines of the extension ops.</summary>
internal static class FeedExtensionJson
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = MapFeedProtocol.MaxJsonDepth,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    /// <summary>Parse a line the core parser already accepted as a JSON object.</summary>
    public static JsonDocument Parse(ReadOnlyMemory<byte> line) => JsonDocument.Parse(line, DocumentOptions);

    /// <summary>The integer <c>id</c> of a request, or false when it has none (the line is then refused with an uncorrelated error).</summary>
    public static bool TryId(JsonElement root, out long id)
    {
        id = 0;
        return root.TryGetProperty("id"u8, out JsonElement element) && element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out id);
    }

    /// <summary>A <c>result</c> line: <c>{"op":"result","id","ok","kind", ...body}</c>, newline included.</summary>
    public static byte[] Result(long id, bool ok, string kind, Action<Utf8JsonWriter>? body = null)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("op"u8, "result"u8);
            writer.WriteNumber("id"u8, id);
            writer.WriteBoolean("ok"u8, ok);
            writer.WriteString("kind"u8, kind);
            body?.Invoke(writer);
            writer.WriteEndObject();
        }

        return MapFeedJson.Finish(buffer);
    }

    /// <summary>A refusal: <c>ok:false</c> with a stable <paramref name="code"/> and a sentence.</summary>
    public static byte[] Refused(long id, string kind, string code, string message, Action<Utf8JsonWriter>? extra = null)
        => Result(id, ok: false, kind, writer =>
        {
            writer.WriteString("code"u8, code);
            writer.WriteString("message"u8, message);
            extra?.Invoke(writer);
        });

    /// <summary>A line of a command's output that arrived after its <c>result</c>: <c>{"op":"actionLine","id","line"}</c>.</summary>
    public static byte[] ActionLine(long id, string line)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("op"u8, "actionLine"u8);
            writer.WriteNumber("id"u8, id);
            writer.WriteString("line"u8, line);
            writer.WriteEndObject();
        }

        return MapFeedJson.Finish(buffer);
    }
}
