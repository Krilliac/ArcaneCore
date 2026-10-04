using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Objects;

namespace ArcaneCore.World.Gm.Npc;

/// <summary>
/// <c>.respawn</c> (the reference cores' command of that name) and the ArcaneCore-native <c>.spawninfo</c>
/// (docs/integration/gm-objects-npc-lane.md). <c>.respawn [#radius]</c> brings back the dead database creatures and the despawned database game
/// objects waiting on a respawn timer within the radius of the invoker, through the systems' own respawn paths (so a persisted creature respawn
/// row is deleted as for any respawn). <c>.spawninfo</c> only reads: what is placed near the invoker and in what respawn state, and a per-map summary.
/// </summary>
public sealed class GmSpawnCommands : ICommandGroup
{
    /// <summary>The radius of <c>.respawn</c> when none is given (yards).</summary>
    public const float DefaultRespawnRadius = 100f;

    /// <summary>Rows <c>.spawninfo creature|gameobject</c> print before they say how many they left out.</summary>
    public const int MaxRows = 20;

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("respawn", AccountSecurity.GameMaster, "Syntax: .respawn [#radius]\nRespawn the dead creatures and the despawned game objects within #radius yards (default 100) without waiting for their timers. Temporary objects and chests kept by an instance are left alone.", Respawn, RetailLevel: 3),
        new ChatCommand("spawninfo", AccountSecurity.GameMaster, "Syntax: .spawninfo $subcommand\nType .spawninfo to see the list of possible subcommands. Read-only.", Children:
        [
            new ChatCommand("creature", AccountSecurity.GameMaster, "Syntax: .spawninfo creature [#radius]\nList the creatures within #radius yards (default 40) with their spawn origin and respawn state, nearest first.", Creatures, RetailLevel: 2),
            new ChatCommand("gameobject", AccountSecurity.GameMaster, "Syntax: .spawninfo gameobject [#radius]\nList the game objects within #radius yards (default 40) with their spawn origin and respawn state, nearest first.", GameObjects, RetailLevel: 2),
            new ChatCommand("summary", AccountSecurity.GameMaster, "Syntax: .spawninfo summary\nCount the creatures and game objects of this map by state, including the respawn times kept for unloaded grids.", Summary, RetailLevel: 2),
        ], RetailLevel: 2),
    ];

    public static string Respawned(int creatures, int objects, float radius)
        => string.Create(CultureInfo.InvariantCulture, $"Respawned {creatures} creature(s) and {objects} game object(s) within {radius:0.##} yards.");

    private static bool Respawn(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractOptFloat(out float radius, DefaultRespawnRadius) || !args.IsEmpty || !(radius > 0f) || radius > GmObjectCommands.MaxNearRadius)
        {
            return false;
        }

        Player player = context.Player;
        int creatures = 0;
        if (GmNpcCommands.SystemOf(context) is { } creatureSystem)
        {
            foreach (Creature creature in creatureSystem.Creatures
                .Where(c => c.Spawn is not null && c.DeathState != CreatureDeathState.Alive && !c.IsPet && GmDistance.Between(player, c) <= radius).ToArray())
            {
                creatureSystem.ForceRespawn(creature);
                creatures++;
            }
        }

        int objects = 0;
        if (GmObjectCommands.SystemOf(context) is { } objectSystem)
        {
            foreach (GameObject go in objectSystem.GameObjects.Where(g => !g.IsSpawned && GmDistance.Between(player, g) <= radius).ToArray())
            {
                if (objectSystem.RespawnPending(go))
                {
                    objects++;
                }
            }
        }

        context.Reply(Respawned(creatures, objects, radius));
        return true;
    }

    private static bool Creatures(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractOptFloat(out float radius, 40f) || !args.IsEmpty || !(radius > 0f) || radius > GmObjectCommands.MaxNearRadius)
        {
            return false;
        }

        if (GmNpcCommands.SystemOf(context) is not { } system)
        {
            return true;
        }

        Player player = context.Player;
        List<(Creature Creature, float Distance)> near = [.. system.Creatures
            .Where(c => c is not { IsPet: true })
            .Select(c => (Creature: c, Distance: GmDistance.Between(player, c)))
            .Where(t => t.Distance <= radius)
            .OrderBy(t => t.Distance).ThenBy(t => t.Creature.Guid.Counter)];
        context.Reply(string.Create(CultureInfo.InvariantCulture, $"Creatures within {radius:0.##} yards: {near.Count}"));
        foreach ((Creature creature, float distance) in near.Take(MaxRows))
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture,
                $"{creature.Guid.Counter} entry {creature.Template.Entry} {creature.Template.Name}: {Origin(creature)}, {State(system, creature)}, {distance:F1} yards"));
        }

        if (near.Count > MaxRows)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"... {near.Count - MaxRows} more not shown."));
        }

        return true;
    }

    private static bool GameObjects(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractOptFloat(out float radius, 40f) || !args.IsEmpty || !(radius > 0f) || radius > GmObjectCommands.MaxNearRadius)
        {
            return false;
        }

        if (GmObjectCommands.SystemOf(context) is not { } system)
        {
            return true;
        }

        Player player = context.Player;
        List<(GameObject Go, float Distance)> near = [.. system.GameObjects
            .Select(g => (Go: g, Distance: GmDistance.Between(player, g)))
            .Where(t => t.Distance <= radius)
            .OrderBy(t => t.Distance).ThenBy(t => t.Go.Guid.Counter)];
        context.Reply(string.Create(CultureInfo.InvariantCulture, $"Game objects within {radius:0.##} yards: {near.Count}"));
        foreach ((GameObject go, float distance) in near.Take(MaxRows))
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture,
                $"{go.Guid.Counter} entry {go.Entry} {go.Template.Name}: {Origin(go)}, {State(system, go)}, {distance:F1} yards"));
        }

        if (near.Count > MaxRows)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"... {near.Count - MaxRows} more not shown."));
        }

        return true;
    }

    private static bool Summary(CommandContext context, string text)
    {
        if (text.Trim().Length != 0)
        {
            return false;
        }

        if (context.Player.Map is not { } map)
        {
            return true;
        }

        if (GmNpcCommands.SystemOf(context) is { } creatures)
        {
            Creature[] all = [.. creatures.Creatures];
            int database = all.Count(c => c.Spawn is not null);
            context.Reply(string.Create(CultureInfo.InvariantCulture,
                $"Creatures on map {map.MapId}: {all.Length} loaded ({database} database, {all.Length - database} temporary), " +
                $"{all.Count(c => c.DeathState == CreatureDeathState.Alive)} alive, {all.Count(c => c.DeathState == CreatureDeathState.Corpse)} corpses, " +
                $"{all.Count(c => c.DeathState == CreatureDeathState.Dead)} dead; {creatures.DormantRespawnCount} respawn time(s) kept for unloaded grids."));
        }

        if (GmObjectCommands.SystemOf(context) is { } objects)
        {
            GameObject[] all = [.. objects.GameObjects];
            int database = all.Count(g => g.Spawn is not null);
            context.Reply(string.Create(CultureInfo.InvariantCulture,
                $"Game objects on map {map.MapId}: {all.Length} loaded ({database} database, {all.Length - database} runtime), " +
                $"{all.Count(g => g.IsSpawned)} spawned, {all.Count(g => !g.IsSpawned && objects.RespawnRemainingMs(g) is not null)} waiting to respawn, " +
                $"{all.Count(g => !g.IsSpawned && objects.RespawnRemainingMs(g) is null)} despawned with no timer; {objects.DormantRespawnCount} respawn time(s) kept for unloaded grids."));
        }

        return true;
    }

    private static string Origin(Creature creature)
        => creature.Spawn is { } spawn ? string.Create(CultureInfo.InvariantCulture, $"database spawn {spawn.Guid}") : "temporary";

    private static string Origin(GameObject go)
        => go.Spawn is { } spawn ? string.Create(CultureInfo.InvariantCulture, $"database spawn {spawn.Guid}") : "runtime";

    private static string State(CreatureMapSystem system, Creature creature)
        => system.RespawnRemainingMs(creature) is { } ms
            ? string.Create(CultureInfo.InvariantCulture, $"{GmNpcCommands.Describe(creature.DeathState)}, respawns in {(ms + 999) / 1000} s")
            : "alive";

    private static string State(GameObjectMapSystem system, GameObject go)
        => system.RespawnRemainingMs(go) is { } ms
            ? string.Create(CultureInfo.InvariantCulture, $"despawned, respawns in {(ms + 999) / 1000} s")
            : go.IsSpawned ? "spawned" : "despawned, no respawn timer";
}
