using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Gm.Objects;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Atlas;

/// <summary>
/// The <c>.atlas</c> commands (docs/areas/atlas-actions.md, "The .atlas commands"): the click-driven leaves the Atlas tool's templates expand
/// to. Every leaf takes its numbers on the line (no hidden state: the point and the unit are arguments, written by the daemon from the
/// request), is GameMaster level (retail level 3), and works for a console-like invoker, which has no character. A unit is named by its guid as
/// <c>0x</c> and hex digits. Failures a tool needs to tell apart set <see cref="CommandContext.Fail"/> (<c>map-not-loaded</c>,
/// <c>no-such-entry</c>, <c>target-gone</c>, <c>security</c>); a malformed line is a syntax failure.
/// <para>
/// The root name cannot shadow any other: the command table resolves a word to the first root it is a prefix of, and no other root is a
/// prefix of <c>atlas</c> or begins with it (a test holds that for every root). World thread only.
/// </para>
/// </summary>
public sealed class AtlasCommands : ICommandGroup
{
    /// <inheritdoc />
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("atlas", AccountSecurity.GameMaster, "Atlas tool commands (used by the map tool's action templates).", Children:
        [
            new ChatCommand("place", AccountSecurity.GameMaster, "Place a temporary creature or game object at a point.", Children:
            [
                new ChatCommand("creature", AccountSecurity.GameMaster,
                    "Syntax: .atlas place creature #entry #map $x $y $z [$o]\nPlace a temporary creature (not saved) at the point on the map.", PlaceCreature, RetailLevel: 3),
                new ChatCommand("gobject", AccountSecurity.GameMaster,
                    "Syntax: .atlas place gobject #entry #map $x $y $z [$o]\nPlace a temporary game object (not saved) at the point on the map.", PlaceGameObject, RetailLevel: 3),
            ]),
            new ChatCommand("tele", AccountSecurity.GameMaster,
                "Syntax: .atlas tele 0xGUID #map $x $y $z\nTeleport the player to the point. A GameMaster may move characters of their own account only; another account's character needs Administrator.",
                Tele, RetailLevel: 3),
            new ChatCommand("moveto", AccountSecurity.GameMaster,
                "Syntax: .atlas moveto 0xGUID $x $y $z\nWalk the creature to the point.", MoveTo, RetailLevel: 3),
            new ChatCommand("debug", AccountSecurity.GameMaster,
                "Syntax: .atlas debug 0xGUID\nPrint one JSON object with the creature's aggro radius, leash, home, spawn, current path points, threat list and AI.",
                AtlasDebugCommands.Debug, RetailLevel: 3),
            new ChatCommand("spawns", AccountSecurity.GameMaster,
                "Syntax: .atlas spawns #map $x $y $radius\nPrint one JSON object listing the creature and game object spawn points within the radius (at most 500 rows).",
                AtlasDebugCommands.Spawns, RetailLevel: 3),
        ]),
    ];

    // ---- place -----------------------------------------------------------------------------------------------------------------

    private static bool PlaceCreature(CommandContext context, string args)
    {
        if (!TryPlace(args, out PlaceArgs place))
        {
            return false;
        }

        if (RunningMap(context, place.MapId) is not { } map)
        {
            return true;
        }

        CreatureWorldFeature feature = context.Session.Services.GetRequiredService<CreatureWorldFeature>();
        if (feature.Content.FindTemplate(place.Entry) is not { } template)
        {
            context.Fail("no-such-entry");
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"Creature template {place.Entry} does not exist."));
            return true;
        }

        (float x, float y, float z, float o) = (place.Position[0], place.Position[1], place.Position[2], place.Position[3]);
        Creature creature = feature.GetOrCreateSystem(map).SpawnTemporary(template, x, y, z, o);
        context.Reply(string.Create(CultureInfo.InvariantCulture, $"Placed {template.Name} ({creature.Guid}) at {x:0.##} {y:0.##} {z:0.##} on map {place.MapId}."));
        return true;
    }

    private static bool PlaceGameObject(CommandContext context, string args)
    {
        if (!TryPlace(args, out PlaceArgs place))
        {
            return false;
        }

        if (RunningMap(context, place.MapId) is not { } map)
        {
            return true;
        }

        if (map.FindUpdater<GameObjectMapSystem>() is not { } system)
        {
            context.Fail("map-not-loaded");
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"Map {place.MapId} has no game object system."));
            return true;
        }

        if (system.FindTemplate(place.Entry) is not { } template)
        {
            context.Fail("no-such-entry");
            context.Reply(GmObjectCommands.TemplateMissing(place.Entry));
            return true;
        }

        (float x, float y, float z, float o) = (place.Position[0], place.Position[1], place.Position[2], place.Position[3]);
        if (system.Summon(place.Entry, x, y, z, o, 0) is not { } placed)
        {
            context.Fail("failed");
            context.Reply("The game object could not be placed there.");
            return true;
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture, $"Placed {template.Name} ({placed.Guid}) at {x:0.##} {y:0.##} {z:0.##} on map {place.MapId}."));
        return true;
    }

    /// <summary><c>#entry #map $x $y $z [$o]</c>: five or six tokens, the numbers finite, the position inside the map. <see cref="PlaceArgs.Position"/> always has four floats (orientation 0 when absent).</summary>
    private static bool TryPlace(string args, out PlaceArgs place)
    {
        place = default;
        string[] tokens = AtlasArgs.Tokens(args);
        if (tokens.Length is not (5 or 6)
            || !AtlasArgs.TryUInt(tokens[0], out uint entry) || entry == 0
            || !AtlasArgs.TryUInt(tokens[1], out uint mapId)
            || !AtlasArgs.TryFloat(tokens[2], out float x) || !AtlasArgs.TryFloat(tokens[3], out float y) || !AtlasArgs.TryFloat(tokens[4], out float z))
        {
            return false;
        }

        float o = 0f;
        if (tokens.Length == 6 && !AtlasArgs.TryFloat(tokens[5], out o))
        {
            return false;
        }

        if (!GridDefines.IsValidMapCoord(x, y, z, o))
        {
            return false;
        }

        place = new PlaceArgs(entry, mapId, [x, y, z, o]);
        return true;
    }

    private readonly record struct PlaceArgs(uint Entry, uint MapId, float[] Position);

    /// <summary>The running map instance for the point: the instance the action's click named (when it is on this map), else the continent. Fails <c>map-not-loaded</c> when none runs.</summary>
    private static Map? RunningMap(CommandContext context, uint mapId)
    {
        uint instance = GmActionContext.Of(context)?.Point is { } point && point.Map == mapId ? point.Instance : 0;
        Map? map = context.World.FindMap(mapId, instance);
        if (map is null || map.IsUnloaded)
        {
            context.Fail("map-not-loaded");
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"Map {mapId} is not running."));
            return null;
        }

        return map;
    }

    // ---- tele ------------------------------------------------------------------------------------------------------------------

    private static bool Tele(CommandContext context, string args)
    {
        string[] tokens = AtlasArgs.Tokens(args);
        if (tokens.Length != 5
            || !AtlasArgs.TryGuid(tokens[0], out ulong guid)
            || !AtlasArgs.TryUInt(tokens[1], out uint mapId)
            || !AtlasArgs.TryFloat(tokens[2], out float x) || !AtlasArgs.TryFloat(tokens[3], out float y) || !AtlasArgs.TryFloat(tokens[4], out float z))
        {
            return false;
        }

        if (GmActionContext.FindUnit(context, guid) is not { } unit)
        {
            context.Fail("target-gone");
            context.Reply(GmStrings.SelectCharOrCreature);
            return true;
        }

        if (unit is not Player target)
        {
            context.Fail("not-a-player");
            context.Reply("Only a player can be teleported with this command.");
            return true;
        }

        // "Self" is the account: the actor's account id (the web ticket's, or the configured LocalAccountId) equals the target character's account id.
        // Never a name: a name in a request is untrusted, and two characters may share one.
        bool own = GmActionContext.Of(context)?.ActorAccountId is { } actorAccount && actorAccount == target.AccountId;
        if (!own && context.Security < AccountSecurity.Administrator)
        {
            context.Fail("security");
            context.Reply(GmStrings.SecurityTooLow);
            return true;
        }

        return TeleportCommands.GoHelper(context, target, mapId, x, y, z, target.Orientation);
    }

    // ---- moveto ----------------------------------------------------------------------------------------------------------------

    private static bool MoveTo(CommandContext context, string args)
    {
        string[] tokens = AtlasArgs.Tokens(args);
        if (tokens.Length != 4
            || !AtlasArgs.TryGuid(tokens[0], out ulong guid)
            || !AtlasArgs.TryFloat(tokens[1], out float x) || !AtlasArgs.TryFloat(tokens[2], out float y) || !AtlasArgs.TryFloat(tokens[3], out float z)
            || !GridDefines.IsValidMapCoord(x, y, z, 0f))
        {
            return false;
        }

        if (GmActionContext.FindUnit(context, guid) is not Creature { Map: { } map } creature)
        {
            context.Fail("target-gone");
            context.Reply(GmStrings.SelectCharOrCreature);
            return true;
        }

        if (!creature.IsAlive)
        {
            context.Fail("dead");
            context.Reply("The creature is dead.");
            return true;
        }

        context.Session.Services.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(map).MoveTo(creature, x, y, z, run: false, finalOrientation: null);
        context.Reply(string.Create(CultureInfo.InvariantCulture, $"{creature.Template.Name} ({creature.Guid}) walks to {x:0.##} {y:0.##} {z:0.##}."));
        return true;
    }
}

/// <summary>Argument parsing shared by the <c>.atlas</c> leaves. Pure.</summary>
internal static class AtlasArgs
{
    /// <summary>The whitespace-separated tokens of <paramref name="args"/>.</summary>
    public static string[] Tokens(string args) => args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>An unsigned decimal integer, digits only.</summary>
    public static bool TryUInt(string token, out uint value) => uint.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    /// <summary>A finite decimal number.</summary>
    public static bool TryFloat(string token, out float value)
        => float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    /// <summary>A non-zero guid as <c>0x</c> and 1 to 16 hex digits.</summary>
    public static bool TryGuid(string token, out ulong guid)
    {
        guid = 0;
        return token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && token.Length is > 2 and <= 18
            && ulong.TryParse(token.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out guid)
            && guid != 0;
    }
}
