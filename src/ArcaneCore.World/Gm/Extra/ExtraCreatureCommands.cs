using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Inspect;
using ArcaneCore.World.Gm.Npc;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Extra;

/// <summary>
/// More creature manipulators under <c>.ai</c>: <c>goto</c> (walk or run to a point), <c>face</c> (turn to you or to an angle),
/// <c>wander</c> (make random movement around home the default) and <c>distract</c> (stand and look around). No vmangos equivalent.
/// </summary>
public sealed class ExtraAiCommands : ICommandExtension
{
    public string Path => "ai";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("goto", AccountSecurity.GameMaster,
            "Syntax: .ai goto #x #y [#z] [run]\nMake the selected creature walk (or run) to a point on your map (z defaults to yours). The point must be within 200 yards of it.",
            Goto, RetailLevel: 3),
        new("face", AccountSecurity.GameMaster,
            "Syntax: .ai face [#radians]\nTurn the selected creature to face you, or to an absolute orientation in radians.",
            Face, RetailLevel: 3),
        new("wander", AccountSecurity.GameMaster,
            "Syntax: .ai wander [#radius]\nMake wandering around its home (default 5 yards, 1-50) the selected creature's default movement, starting now. .ai idle turns it off.",
            Wander, RetailLevel: 3),
        new("distract", AccountSecurity.GameMaster,
            "Syntax: .ai distract [#seconds]\nMake the selected creature stop and look around for #seconds (default 5, at most 60), as a distract spell does.",
            Distract, RetailLevel: 3),
    ];

    /// <summary>Farthest a creature is sent by <c>.ai goto</c>.</summary>
    public const float MaxGotoDistance = 200f;

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private static bool TrySelected(CommandContext context, out Creature creature, out CreatureMapSystem system)
    {
        creature = null!;
        system = null!;
        if (GmNpcCommands.SystemOf(context) is not { } s || InspectCommands.TargetCreature(context) is not { } c)
        {
            return false;
        }

        if (c.DeathState != CreatureDeathState.Alive)
        {
            context.Reply(GmNpcCommands.NotAlive(c.Template.Name));
            return false;
        }

        creature = c;
        system = s;
        return true;
    }

    private static bool TryFloat(string? text, out float value)
        => float.TryParse(text, NumberStyles.Float, C, out value) && float.IsFinite(value);

    private static bool Goto(CommandContext context, string text)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool run = words.Length > 0 && words[^1].Equals("run", StringComparison.OrdinalIgnoreCase);
        if (run) words = words[..^1];
        if (words.Length is < 2 or > 3 || !TryFloat(words[0], out float x) || !TryFloat(words[1], out float y)) return false;
        float z = context.Player.Z;
        if (words.Length == 3 && !TryFloat(words[2], out z)) return false;
        if (!TrySelected(context, out Creature creature, out CreatureMapSystem system)) return true;
        float dx = x - creature.X, dy = y - creature.Y, dz = z - creature.Z;
        float distance = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        if (distance > MaxGotoDistance)
        {
            context.Reply(string.Create(C, $"That point is {distance:F0} yards from {creature.Template.Name}; at most {MaxGotoDistance:F0}."));
            return true;
        }

        system.MoveTo(creature, x, y, z, run, finalOrientation: null);
        context.Reply(string.Create(C, $"{InspectCommands.Describe(creature)} {(run ? "runs" : "walks")} to ({x:F1}, {y:F1}, {z:F1}), {distance:F1} yd."));
        return true;
    }

    private static bool Face(CommandContext context, string text)
    {
        string arg = text.Trim();
        float angle = 0;
        if (arg.Length != 0 && !TryFloat(arg, out angle)) return false;
        if (!TrySelected(context, out Creature creature, out CreatureMapSystem system)) return true;
        Player me = context.Player;
        if (arg.Length == 0)
        {
            angle = MathF.Atan2(me.Y - creature.Y, me.X - creature.X);
        }

        system.SetFacingTo(creature, angle);
        context.Reply(string.Create(C, $"{InspectCommands.Describe(creature)} faces {creature.Orientation:F2} rad{(arg.Length == 0 ? " (you)" : string.Empty)}."));
        return true;
    }

    private static bool Wander(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractOptFloat(out float radius, 5f) || !args.IsEmpty || !float.IsFinite(radius) || radius < 1 || radius > 50) return false;
        if (!TrySelected(context, out Creature creature, out CreatureMapSystem system)) return true;
        system.SetDefaultRandomMovement(creature, radius);
        context.Reply(string.Create(C, $"{InspectCommands.Describe(creature)} wanders within {radius:0.#} yards of its home."));
        return true;
    }

    private static bool Distract(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractOptUInt32(out uint seconds, 5) || !args.IsEmpty || seconds is 0 or > 60) return false;
        if (!TrySelected(context, out Creature creature, out _)) return true;
        creature.Motion.MoveDistract(seconds * 1000);
        context.Reply(string.Create(C, $"{InspectCommands.Describe(creature)} is distracted for {seconds} s."));
        return true;
    }
}

