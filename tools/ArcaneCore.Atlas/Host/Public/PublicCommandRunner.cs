using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ArcaneCore.Atlas.Core.Commands;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Host.Public;

/// <summary>
/// Runs a command for a public session (HTTP <c>/api/public/command</c> and the socket's <c>command</c> message): the allowlist first, then the typed
/// arguments, then the command. Results are <c>CommandResult</c>-shaped JSON; failures carry a stable code and a fixed generic message, never exception
/// text (the reason goes to the log with a reference). The Local registry is not consulted for session commands, so a Local command of the same name
/// can never answer a visitor.
/// </summary>
internal sealed partial class PublicCommandRunner(PublicRuntime runtime)
{
    /// <summary>The code of an unlisted command.</summary>
    public const string NotAvailable = "not-available";

    /// <summary>The code of a call over a rate limit.</summary>
    public const string RateLimited = "rate-limited";

    [GeneratedRegex(@"^[A-Za-z0-9 _'-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex FindName();

    /// <summary>Runs <paramref name="name"/> with <paramref name="args"/> for <paramref name="session"/>.</summary>
    public async Task<JsonObject> RunAsync(PublicSession session, string name, JsonObject args, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        if (!PublicCommandPolicy.IsAllowed(name, session.Tier, session.Security, runtime.Options))
        {
            return Failure(name, NotAvailable, started);
        }

        try
        {
            JsonNode? data = name switch
            {
                "guest.status" => GuestCommands.Status(runtime),
                "guest.maps" => GuestCommands.Maps(runtime),
                "guest.layers" => GuestCommands.Layers(),
                "guest.help" => GuestCommands.Help(runtime, session.Tier, session.Security),
                "bookmarks.list" => GuestCommands.BookmarksList(runtime),
                "live.entities" => LiveEntities(session, args),
                "live.find" => LiveFind(session, args),
                "pick.public" => await Pick(session, args, cancellationToken).ConfigureAwait(false),
                "me.characters" => await Characters(session, cancellationToken).ConfigureAwait(false),
                _ => await Action(session, name, args, cancellationToken).ConfigureAwait(false),
            };
            return new JsonObject { ["command"] = name, ["ok"] = true, ["data"] = data, ["elapsedMs"] = Elapsed(started) };
        }
        catch (CommandException e)
        {
            return Failure(name, PublicCode(e.Code), started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            string reference = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            runtime.Log.LogWarning(e, "public command {Command} failed (ref {Ref})", name, reference);
            return Failure(name, "failed", started, reference);
        }
    }

    private uint MapArg(JsonObject args)
    {
        IReadOnlyList<uint> allowed = runtime.AllowedMaps;
        if (args["map"] is null)
        {
            return allowed.Count > 0 ? allowed[0] : throw new CommandException(CommandException.Unavailable, "no public map");
        }

        if (args["map"] is JsonValue v && v.TryGetValue(out long id) && id is >= 0 and <= uint.MaxValue && allowed.Contains((uint)id))
        {
            return (uint)id;
        }

        throw new CommandException(CommandException.BadArguments, "map is not public");
    }

    private JsonObject LiveEntities(PublicSession session, JsonObject args)
    {
        uint map = MapArg(args);
        int limit = GuestCommands.MaxEntities;
        if (args["limit"] is { } limitNode)
        {
            limit = limitNode is JsonValue v && v.TryGetValue(out long l) && l is >= 1 and <= GuestCommands.MaxEntities
                ? (int)l
                : throw new CommandException(CommandException.BadArguments, "limit");
        }

        HashSet<string>? kinds = null;
        if (args["kinds"] is { } kindsNode)
        {
            kinds = [];
            if (kindsNode is not JsonArray array || array.Count > 3)
            {
                throw new CommandException(CommandException.BadArguments, "kinds");
            }

            foreach (JsonNode? item in array)
            {
                string? kind = item is JsonValue kv && kv.TryGetValue(out string? text) ? text : null;
                if (kind is not ("character" or "creature" or "gameobject"))
                {
                    throw new CommandException(CommandException.BadArguments, "kinds");
                }

                kinds.Add(kind);
            }
        }

        return GuestCommands.LiveEntities(runtime, session, map, kinds, limit);
    }

    private JsonObject LiveFind(PublicSession session, JsonObject args)
    {
        uint map = MapArg(args);
        string text = args["name"] is JsonValue v && v.TryGetValue(out string? name) && FindName().IsMatch(name)
            ? name
            : throw new CommandException(CommandException.BadArguments, "name");
        if (!runtime.Limits.AllowWorldCall(session.Ref, session.IpKey))
        {
            throw new CommandException(RateLimited, RateLimited);
        }

        return GuestCommands.LiveFind(runtime, session, map, text);
    }

    private async Task<JsonNode?> Pick(PublicSession session, JsonObject args, CancellationToken cancellationToken)
    {
        if (!runtime.Limits.AllowWorldCall(session.Ref, session.IpKey))
        {
            throw new CommandException(RateLimited, RateLimited);
        }

        uint map = MapArg(args);
        double[]? cell = null;
        if (args["cell"] is { } cellNode)
        {
            if (cellNode is not JsonArray { Count: 2 } pair
                || pair[0] is not JsonValue x || !x.TryGetValue(out double cx) || !double.IsFinite(cx)
                || pair[1] is not JsonValue y || !y.TryGetValue(out double cy) || !double.IsFinite(cy))
            {
                throw new CommandException(CommandException.BadArguments, "cell");
            }

            cell = [cx, cy];
        }

        string? id = args["id"] is JsonValue v && v.TryGetValue(out string? text) ? text : null;
        return await PublicLoginCommands.PickAsync(runtime, session, map, id, cell, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonNode?> Characters(PublicSession session, CancellationToken cancellationToken)
    {
        if (!runtime.Limits.AllowWorldCall(session.Ref, session.IpKey))
        {
            throw new CommandException(RateLimited, RateLimited);
        }

        return await PublicLoginCommands.CharactersAsync(runtime, session, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonNode?> Action(PublicSession session, string name, JsonObject args, CancellationToken cancellationToken)
    {
        if (runtime.Actions is not { } actions)
        {
            throw new CommandException(NotAvailable, NotAvailable);
        }

        return await actions.RunAsync(session, name, args, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The public code of a command failure.</summary>
    private static string PublicCode(string code) => code switch
    {
        CommandException.BadArguments => "bad-args",
        CommandException.Unavailable => "unavailable",
        NotAvailable => NotAvailable,
        RateLimited => RateLimited,
        _ => "failed",
    };

    private static double Elapsed(long started) => Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 3);

    /// <summary>
    /// A failed result with the fixed message of its code. The command name is echoed only when it is one this listener lists; any other text (it came from
    /// the client) is replaced by <c>?</c>, so a refusal is small whatever the client sent.
    /// </summary>
    internal static JsonObject Failure(string name, string code, long started, string? reference = null)
    {
        if (!PublicCommandPolicy.IsKnown(name))
        {
            name = "?";
        }

        var error = new JsonObject { ["code"] = code, ["message"] = Message(code) };
        if (reference is not null)
        {
            error["ref"] = reference;
        }

        return new JsonObject { ["command"] = name, ["ok"] = false, ["error"] = error, ["elapsedMs"] = Elapsed(started) };
    }

    /// <summary>The generic message of a public error code.</summary>
    internal static string Message(string code) => code switch
    {
        NotAvailable => "That command is not available here.",
        "bad-args" => "The arguments are not valid.",
        RateLimited => "Too many requests; try again in a moment.",
        "unavailable" => "That is not available right now.",
        _ => "The command failed.",
    };
}
