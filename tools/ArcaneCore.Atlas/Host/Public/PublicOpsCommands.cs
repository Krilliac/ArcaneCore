using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Host.Public;

/// <summary>
/// The operator's commands for the public listener (design 0.9): <c>public.status</c>, <c>public.sessions</c>, <c>public.kick</c> and <c>public.revoke</c>.
/// Ordinary Atlas commands, so the CLI and MCP reach them; never on the public allowlist. Kicks and revokes end the sessions here, tell the world to end
/// their tickets, and are written to the Atlas log with the reason.
/// </summary>
internal static class PublicOpsCommands
{
    /// <summary>Lets <c>public.revoke</c> ask the world to drop every ticket of an account when this process has a Local feed path (design 0.9); returns whether it did.</summary>
    public interface IWorldRevoker
    {
        Task<bool> RevokeAsync(string account, CancellationToken cancellationToken);
    }

    public static void Register(CommandRegistry registry, PublicState state)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(state);
        registry.Register(new CommandDefinition(
            "public.status",
            "The public listener: mode, TLS, certificate expiry, guest policy, sessions per tier, sockets, bytes, login failures and the feed.",
            [],
            (_, _) => Task.FromResult<JsonNode?>(Status(GuestCommands.Require(state)))));
        registry.Register(new CommandDefinition(
            "public.sessions",
            "Lists the public sessions: reference, tier, account, address, age, idle time and bytes.",
            [],
            (_, _) => Task.FromResult<JsonNode?>(Sessions(GuestCommands.Require(state)))));
        registry.Register(new CommandDefinition(
            "public.kick",
            "Ends public sessions: closes their sockets, deletes them and ends their world tickets. Give one of ref, account or ip.",
            [
                new("ref", ArgType.String, "A session reference from public.sessions."),
                new("account", ArgType.String, "Every session of this account."),
                new("ip", ArgType.String, "Every session from this address (IPv6 by /64)."),
                new("reason", ArgType.String, "Why (written to the log)."),
            ],
            async (call, ct) => await KickAsync(GuestCommands.Require(state), call, ct).ConfigureAwait(false)));
        registry.Register(new CommandDefinition(
            "public.revoke",
            "Ends every public session of an account and, when this process can, asks the world to drop all of the account's web tickets.",
            [
                new("account", ArgType.String, "The account name.", Required: true),
                new("reason", ArgType.String, "Why (written to the log)."),
            ],
            async (call, ct) => await RevokeAsync(GuestCommands.Require(state), call, ct).ConfigureAwait(false)));
    }

    public static JsonObject Status(PublicRuntime runtime)
    {
        (int guests, int accounts, int privileged) = runtime.Sessions.Counts();
        var top = new JsonArray();
        foreach ((string ip, long bytes) in runtime.Stats.TopIps(5))
        {
            top.Add(new JsonObject { ["ip"] = ip, ["bytes"] = bytes });
        }

        var maps = new JsonArray();
        foreach (PublicMapOption map in runtime.Options.Guest.Maps)
        {
            PublicMapStatus s = runtime.Frames.Status(map.Id);
            maps.Add(new JsonObject { ["id"] = map.Id, ["connected"] = s.Connected, ["busy"] = s.Busy, ["reduced"] = s.Reduced, ["seq"] = s.Projection?.Seq ?? 0 });
        }

        return new JsonObject
        {
            ["mode"] = runtime.Mode == PublicProcessMode.Separate ? "separate" : "in-process",
            ["scheme"] = runtime.Scheme,
            ["bind"] = runtime.Options.Bind,
            ["origin"] = runtime.Config.Origin?.GetLeftPart(UriPartial.Authority),
            ["tls"] = TlsWord(runtime.Config.TlsMode),
            ["certificateNotAfter"] = runtime.CertificateNotAfter?.ToString("O", CultureInfo.InvariantCulture),
            ["guest"] = new JsonObject
            {
                ["players"] = runtime.Options.PlayersWord,
                ["names"] = runtime.Options.NamesWord,
                ["feedHz"] = runtime.Options.Guest.FeedHz,
                ["maxBytesPerSecond"] = runtime.Options.Guest.MaxBytesPerSecond,
            },
            ["accountActions"] = PublicServer.AccountActionsWord(runtime.Options.AccountActions, runtime.Actions is not null),
            ["loginMinSecurity"] = PublicRuntime.LevelName(runtime.LoginFloor),
            ["loginAvailable"] = runtime.Config.LoginAllowed,
            ["sessions"] = new JsonObject { ["guest"] = guests, ["account0"] = accounts - privileged, ["accountGm"] = privileged },
            ["sockets"] = runtime.Stats.Sockets,
            ["bytesOut"] = runtime.Stats.BytesOut,
            ["framesSent"] = runtime.Stats.FramesSent,
            ["framesDropped"] = runtime.Stats.FramesDropped,
            ["loginFailuresLast15Min"] = runtime.Stats.LoginFailuresRecent(),
            ["topIpsByBytes"] = top,
            ["feed"] = new JsonObject { ["connected"] = runtime.World.Connected, ["maps"] = maps },
            ["reducedData"] = runtime.AnyReduced,
            ["uptimeSeconds"] = (long)(runtime.Time.GetUtcNow() - runtime.Started).TotalSeconds,
        };
    }

    public static JsonObject Sessions(PublicRuntime runtime)
    {
        DateTimeOffset now = runtime.Time.GetUtcNow();
        var list = new JsonArray();
        foreach (PublicSession s in runtime.Sessions.Snapshot())
        {
            list.Add(new JsonObject
            {
                ["ref"] = s.Ref,
                ["tier"] = s.Tier == PublicTier.Account ? "account" : "guest",
                ["account"] = s.Account,
                ["security"] = s.Tier == PublicTier.Account ? s.Security : null,
                ["ip"] = s.ClientIp,
                ["ageSeconds"] = (long)(now - s.Created).TotalSeconds,
                ["idleSeconds"] = (long)(now - s.LastUse).TotalSeconds,
                ["bytes"] = s.Bytes,
                ["sockets"] = s.SocketCount,
            });
        }

        return new JsonObject { ["count"] = list.Count, ["sessions"] = list };
    }

    private static async Task<JsonNode?> KickAsync(PublicRuntime runtime, CommandInvocation call, CancellationToken ct)
    {
        string? reference = call.OptionalString("ref");
        string? account = call.OptionalString("account");
        string? ip = call.OptionalString("ip");
        if (new[] { reference, account, ip }.Count(v => !string.IsNullOrWhiteSpace(v)) != 1)
        {
            throw new CommandException(CommandException.BadArguments, "public.kick needs exactly one of ref, account or ip.");
        }

        string reason = Reason(call);
        Func<PublicSession, bool> match;
        string target;
        if (!string.IsNullOrWhiteSpace(reference))
        {
            match = s => string.Equals(s.Ref, reference!.Trim(), StringComparison.OrdinalIgnoreCase);
            target = "ref=" + reference.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(account))
        {
            match = s => string.Equals(s.Account, account!.Trim(), StringComparison.OrdinalIgnoreCase);
            target = "account=" + account.Trim();
        }
        else
        {
            string key = System.Net.IPAddress.TryParse(ip!.Trim(), out System.Net.IPAddress? parsed) ? PublicAddresses.Key(parsed) : ip.Trim();
            match = s => string.Equals(s.IpKey, key, StringComparison.OrdinalIgnoreCase);
            target = "ip=" + key;
        }

        IReadOnlyList<EndedSession> ended = runtime.Sessions.End(match, "kicked");
        await EndTicketsAsync(runtime, ended, kick: true, ct).ConfigureAwait(false);
        runtime.Log.LogWarning("public kick {Target}: {Count} session(s) ended, reason: {Reason}", target, ended.Count, reason);
        return new JsonObject { ["kicked"] = ended.Count, ["target"] = target };
    }

    private static async Task<JsonNode?> RevokeAsync(PublicRuntime runtime, CommandInvocation call, CancellationToken ct)
    {
        string account = call.GetString("account").Trim();
        string reason = Reason(call);
        IReadOnlyList<EndedSession> ended = runtime.Sessions.End(s => string.Equals(s.Account, account, StringComparison.OrdinalIgnoreCase), "revoked");
        await EndTicketsAsync(runtime, ended, kick: true, ct).ConfigureAwait(false);
        bool world = false;
        if (call.Services.GetService(typeof(IWorldRevoker)) is IWorldRevoker revoker)
        {
            world = await revoker.RevokeAsync(account, ct).ConfigureAwait(false);
        }

        runtime.Log.LogWarning("public revoke account={Account}: {Count} session(s) ended, world revoke {World}, reason: {Reason}", account, ended.Count, world ? "sent" : "not sent", reason);
        return new JsonObject
        {
            ["revoked"] = ended.Count,
            ["account"] = account,
            ["world"] = world ? "tickets dropped" : "not sent: this process has no Local feed path to the world; run the world's own revoke (logout account=) from a Local connection",
        };
    }

    internal static async Task EndTicketsAsync(PublicRuntime runtime, IReadOnlyList<EndedSession> ended, bool kick, CancellationToken ct)
    {
        foreach (EndedSession end in ended)
        {
            if (end.Ticket is { } ticket)
            {
                await runtime.World.EndTicketAsync(ticket, kick, ct).ConfigureAwait(false);
            }
        }
    }

    private static string Reason(CommandInvocation call)
    {
        string text = call.OptionalString("reason") ?? "operator";
        return text.Length > 120 ? text[..120] : text;
    }

    internal static string TlsWord(PublicTlsMode mode) => mode switch
    {
        PublicTlsMode.Certificate => "certificate",
        PublicTlsMode.Proxy => "proxy",
        _ => "INSECURE-DEV",
    };
}
