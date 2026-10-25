using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// The extension ops of one connection and its role (docs/areas/atlas-actions.md, "Roles"). The role is the connection's, fixed once: by the
/// pipe it arrived on (the separate web pipe), or by its first extension op on the shared pipe (<c>role</c> with <c>"web"</c> binds web, any
/// other extension op binds local). It is read only by this connection's reader task.
/// </summary>
internal sealed partial class MapFeedConnection
{
    private static long s_lastConnectionId;

    private readonly bool _roleFixedByPipe;
    private ClientIdentity? _identity;

    /// <summary>A number unique to this connection in this process (keys per-connection limits).</summary>
    internal long ConnectionId { get; } = Interlocked.Increment(ref s_lastConnectionId);

    /// <summary>The connection's role; <see cref="FeedRole.Unbound"/> until the first extension op on the shared pipe.</summary>
    internal FeedRole Role { get; private set; }

    /// <summary>The feed this connection belongs to.</summary>
    internal WorldMapFeed Feed => _feed;

    /// <summary>Cancelled when the connection ends (the daemon stops, the client leaves or stops reading).</summary>
    internal CancellationToken Lifetime => _lifetime.Token;

    /// <summary>The logger of the feed.</summary>
    internal ILogger Logger => _logger;

    /// <summary>The pipe client behind this connection (its token user and process id); read lazily, once.</summary>
    internal ClientIdentity Identity => _identity ??= MapFeedClientIdentity.Of(_stream);

    /// <summary>Queue a line that arrives after the request it belongs to was answered; lost when the queue is full or the connection ended.</summary>
    internal void QueueLate(byte[] line) => _outbound.Writer.TryWrite(new Outbound(OutboundKind.Line, null, line, 0, 0));

    private async Task<Outcome> HandleExtensionAsync(FeedRequest request)
    {
        string op = request.ExtensionOp ?? string.Empty;
        if (_feed.Extension is not { } extension || !extension.Handles(op))
        {
            return await RefuseAsync(request, new FeedError(
                MapFeedProtocol.UnknownOp, "unknown op; the ops are hello, subscribe, unsubscribe and query (and console, inject, send, tap and untap with the packet tools)")).ConfigureAwait(false);
        }

        if (op == MapFeedExtensions.RoleOp)
        {
            return await RoleAsync(request.ExtensionLine).ConfigureAwait(false) ? Outcome.Served : Outcome.Refused;
        }

        if (Role == FeedRole.Unbound)
        {
            Role = FeedRole.Local;
        }

        return await extension.HandleAsync(this, op, request.ExtensionLine).ConfigureAwait(false) ? Outcome.Served : Outcome.Refused;
    }

    /// <summary>
    /// <c>{"op":"role","id":n,"role":"web"}</c>: bind the connection as web. Allowed once, as the first extension op; later it is
    /// <c>role-locked</c>. On the web pipe the pipe fixed the role and the op is acknowledged as a no-op.
    /// </summary>
    private async Task<bool> RoleAsync(ReadOnlyMemory<byte> line)
    {
        using JsonDocument document = FeedExtensionJson.Parse(line);
        JsonElement root = document.RootElement;
        if (!FeedExtensionJson.TryId(root, out long id))
        {
            _logger.LogWarning("map feed: a role line without an integer id from {Client}", Client);
            await QueueAsync(MapFeedJson.ErrorLine(MapFeedProtocol.BadArgs, "role needs an integer \"id\"")).ConfigureAwait(false);
            return false;
        }

        bool web = root.TryGetProperty("role"u8, out JsonElement role) && role.ValueKind == JsonValueKind.String && role.ValueEquals("web"u8);
        if (!web)
        {
            await QueueAsync(FeedExtensionJson.Refused(id, "role", MapFeedProtocol.BadArgs, "\"role\" must be \"web\": a connection is local unless it binds as web first")).ConfigureAwait(false);
            return true;
        }

        if (Role != FeedRole.Unbound && !_roleFixedByPipe)
        {
            await QueueAsync(FeedExtensionJson.Refused(id, "role", MapFeedRoles.RoleLocked, "the connection's role is already bound and cannot change")).ConfigureAwait(false);
            return true;
        }

        if (Role == FeedRole.Unbound)
        {
            Role = FeedRole.Web;
            _logger.LogInformation("map feed: {Client} bound as a web connection", Client);
        }

        await QueueAsync(FeedExtensionJson.Result(id, ok: true, "role", w => w.WriteString("role"u8, MapFeedRoles.Name(Role)))).ConfigureAwait(false);
        return true;
    }
}