/// <summary>
/// <c>.creature despawntemp [#radius]</c>: remove the temporary creatures (GM clones and placements, script summons; not pets, guardians
/// or totems, not database spawns) around you. The cleanup for <c>.creature clone</c>.
/// </summary>
public sealed class CreatureDespawnTempCommand : ICommandExtension
{
    public string Path => "creature";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("despawntemp", AccountSecurity.GameMaster,
            "Syntax: .creature despawntemp [#radius]\nRemove the temporary creatures (clones, .npc add without save, script summons; not pets, guardians, totems or database spawns) within #radius yards of you (default 30, at most 200).",
            DespawnTemp, RetailLevel: 3),
    ];

    private static bool DespawnTemp(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractOptFloat(out float radius, 30f) || !args.IsEmpty || !float.IsFinite(radius) || radius <= 0 || radius > 200) return false;
        if (GmNpcCommands.SystemOf(context) is not { } system) return true;
        Player me = context.Player;
        float r2 = radius * radius;
        Creature[] victims =
        [
            .. system.Creatures.Where(c => c.Spawn is null && !c.IsPet && !c.IsTotem
                && ((c.X - me.X) * (c.X - me.X)) + ((c.Y - me.Y) * (c.Y - me.Y)) + ((c.Z - me.Z) * (c.Z - me.Z)) <= r2),
        ];
        foreach (Creature creature in victims)
        {
            system.Despawn(creature);
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture, $"Removed {victims.Length} temporary creature(s) within {radius:0.#} yards."));
        return true;
    }
}

/// <summary>
/// <c>.inspect loot</c> (the selected creature's loot: owner, permission, gold and every item with its state) and
/// <c>.debug sessions [#count]</c> (connected sessions: players and bots, client latency and the sessions with the most queued outbound
/// world packets). Read-only, no vmangos equivalent.
/// </summary>
public sealed class ExtraLootSessionCommands : ICommandExtension
{
    public string Path => "inspect";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("loot", AccountSecurity.GameMaster,
            "Syntax: .inspect loot\nShow the selected creature's loot: owner, permission, recipients, gold and every item with count, quest/per-player flags, looted and roll state.",
            Loot, RetailLevel: 3),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private static bool Loot(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        if (InspectCommands.TargetCreature(context) is not { } creature) return true;
        LootBag? bag = creature.Map is { } map ? context.Session.Services.GetService<GameObjectLootFeature>()?.FindSystem(map)?.Loot?.FindLoot(creature.Guid) : null;
        if (bag is null)
        {
            context.Reply($"{InspectCommands.Describe(creature)} has no loot.");
            return true;
        }

        string owner = bag.Owner.IsEmpty ? "none" : context.World.FindOnlinePlayer(bag.Owner)?.Name ?? bag.Owner.ToString();
        context.Reply(string.Create(C,
            $"{InspectCommands.Describe(creature)} loot ({bag.Type}): owner {owner}, permission {bag.Permission}, {bag.Recipients.Count} recipients, {bag.Viewers.Count} viewing, gold {bag.Gold}, {bag.Items.Count} items{(bag.IsClosed ? ", closed" : string.Empty)}{(bag.RollsStarted ? ", rolls started" : string.Empty)}"));
        foreach (LootItem item in bag.Items)
        {
            string state = item.IsLooted ? "looted" : item.RollActive ? "rolling" : item.LootedBy.Count > 0 ? $"taken by {item.LootedBy.Count}" : "available";
            context.Reply(string.Create(C,
                $"  [{item.Slot}] item {item.ItemId} x{item.Count}{(item.IsQuestItem ? " quest" : string.Empty)}{(item.IsPerPlayer ? " per-player" : string.Empty)}{(item.IsUnderThreshold ? string.Empty : " over threshold")}: {state}"));
        }

        return true;
    }
}

/// <summary><c>.debug sessions [#count]</c>.</summary>
public sealed class ExtraSessionDebugCommands : ICommandExtension
{
    public string Path => "debug";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("sessions", AccountSecurity.GameMaster,
            "Syntax: .debug sessions [#count]\nShow the in-world sessions: players and bots, client latency (median, p90, max over the players) and the sessions with the most queued outbound world packets (default 10 rows, at most 60).",
            Sessions, RetailLevel: 3),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private static bool Sessions(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractOptUInt32(out uint count, 10) || !args.IsEmpty || count is 0 or > 60) return false;
        var players = context.World.OnlinePlayers.ToList();
        var sessions = players.Select(p => (Player: p, Session: p.Session as WorldSession)).Where(t => t.Session is not null).ToList();
        int bots = players.Count(p => p.Session.IsPlayerbot);
        int[] latency = [.. sessions.Where(t => !t.Player.Session.IsPlayerbot).Select(t => t.Session!.LatencyMs).Order()];
        string lat = latency.Length == 0
            ? "no players"
            : string.Create(C, $"median {latency[latency.Length / 2]} ms, p90 {latency[Math.Min(latency.Length - 1, (int)(latency.Length * 0.9))]} ms, max {latency[^1]} ms");
        long queuedBytes = sessions.Sum(t => t.Session!.QueuedWorldBytes);
        int queuedPackets = sessions.Sum(t => t.Session!.QueuedWorldPackets);
        context.Reply(string.Create(C,
            $"{players.Count} in world: {players.Count - bots} players, {bots} bots; latency {lat}; queued outbound {queuedPackets} packets / {queuedBytes / 1024.0:F1} KiB"));
        foreach ((Player player, WorldSession? session) in sessions.OrderByDescending(t => t.Session!.QueuedWorldBytes).ThenByDescending(t => t.Session!.LatencyMs).Take((int)count))
        {
            context.Reply(string.Create(C,
                $"  {player.Name}{(player.Session.IsPlayerbot ? " (bot)" : string.Empty)} map {player.MapId}: latency {session!.LatencyMs} ms, queued {session.QueuedWorldPackets} packets / {session.QueuedWorldBytes / 1024.0:F1} KiB"));
        }

        return true;
    }
}
