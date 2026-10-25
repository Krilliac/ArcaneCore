using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Host.Public;

/// <summary>
/// <c>pick.public</c> and <c>me.characters</c> (design 0.3.1, 0.6, 0.7): the two commands that need a public session. <c>pick.public</c> answers a click
/// on an entity a guest may see with its basic information; <c>me.characters</c> lists a logged-in account's own online characters. The registry
/// versions exist so <c>ctl help</c> lists them; they refuse a Local caller, who has no session (the Local <c>query.pick</c> is the Local tool).
/// </summary>
internal static class PublicLoginCommands
{
    /// <summary>The fields of the world's <c>info.public</c> answer that may reach a guest (an allowlist: any other field is dropped here).</summary>
    private static readonly string[] InfoFields = ["level", "faction", "type", "race", "class", "family"];

    public static void Register(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(new CommandDefinition(
            "pick.public",
            "Basic public information about an entity a visitor clicked (public sessions only).",
            [
                new("id", ArgType.String, "The entity's opaque id from a live frame (or pass \"cell\": [x, y] for a player cell)."),
                new("map", ArgType.Integer, "Map id; default the first public map."),
            ],
            (_, _) => throw new CommandException(CommandException.Unavailable, "pick.public needs a public session; use the public listener.")));
        registry.Register(new CommandDefinition(
            "me.characters",
            "A logged-in account's own online characters (public sessions only).",
            [],
            (_, _) => throw new CommandException(CommandException.Unavailable, "me.characters needs a logged-in public session.")));
    }

    /// <summary><c>pick.public</c>: finds the entity (or player cell) by its opaque id and adds what the world may tell.</summary>
    public static async Task<JsonObject> PickAsync(PublicRuntime runtime, PublicSession session, uint map, string? id, double[]? cell, CancellationToken cancellationToken)
    {
        PublicMapStatus status = runtime.Frames.Status(map);
        GuestProjection? projection = status.Projection;
        if (projection is null)
        {
            throw new CommandException(CommandException.Unavailable, "there is no live data for that map right now.");
        }

        if (cell is { Length: 2 })
        {
            double half = projection.CellYards / 2.0;
            foreach (GuestCell candidate in projection.Cells)
            {
                if (Math.Abs(candidate.X - cell[0]) <= half && Math.Abs(candidate.Y - cell[1]) <= half)
                {
                    return new JsonObject { ["type"] = "players", ["count"] = candidate.Count };
                }
            }

            throw new CommandException(CommandException.BadArguments, "there is no player cell there.");
        }

        if (id is null || id.Length != 16 || !id.All(char.IsAsciiHexDigit))
        {
            throw new CommandException(CommandException.BadArguments, "pick.public needs an entity id.");
        }

        if (FindEntity(projection, session, id) is not { } picked)
        {
            throw new CommandException(CommandException.BadArguments, "that entity is no longer there.");
        }

        JsonObject info = GuestCommands.EntityJson(session, picked);
        info["type"] = picked.Kind switch
        {
            GuestKind.Character => "Character",
            GuestKind.Creature => "Creature",
            _ => "Game object",
        };
        if (runtime.Limits.AllowInfo(session.Ref)
            && await runtime.World.InfoPublicAsync(picked.Guid, runtime.Options.Guest.ShowPlayers, runtime.Options.Guest.ShowPlayerNames, cancellationToken).ConfigureAwait(false) is { } world)
        {
            foreach (string field in InfoFields)
            {
                if (world[field] is JsonValue value)
                {
                    info[field] = value.DeepClone();
                }
            }
        }

        return info;
    }

    private static GuestEntity? FindEntity(GuestProjection projection, PublicSession session, string id)
    {
        string wanted = id.ToLowerInvariant();
        Span<char> text = stackalloc char[16];
        foreach (GuestEntity entity in projection.Entities)
        {
            GuestFrameProjector.OpaqueId(session.OpaqueKey, entity.Guid, text);
            if (text.SequenceEqual(wanted))
            {
                return entity;
            }
        }

        return null;
    }

    /// <summary><c>me.characters</c>: the account's own online characters, from the world with the session's ticket.</summary>
    public static async Task<JsonObject> CharactersAsync(PublicRuntime runtime, PublicSession session, CancellationToken cancellationToken)
    {
        if (session.Tier != PublicTier.Account || session.Ticket is null)
        {
            throw new CommandException(CommandException.Unavailable, "log in to see your characters.");
        }

        JsonObject? answer = await runtime.World.CharactersAsync(session.Ticket, cancellationToken).ConfigureAwait(false)
            ?? throw new CommandException(CommandException.Unavailable, "the world could not list your characters right now.");
        var list = new JsonArray();
        if (answer["characters"] is JsonArray characters)
        {
            foreach (JsonNode? item in characters.Take(16))
            {
                if (item is JsonObject character)
                {
                    var copy = new JsonObject();
                    foreach (string key in new[] { "g", "n", "map", "instance", "x", "y", "z", "o", "level" })
                    {
                        if (character[key] is JsonValue value)
                        {
                            copy[key] = value.DeepClone();
                        }
                    }

                    list.Add(copy);
                }
            }
        }

        return new JsonObject { ["count"] = list.Count, ["characters"] = list };
    }
}
