using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps.SpawnGroups;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;

namespace ArcaneCore.World.Gm.Objects;

/// <summary>
/// <c>.spawngroup</c>: inspection of the cmangos spawn groups (and the pools) of the invoker's map. cmangos-classic has no such root (its
/// <c>.npc formation info</c> and <c>.pool</c> show parts of it, SEC_GAMEMASTER: Chat.cpp:506-589); the wording is ArcaneCore's. Read-only,
/// GameMaster (retail level 3, as those).
/// <list type="bullet">
/// <item><c>.spawngroup list [creature|gameobject]</c>: the groups of the map with their members in the world and their maximum.</item>
/// <item><c>.spawngroup info [#group]</c>: a group's definition, condition, formation and every member's state; without an id, the group of the
/// selected creature.</item>
/// <item><c>.spawngroup spawn #guid [creature|gameobject]</c>: the group and the pool a database spawn belongs to, and their state.</item>
/// </list>
/// </summary>
public sealed class GmSpawnGroupCommands : ICommandGroup
{
    /// <summary>Rows <c>.spawngroup list</c> prints before it says how many it left out.</summary>
    public const int MaxListRows = 40;

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("spawngroup", AccountSecurity.GameMaster, "Syntax: .spawngroup $subcommand\nType .spawngroup to see the list of possible subcommands or .help spawngroup $subcommand to see info on subcommands.", Children:
        [
            new ChatCommand("list", AccountSecurity.GameMaster, "Syntax: .spawngroup list [creature|gameobject]\nList the spawn groups of your map: id, type, members in the world / maximum, name.", List, RetailLevel: 3),
            new ChatCommand("info", AccountSecurity.GameMaster, "Syntax: .spawngroup info [#group]\nShow a spawn group of your map (the selected creature's without an id): flags, condition, formation and what each member is doing.", Info, RetailLevel: 3),
            new ChatCommand("spawn", AccountSecurity.GameMaster, "Syntax: .spawngroup spawn #guid [creature|gameobject]\nShow the spawn group and the pool a database spawn of your map belongs to.", Spawn, RetailLevel: 3),
        ], RetailLevel: 3),
    ];

    public static string NoGroup(uint id) => string.Create(CultureInfo.InvariantCulture, $"Spawn group {id} is not on this map.");

    public static string NotASpawn(uint guid) => string.Create(CultureInfo.InvariantCulture, $"Spawn {guid} is neither in a spawn group nor in a pool on this map.");

    public const string NoSelection = "Select a creature of a spawn group, or give a group id.";

    private static CreatureMapSystem? Creatures(CommandContext context) => context.Player.Map?.FindUpdater<CreatureMapSystem>();

    private static GameObjectMapSystem? Objects(CommandContext context) => context.Player.Map?.FindUpdater<GameObjectMapSystem>();

    private static bool TryKind(string word, out SpawnGroupType? kind)
    {
        kind = word.ToLowerInvariant() switch
        {
            "" => null,
            "creature" or "c" or "npc" => SpawnGroupType.Creature,
            "gameobject" or "go" or "object" or "g" => SpawnGroupType.GameObject,
            _ => (SpawnGroupType)255,
        };
        return kind is null or SpawnGroupType.Creature or SpawnGroupType.GameObject;
    }

    private static bool List(CommandContext context, string text)
    {
        if (!TryKind(text.Trim(), out SpawnGroupType? kind))
        {
            return false;
        }

        var rows = new List<SpawnGroupReport>();
        if (kind is null or SpawnGroupType.Creature && Creatures(context) is { } creatures)
        {
            rows.AddRange(creatures.SpawnGroupIds.Select(creatures.DescribeSpawnGroup).OfType<SpawnGroupReport>());
        }

        if (kind is null or SpawnGroupType.GameObject && Objects(context) is { } objects)
        {
            rows.AddRange(objects.SpawnGroupIds.Select(objects.DescribeSpawnGroup).OfType<SpawnGroupReport>());
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture, $"{rows.Count} spawn group(s) on map {context.Player.MapId}:"));
        foreach (SpawnGroupReport row in rows.Take(MaxListRows))
        {
            context.Reply(ListLine(row));
        }

        if (rows.Count > MaxListRows)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"... and {rows.Count - MaxListRows} more (narrow it with creature or gameobject)."));
        }

        return true;
    }

    public static string ListLine(SpawnGroupReport row)
        => string.Create(CultureInfo.InvariantCulture,
            $"{row.Definition.Id} {(row.Definition.Type == SpawnGroupType.Creature ? "creature" : "gameobject")} {row.InWorld}/{row.EffectiveMaxCount} {row.Definition.Name}");

    private static bool Info(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        uint id;
        SpawnGroupReport? report = null;
        if (args.IsEmpty)
        {
            if (GmSelectedUnit.Of(context) is not Creature { Spawn: { } spawn } || Creatures(context) is not { } system || system.SpawnGroupIdOf(spawn.Guid) is 0)
            {
                context.Reply(NoSelection);
                return true;
            }

            id = system.SpawnGroupIdOf(spawn.Guid);
            report = system.DescribeSpawnGroup(id);
        }
        else
        {
            if (!args.ExtractUInt32(out id) || !args.IsEmpty)
            {
                return false;
            }

            report = Creatures(context)?.DescribeSpawnGroup(id) ?? Objects(context)?.DescribeSpawnGroup(id);
        }

        if (report is null)
        {
            context.Reply(NoGroup(id));
            return true;
        }

        foreach (string line in InfoLines(report))
        {
            context.Reply(line);
        }

        return true;
    }

    /// <summary>The lines of <c>.spawngroup info</c>.</summary>
    public static IEnumerable<string> InfoLines(SpawnGroupReport report)
    {
        SpawnGroupDefinition d = report.Definition;
        yield return string.Create(CultureInfo.InvariantCulture, $"Spawn group {d.Id}: {d.Name}");
        yield return string.Create(CultureInfo.InvariantCulture,
            $"Type {(d.Type == SpawnGroupType.Creature ? "creature" : "gameobject")}, in the world {report.InWorld}/{report.EffectiveMaxCount} (stored MaxCount {d.MaxCount}), flags {Flags(d.Flags)}");
        if (d.WorldStateCondition != 0 || d.WorldStateExpression != 0)
        {
            yield return string.Create(CultureInfo.InvariantCulture,
                $"Condition {(d.WorldStateCondition != 0 ? $"conditions entry {d.WorldStateCondition}" : $"world-state expression {d.WorldStateExpression} (not supported)")}: {(report.ConditionHolds ? "holds" : "does not hold")}");
        }

        if (d.RandomEntries.Count > 0)
        {
            yield return "Entries: " + string.Join(", ", d.RandomEntries.Select(e => string.Create(CultureInfo.InvariantCulture,
                $"{e.Entry} (min {e.MinCount}, max {e.MaxCount}, chance {e.Chance})")));
        }

        if (report.Formation is { } formation)
        {
            yield return "Formation: " + formation;
        }

        foreach (SpawnGroupMemberReport m in report.Members)
        {
            yield return string.Create(CultureInfo.InvariantCulture,
                $"  {m.Guid}{(m.SlotId >= 0 ? $" slot {m.SlotId}" : string.Empty)}{(m.Chance != 0 ? $" chance {m.Chance}" : string.Empty)}: {(m.Entry != 0 ? $"entry {m.Entry}, " : string.Empty)}{m.State}");
        }
    }

    private static string Flags(SpawnGroupFlags flags)
        => flags == SpawnGroupFlags.None ? "none" : string.Create(CultureInfo.InvariantCulture, $"{(uint)flags} ({flags})");

    private static bool Spawn(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint guid))
        {
            return false;
        }

        string rest = args.Rest.Trim();
        if (!TryKind(rest, out SpawnGroupType? kind))
        {
            return false;
        }

        bool shown = false;
        if (kind is null or SpawnGroupType.Creature && Creatures(context) is { } creatures)
        {
            shown |= ShowSpawn(context, "creature", guid, creatures.SpawnGroupIdOf(guid), creatures.DescribeSpawnGroup, creatures.DescribeSpawnPool(guid));
        }

        if ((!shown || kind is SpawnGroupType.GameObject) && kind is null or SpawnGroupType.GameObject && Objects(context) is { } objects)
        {
            shown |= ShowSpawn(context, "gameobject", guid, objects.SpawnGroupIdOf(guid), objects.DescribeSpawnGroup, objects.DescribeSpawnPool(guid));
        }

        if (!shown)
        {
            context.Reply(NotASpawn(guid));
        }

        return true;
    }

    private static bool ShowSpawn(CommandContext context, string kind, uint guid, uint groupId, Func<uint, SpawnGroupReport?> describe, SpawnPoolReport? pool)
    {
        if (groupId == 0 && pool is null)
        {
            return false;
        }

        if (groupId != 0 && describe(groupId) is { } report)
        {
            SpawnGroupMemberReport member = report.Members.First(m => m.Guid == guid);
            context.Reply(string.Create(CultureInfo.InvariantCulture,
                $"{kind} spawn {guid}: spawn group {groupId} \"{report.Definition.Name}\" ({report.InWorld}/{report.EffectiveMaxCount} in the world); this one: {(member.Entry != 0 ? $"entry {member.Entry}, " : string.Empty)}{member.State}"));
        }

        if (pool is not null)
        {
            context.Reply(PoolLine(kind, guid, pool));
        }

        return true;
    }

    /// <summary>The pool line of <c>.spawngroup spawn</c>.</summary>
    public static string PoolLine(string kind, uint guid, SpawnPoolReport pool)
        => pool.PoolId == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{kind} spawn {guid}: pooled, but its pool row was dropped at load; it never spawns")
            : string.Create(CultureInfo.InvariantCulture,
                $"{kind} spawn {guid}: pool {pool.PoolId} \"{pool.Description}\" ({pool.SpawnedCount}/{pool.MaxLimit} out){(pool.MotherPool != 0 ? $", in mother pool {pool.MotherPool} \"{pool.MotherDescription}\"" : string.Empty)}; this one is {(pool.SpawnOut ? "out" : "not chosen")}");
}
