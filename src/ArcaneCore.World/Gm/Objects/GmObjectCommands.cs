using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Teleport;

namespace ArcaneCore.World.Gm.Objects;

/// <summary>
/// The <c>.gobject</c> commands (docs/integration/gm-objects-npc-lane.md). The reference cores persist <c>gobject add|delete|move|turn</c> into the
/// <c>gameobject</c> table; ArcaneCore has no spawn write path (the spawn stores are read-only content), so here the editing commands act on
/// runtime objects only: <c>add</c> places one, <c>delete</c>, <c>move</c> and <c>turn</c> refuse a database spawn, and nothing survives a restart.
/// <c>activate</c>, <c>near</c> and <c>info</c> work on any object. An object is named by the guid number <c>.gobject near</c> prints. The reply
/// texts are ArcaneCore's own wording.
/// </summary>
public sealed class GmObjectCommands : ICommandGroup
{
    /// <summary>Rows <c>.gobject near</c> prints before it says how many it left out.</summary>
    public const int MaxNearRows = 20;

    /// <summary>The largest radius <c>.gobject near</c> accepts (yards).</summary>
    public const float MaxNearRadius = 1000f;

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("gobject", AccountSecurity.GameMaster, "Syntax: .gobject $subcommand\nType .gobject to see the list of possible subcommands or .help gobject $subcommand to see info on subcommands.", Children:
        [
            new ChatCommand("add", AccountSecurity.GameMaster, "Syntax: .gobject add #entry [#despawnSeconds]\nPlace a game object where you stand. It is not saved: it is gone after a restart (or after #despawnSeconds).", Add, RetailLevel: 3),
            new ChatCommand("delete", AccountSecurity.GameMaster, "Syntax: .gobject delete #guid\nRemove a game object that was placed with .gobject add. Database spawns are refused.", Delete, RetailLevel: 3),
            new ChatCommand("move", AccountSecurity.GameMaster, "Syntax: .gobject move #guid [#x #y #z]\nMove a game object that was placed with .gobject add to the given place (or to yours).", Move, RetailLevel: 3),
            new ChatCommand("turn", AccountSecurity.GameMaster, "Syntax: .gobject turn #guid [#orientation]\nTurn a game object that was placed with .gobject add (radians; your own facing when omitted).", Turn, RetailLevel: 3),
            new ChatCommand("activate", AccountSecurity.GameMaster, "Syntax: .gobject activate #guid\nActivate a door, button or other object as if it had been used: its state flips and it returns after its auto-close time.", Activate, RetailLevel: 3),
            new ChatCommand("near", AccountSecurity.GameMaster, "Syntax: .gobject near [#radius]\nList the game objects within #radius yards (default 10), nearest first.", Near, RetailLevel: 2),
            new ChatCommand("info", AccountSecurity.GameMaster, "Syntax: .gobject info #guid\nShow the details of a game object.", Info, RetailLevel: 2),
        ], RetailLevel: 2),
    ];

    /// <summary>The game object system of the invoker's map; null when the invoker is in no map.</summary>
    internal static GameObjectMapSystem? SystemOf(CommandContext context) => context.Player.Map?.FindUpdater<GameObjectMapSystem>();

    /// <summary>The object with guid number <paramref name="counter"/> in the invoker's map (replies "not found" and returns null otherwise).</summary>
    private static GameObject? Find(CommandContext context, GameObjectMapSystem system, uint counter)
    {
        GameObject? found = system.GameObjects.FirstOrDefault(g => g.Guid.Counter == counter);
        if (found is null)
        {
            context.Reply(NotFound(counter));
        }

        return found;
    }

    public static string NotFound(uint counter) => string.Create(CultureInfo.InvariantCulture, $"Game object with guid {counter} was not found on this map.");

    public static string TemplateMissing(uint entry) => string.Create(CultureInfo.InvariantCulture, $"Game object template {entry} does not exist.");

    public static string DatabaseSpawn(uint counter) => string.Create(CultureInfo.InvariantCulture, $"Game object {counter} is a database spawn; in-game edits are not saved, so it is left alone.");

    public static string Spawned(string name, uint entry, uint counter, float x, float y, float z)
        => string.Create(CultureInfo.InvariantCulture, $"Game object {name} (entry {entry}, guid {counter}) placed at {x:F2} {y:F2} {z:F2}. It is not saved.");

    public static string Removed(string name, uint entry, uint counter) => string.Create(CultureInfo.InvariantCulture, $"Game object {name} (entry {entry}, guid {counter}) removed.");

    public static string Moved(uint counter, float x, float y, float z) => string.Create(CultureInfo.InvariantCulture, $"Game object {counter} moved to {x:F2} {y:F2} {z:F2}.");

    public static string Turned(uint counter, float orientation) => string.Create(CultureInfo.InvariantCulture, $"Game object {counter} turned to orientation {orientation:F4}.");

    public static string Activated(uint counter, GameObjectState state) => string.Create(CultureInfo.InvariantCulture, $"Game object {counter} activated; its state is now {state}.");

    public static string NotSpawned(uint counter) => string.Create(CultureInfo.InvariantCulture, $"Game object {counter} is not spawned.");

    private static bool Add(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint entry) || entry == 0 || !args.ExtractOptUInt32(out uint despawnSeconds, 0) || !args.IsEmpty)
        {
            return false;
        }

        if (SystemOf(context) is not { } system)
        {
            return true;
        }

        if (system.FindTemplate(entry) is not { } template)
        {
            context.Reply(TemplateMissing(entry));
            return true;
        }

        Player player = context.Player;
        GameObject? go = system.Summon(entry, player.X, player.Y, player.Z, player.Orientation, despawnSeconds);
        if (go is null)
        {
            context.Reply(TemplateMissing(entry));
            return true;
        }

        context.Reply(Spawned(template.Name, entry, go.Guid.Counter, go.X, go.Y, go.Z));
        return true;
    }

    private static bool Delete(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint counter) || !args.IsEmpty)
        {
            return false;
        }

        if (SystemOf(context) is not { } system || Find(context, system, counter) is not { } go)
        {
            return true;
        }

        if (go.Spawn is not null)
        {
            context.Reply(DatabaseSpawn(counter));
            return true;
        }

        system.Remove(go);
        context.Reply(Removed(go.Template.Name, go.Entry, counter));
        return true;
    }

    private static bool Move(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        Player player = context.Player;
        float x = player.X, y = player.Y, z = player.Z;
        if (!args.ExtractUInt32(out uint counter))
        {
            return false;
        }

        if (!args.IsEmpty && (!args.ExtractFloat(out x) || !args.ExtractFloat(out y) || !args.ExtractFloat(out z) || !args.IsEmpty))
        {
            return false;
        }

        if (SystemOf(context) is not { } system || Find(context, system, counter) is not { } go)
        {
            return true;
        }

        if (go.Spawn is not null)
        {
            context.Reply(DatabaseSpawn(counter));
            return true;
        }

        // The same check .go xyz / .tele apply (GridDefines.IsValidMapCoord, as TrinityCore's HandleGameObjectMoveCommand uses
        // MapManager::IsValidMapCoord): ExtractFloat accepts exponents, so "1e40" parses to infinity and "1e9" is far outside the map.
        if (!GridDefines.IsValidMapCoord(x, y, z, go.Orientation))
        {
            TeleportCommands.ReplyInvalid(context, x, y, player.MapId);
            return true;
        }

        if (!system.Relocate(go, x, y, z, go.Orientation))
        {
            context.Reply(NotSpawned(counter));
            return true;
        }

        context.Reply(Moved(counter, x, y, z));
        return true;
    }

    private static bool Turn(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint counter) || !args.ExtractOptFloat(out float orientation, context.Player.Orientation) || !args.IsEmpty)
        {
            return false;
        }

        if (SystemOf(context) is not { } system || Find(context, system, counter) is not { } go)
        {
            return true;
        }

        if (go.Spawn is not null)
        {
            context.Reply(DatabaseSpawn(counter));
            return true;
        }

        // A non-finite orientation (or one beyond ±4π, the IsValidMapCoord bound) would be written to the facing and rotation fields as is.
        if (!GridDefines.IsValidMapCoord(go.X, go.Y, go.Z, orientation))
        {
            TeleportCommands.ReplyInvalid(context, go.X, go.Y, context.Player.MapId);
            return true;
        }

        if (!system.Relocate(go, go.X, go.Y, go.Z, orientation))
        {
            context.Reply(NotSpawned(counter));
            return true;
        }

        context.Reply(Turned(counter, orientation));
        return true;
    }

    private static bool Activate(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint counter) || !args.IsEmpty)
        {
            return false;
        }

        if (SystemOf(context) is not { } system || Find(context, system, counter) is not { } go)
        {
            return true;
        }

        context.Reply(system.Activate(go) ? Activated(counter, go.State) : NotSpawned(counter));
        return true;
    }

    private static bool Near(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractOptFloat(out float radius, 10f) || !args.IsEmpty || !(radius > 0f) || radius > MaxNearRadius)
        {
            return false;
        }

        if (SystemOf(context) is not { } system)
        {
            return true;
        }

        Player player = context.Player;
        List<(GameObject Go, float Distance)> near = [.. system.GameObjects
            .Select(g => (Go: g, Distance: GmDistance.Between(player, g)))
            .Where(t => t.Distance <= radius)
            .OrderBy(t => t.Distance).ThenBy(t => t.Go.Guid.Counter)];
        context.Reply(string.Create(CultureInfo.InvariantCulture, $"Game objects within {radius:0.##} yards: {near.Count}"));
        foreach ((GameObject go, float distance) in near.Take(MaxNearRows))
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture,
                $"{go.Guid.Counter} entry {go.Entry} {go.Template.Name} ({go.Type}) {(go.IsSpawned ? "spawned" : "despawned")} at {go.X:F2} {go.Y:F2} {go.Z:F2}, {distance:F1} yards"));
        }

        if (near.Count > MaxNearRows)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"... {near.Count - MaxNearRows} more not shown."));
        }

        return true;
    }

    private static bool Info(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint counter) || !args.IsEmpty)
        {
            return false;
        }

        if (SystemOf(context) is not { } system || Find(context, system, counter) is not { } go)
        {
            return true;
        }

        context.Reply(Describe(system, go));
        return true;
    }

    /// <summary>The <c>.gobject info</c> lines of an object.</summary>
    public static string Describe(GameObjectMapSystem system, GameObject go)
    {
        string origin = go.Spawn is { } spawn ? string.Create(CultureInfo.InvariantCulture, $"database spawn {spawn.Guid}") : "runtime (not saved)";
        string respawn = system.RespawnRemainingMs(go) is { } ms
            ? string.Create(CultureInfo.InvariantCulture, $"respawns in {(ms + 999) / 1000} s")
            : go.IsSpawned ? "spawned" : "despawned, no respawn timer";
        return string.Create(CultureInfo.InvariantCulture,
            $"{go.Template.Name} entry {go.Entry} guid {go.Guid.Counter} type {go.Type} display {go.Template.DisplayId}\n" +
            $"{origin}; {respawn}\n" +
            $"state {go.State} loot state {go.LootState} flags {(uint)go.Flags}\n" +
            $"position {go.X:F2} {go.Y:F2} {go.Z:F2} orientation {go.Orientation:F4}");
    }
}
