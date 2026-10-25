using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Live;

namespace ArcaneCore.Atlas.Host.Public;

/// <summary>
/// The guest commands (design 0.3.1): <c>guest.status</c>, <c>guest.maps</c>, <c>guest.layers</c> and <c>guest.help</c>, plus the projected public versions of
/// <c>live.entities</c> and <c>live.find</c> and the public <c>bookmarks.list</c>. They exist because the Local commands of similar name
/// (<c>status</c>, <c>maps.list</c>, <c>layers.list</c>, <c>help</c>) disclose local paths, client-derived map names, server tile counts and the whole
/// registry. Everything here is built from the operator's settings and the guest projection; nothing is read from the Atlas context or the disk.
/// The registry versions work in any Atlas process and answer from the running listener; the public listener calls the builders directly.
/// </summary>
internal static class GuestCommands
{
    /// <summary>The most entities one <c>live.entities</c> or <c>live.find</c> answer carries.</summary>
    public const int MaxEntities = 500;

    /// <summary>Longest <c>live.find</c> name.</summary>
    public const int MaxFindLength = 32;

    /// <summary>Adds the four guest commands to the registry (Local callers get the same data as a visitor would).</summary>
    public static void Register(CommandRegistry registry, PublicState state)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(state);
        registry.Register(new CommandDefinition("guest.status", "What a public visitor sees as the server status: version, tiers, guest policy flags and whether login is offered.", [],
            (_, _) => Task.FromResult<JsonNode?>(Status(Require(state)))));
        registry.Register(new CommandDefinition("guest.maps", "The maps public visitors may view, with the names the operator wrote.", [],
            (_, _) => Task.FromResult<JsonNode?>(Maps(Require(state)))));
        registry.Register(new CommandDefinition("guest.layers", "The layers public visitors may see (live entity layers only).", [],
            (_, _) => Task.FromResult<JsonNode?>(Layers())));
        registry.Register(new CommandDefinition("guest.help", "The commands a public visitor may run.", [],
            (_, _) => Task.FromResult<JsonNode?>(Help(Require(state), PublicTier.Guest, 0))));
    }

    internal static PublicRuntime Require(PublicState state)
        => state.Runtime ?? throw new CommandException(CommandException.Unavailable, "the public listener is not running in this Atlas process.");

    /// <summary><c>guest.status</c>: no paths, problems, modules or cache data.</summary>
    public static JsonObject Status(PublicRuntime runtime)
    {
        PublicOptions o = runtime.Options;
        return new JsonObject
        {
            ["version"] = AtlasVersion.Current,
            ["tiers"] = new JsonArray("guest", "account"),
            ["guest"] = new JsonObject
            {
                ["players"] = o.PlayersWord,
                ["names"] = o.NamesWord,
                ["feedHz"] = o.Guest.FeedHz,
                ["maps"] = o.Guest.Maps.Count,
            },
            ["login"] = new JsonObject
            {
                ["available"] = runtime.Config.LoginAllowed,
                ["minSecurity"] = PublicRuntime.LevelName(runtime.LoginFloor),
            },
            ["accountActions"] = o.AccountActions,
            ["reducedData"] = runtime.AnyReduced,
            ["feed"] = runtime.World.Connected ? "connected" : "unavailable",
        };
    }

    /// <summary><c>guest.maps</c>: the entries of <c>Guest:Maps</c> with the operator's names and whether the feed has the map right now.</summary>
    public static JsonArray Maps(PublicRuntime runtime)
    {
        var list = new JsonArray();
        foreach (PublicMapOption map in runtime.Options.Guest.Maps)
        {
            PublicMapStatus status = runtime.Frames.Status(map.Id);
            list.Add(new JsonObject { ["id"] = map.Id, ["name"] = map.Name, ["live"] = status.Connected, ["busy"] = status.Busy });
        }

        return list;
    }

    /// <summary><c>guest.layers</c>: the public layer ids with their catalog titles and colours.</summary>
    public static JsonArray Layers()
    {
        var list = new JsonArray();
        foreach (string id in PublicRuntime.PublicLayerIds)
        {
            if (LayerCatalog.TryGet(id, out LayerInfo? info))
            {
                list.Add(new JsonObject { ["id"] = info.Id, ["title"] = info.Title, ["defaultColor"] = info.DefaultColor, ["defaultVisible"] = info.DefaultVisible, ["tiled"] = false });
            }
        }

        return list;
    }

    /// <summary><c>guest.help</c>: only the commands the visitor may run.</summary>
    public static JsonArray Help(PublicRuntime runtime, PublicTier tier, int security)
    {
        var list = new JsonArray();
        foreach (string name in PublicCommandPolicy.AllowedFor(tier, security, runtime.Options))
        {
            list.Add(new JsonObject { ["name"] = name, ["summary"] = Summary(name) });
        }

        return list;
    }

    private static string Summary(string name) => name switch
    {
        "guest.status" => "Version, tiers, guest policy flags and whether login is offered.",
        "guest.maps" => "The maps you may view.",
        "guest.layers" => "The layers you may see.",
        "guest.help" => "This list.",
        "live.entities" => "The creatures, objects and characters on a map.",
        "live.find" => "Find an entity by name.",
        "pick.public" => "Basic information about an entity you clicked.",
        "bookmarks.list" => "Saved places.",
        "me.characters" => "Your own online characters.",
        _ => "An action command (needs a Moderator or higher account).",
    };

    /// <summary><c>live.entities</c> for a public visitor: the projection of one map with this session's opaque ids.</summary>
    public static JsonObject LiveEntities(PublicRuntime runtime, PublicSession session, uint map, IReadOnlySet<string>? kinds, int limit)
    {
        PublicMapStatus status = runtime.Frames.Status(map);
        var entities = new JsonArray();
        var cells = new JsonArray();
        if (status.Projection is { } projection)
        {
            foreach (GuestEntity entity in projection.Entities)
            {
                if (entities.Count >= limit)
                {
                    break;
                }

                if (kinds is null || kinds.Contains(KindName(entity.Kind)))
                {
                    entities.Add(EntityJson(session, entity));
                }
            }

            foreach (GuestCell cell in projection.Cells)
            {
                cells.Add(new JsonObject { ["x"] = Math.Round(cell.X, 2), ["y"] = Math.Round(cell.Y, 2), ["n"] = cell.Count });
            }
        }

        return new JsonObject
        {
            ["map"] = map,
            ["connected"] = status.Connected,
            ["busy"] = status.Busy,
            ["reduced"] = status.Reduced,
            ["seq"] = status.Projection?.Seq ?? 0,
            ["count"] = entities.Count,
            ["entities"] = entities,
            ["playerCells"] = cells,
        };
    }

    /// <summary><c>live.find</c> for a public visitor: named entities of the projection whose name contains the text (case-insensitive).</summary>
    public static JsonObject LiveFind(PublicRuntime runtime, PublicSession session, uint map, string text)
    {
        PublicMapStatus status = runtime.Frames.Status(map);
        var found = new JsonArray();
        if (status.Projection is { } projection)
        {
            foreach (GuestEntity entity in projection.Entities)
            {
                if (found.Count >= 50)
                {
                    break;
                }

                if (!string.IsNullOrEmpty(entity.Name) && entity.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(EntityJson(session, entity));
                }
            }
        }

        return new JsonObject { ["map"] = map, ["query"] = text, ["count"] = found.Count, ["entities"] = found };
    }

    /// <summary><c>bookmarks.list</c> for a public visitor: the built-in bookmarks on a map guests may view.</summary>
    public static JsonArray BookmarksList(PublicRuntime runtime)
    {
        var maps = runtime.AllowedMaps.ToHashSet();
        var list = new JsonArray();
        foreach (Bookmark bookmark in runtime.Bookmarks)
        {
            if (maps.Contains(bookmark.Map))
            {
                list.Add(new JsonObject
                {
                    ["name"] = bookmark.Name,
                    ["map"] = bookmark.Map,
                    ["at"] = HostJson.Point(bookmark.Target),
                    ["distance"] = bookmark.Distance,
                    ["yaw"] = bookmark.Yaw,
                    ["pitch"] = bookmark.Pitch,
                    ["mode"] = ViewState.Name(bookmark.Mode),
                    ["builtin"] = true,
                });
            }
        }

        return list;
    }

    internal static JsonObject EntityJson(PublicSession session, GuestEntity entity)
    {
        var json = new JsonObject
        {
            ["id"] = GuestFrameProjector.OpaqueId(session.OpaqueKey, entity.Guid),
            ["kind"] = KindName(entity.Kind),
            ["at"] = new JsonArray(Math.Round(entity.X, 2), Math.Round(entity.Y, 2), Math.Round(entity.Z, 2)),
            ["orientation"] = Math.Round(entity.Orientation, 2),
        };
        if (entity.Kind != GuestKind.Character && entity.Entry != 0)
        {
            json["entry"] = entity.Entry;
        }

        if (!string.IsNullOrEmpty(entity.Name))
        {
            json["name"] = entity.Name;
        }

        if (entity.Hp is { } hp)
        {
            json["hp"] = Math.Round(hp, 1);
        }

        if (entity.Motion is not null)
        {
            json["motion"] = entity.Motion;
        }

        return json;
    }

    internal static string KindName(GuestKind kind) => kind switch
    {
        GuestKind.Character => "character",
        GuestKind.Creature => "creature",
        _ => "gameobject",
    };
}
