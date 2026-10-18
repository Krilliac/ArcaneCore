using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Inspect;
using ArcaneCore.World.Gm.Npc;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Manipulate;

/// <summary>
/// The <c>.ai</c> manipulators (no vmangos equivalent): force the selected creature's AI and movement through the same
/// <see cref="CreatureMapSystem"/> entry points scripts use, so a forced state behaves like a scripted one (evade, attack, stop, home,
/// idle, come, follow, flee, cast, event, callhelp, reset), and <c>.creature clone</c> to copy the selected creature where you stand.
/// </summary>
public sealed class AiCommands : ICommandGroup
{
    /// <summary>Most clones one <c>.creature clone</c> places.</summary>
    public const int MaxClones = 25;

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("ai", AccountSecurity.GameMaster,
            "Syntax: .ai $subcommand\nForce the selected creature's AI or movement. Type .ai to see the list of possible subcommands.",
            Children:
            [
                new ChatCommand("evade", AccountSecurity.GameMaster, "Syntax: .ai evade\nMake the selected creature evade: leave combat, clear its threat and go home.", Evade, RetailLevel: 3),
                new ChatCommand("attack", AccountSecurity.GameMaster, "Syntax: .ai attack [$player]\nMake the selected creature attack you or the named online player on the same map.", Attack, RetailLevel: 3),
                new ChatCommand("stop", AccountSecurity.GameMaster, "Syntax: .ai stop\nStop the selected creature's combat where it stands (no evade, no return home).", Stop, RetailLevel: 3),
                new ChatCommand("home", AccountSecurity.GameMaster, "Syntax: .ai home\nSend the selected creature back to its home position.", Home, RetailLevel: 3),
                new ChatCommand("idle", AccountSecurity.GameMaster, "Syntax: .ai idle\nStop the selected creature's movement and keep it idle (its default movement resumes after an evade or respawn).", Idle, RetailLevel: 3),
                new ChatCommand("come", AccountSecurity.GameMaster, "Syntax: .ai come [run]\nMake the selected creature walk (or run) to where you stand.", Come, RetailLevel: 3),
                new ChatCommand("follow", AccountSecurity.GameMaster, "Syntax: .ai follow [#distance]\nMake the selected creature follow you (default 2 yards).", Follow, RetailLevel: 3),
                new ChatCommand("flee", AccountSecurity.GameMaster, "Syntax: .ai flee [#seconds]\nMake the selected creature flee from you (default 5 seconds, at most 60).", Flee, RetailLevel: 3),
                new ChatCommand("cast", AccountSecurity.GameMaster, "Syntax: .ai cast #spellid [self]\nMake the selected creature cast a spell (triggered) on you, or on itself with 'self'.", Cast, RetailLevel: 3),
                new ChatCommand("event", AccountSecurity.GameMaster, "Syntax: .ai event #eventtype [#misc]\nSend an AI event (the EventAI/SmartAI custom event types) to the selected creature, with you as the invoker.", Event, RetailLevel: 3),
                new ChatCommand("callhelp", AccountSecurity.GameMaster, "Syntax: .ai callhelp [#radius]\nMake the selected creature call for help within #radius yards (default 10, at most 100); prints how many answered.", CallHelp, RetailLevel: 3),
                new ChatCommand("reset", AccountSecurity.GameMaster, "Syntax: .ai reset\nKill and respawn the selected creature at once, which rebuilds its AI from its template.", Reset, RetailLevel: 3),
            ], RetailLevel: 3),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    private static bool TrySelected(CommandContext context, string args, bool mustBeAlive, out Creature creature, out CreatureMapSystem system)
    {
        creature = null!;
        system = null!;
        if (GmNpcCommands.SystemOf(context) is not { } s || InspectCommands.TargetCreature(context) is not { } c)
        {
            return false;
        }

        if (mustBeAlive && c.DeathState != CreatureDeathState.Alive)
        {
            context.Reply(GmNpcCommands.NotAlive(c.Template.Name));
            return false;
        }

        creature = c;
        system = s;
        return true;
    }

    private static void Done(CommandContext context, Creature creature, string what)
        => context.Reply($"{InspectCommands.Describe(creature)} {what}.");

    private static bool Evade(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        if (!TrySelected(context, args, true, out Creature creature, out CreatureMapSystem system)) return true;
        system.EnterEvadeMode(creature, "gm .ai evade");
        Done(context, creature, "evades");
        return true;
    }

    private static bool Attack(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        string? name = parsed.ExtractArg();
        if (!parsed.IsEmpty) return false;
        if (!TrySelected(context, args, true, out Creature creature, out CreatureMapSystem system)) return true;
        Player target = context.Player;
        if (name is not null && (!GmTargets.TryPlayer(context, name, out target) || !context.CanActOn(target)))
        {
            return true;
        }

        if (!ReferenceEquals(target.Map, creature.Map) || !target.IsAlive)
        {
            context.Reply($"{target.Name} is not alive on this map.");
            return true;
        }

        bool started = system.AttackStart(creature, target);
        Done(context, creature, started ? $"attacks {target.Name}" : $"cannot attack {target.Name}");
        return true;
    }

    private static bool Stop(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        if (!TrySelected(context, args, false, out Creature creature, out CreatureMapSystem system)) return true;
        system.StopCombatInPlace(creature);
        Done(context, creature, "stops fighting");
        return true;
    }

    private static bool Home(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        if (!TrySelected(context, args, true, out Creature creature, out _)) return true;
        creature.Motion.MoveTargetedHome(creature.Home);
        Done(context, creature, "goes home");
        return true;
    }

    private static bool Idle(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        if (!TrySelected(context, args, true, out Creature creature, out CreatureMapSystem system)) return true;
        system.MoveIdle(creature);
        Done(context, creature, "is idle");
        return true;
    }

    private static bool Come(CommandContext context, string args)
    {
        string text = args.Trim();
        if (text.Length != 0 && !text.Equals("run", StringComparison.OrdinalIgnoreCase)) return false;
        if (!TrySelected(context, args, true, out Creature creature, out CreatureMapSystem system)) return true;
        Player me = context.Player;
        if (!ReferenceEquals(me.Map, creature.Map)) return true;
        system.MoveTo(creature, me.X, me.Y, me.Z, run: text.Length != 0, finalOrientation: null);
        Done(context, creature, text.Length != 0 ? "runs to you" : "walks to you");
        return true;
    }

    private static bool Follow(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        if (!parsed.ExtractOptFloat(out float distance, 2f) || !parsed.IsEmpty || !float.IsFinite(distance) || distance < 0 || distance > 50) return false;
        if (!TrySelected(context, args, true, out Creature creature, out _)) return true;
        creature.Motion.MoveFollow(context.Player, distance, MathF.PI);
        Done(context, creature, string.Create(C, $"follows you at {distance:0.#} yards"));
        return true;
    }

    private static bool Flee(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        if (!parsed.ExtractOptUInt32(out uint seconds, 5) || !parsed.IsEmpty || seconds is 0 or > 60) return false;
        if (!TrySelected(context, args, true, out Creature creature, out _)) return true;
        creature.Motion.MoveFleeing(context.Player, seconds * 1000);
        Done(context, creature, string.Create(C, $"flees for {seconds} s"));
        return true;
    }

    private static bool Cast(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        if (!parsed.ExtractUInt32KeyFromLink("Hspell", out uint spellId) || spellId == 0) return false;
        string? self = parsed.ExtractArg();
        if (!parsed.IsEmpty || (self is not null && !self.Equals("self", StringComparison.OrdinalIgnoreCase))) return false;
        if (!TrySelected(context, args, true, out Creature creature, out CreatureMapSystem system)) return true;
        Unit target = self is null ? context.Player : creature;
        CreatureCastResult result = system.CastSpell(creature, spellId, target, triggered: true);
        Done(context, creature, string.Create(C, $"casts {spellId} on {InspectCommands.Name(target)}: {result}"));
        return true;
    }

    private static bool Event(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        if (!parsed.ExtractUInt32(out uint eventType) || !parsed.ExtractOptUInt32(out uint misc, 0) || !parsed.IsEmpty) return false;
        if (!TrySelected(context, args, true, out Creature creature, out CreatureMapSystem system)) return true;
        bool handled = system.SendAiEventToSelf(creature, eventType, context.Player, misc);
        Done(context, creature, string.Create(C, $"got AI event {eventType} (misc {misc}){(handled ? string.Empty : "; its AI did not take it")}"));
        return true;
    }

    private static bool CallHelp(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        if (!parsed.ExtractOptFloat(out float radius, 10f) || !parsed.IsEmpty || !float.IsFinite(radius) || radius <= 0 || radius > 100) return false;
        if (!TrySelected(context, args, true, out Creature creature, out CreatureMapSystem system)) return true;
        int answered = system.CallForHelp(creature, radius);
        Done(context, creature, string.Create(C, $"called for help within {radius:0.#} yards; {answered} answered"));
        return true;
    }

    private static bool Reset(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        if (!TrySelected(context, args, false, out Creature creature, out CreatureMapSystem system)) return true;
        if (creature.IsPet)
        {
            context.Reply("Pets cannot be reset this way.");
            return true;
        }

        if (creature.DeathState == CreatureDeathState.Alive)
        {
            system.KillCreature(creature);
        }

        system.ForceRespawn(creature);
        Done(context, creature, "was respawned with a fresh AI");
        return true;
    }
}

/// <summary><c>.creature clone [#count]</c>: copy the selected creature (its template) where you stand, as temporary creatures.</summary>
public sealed class CreatureCloneCommand : ICommandExtension
{
    public string Path => "creature";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("clone", AccountSecurity.GameMaster,
            "Syntax: .creature clone [#count]\nPlace #count (default 1, at most 25) temporary copies of the selected creature's template around you. They are not saved and do not respawn.",
            Clone, RetailLevel: 3),
    ];

    private static bool Clone(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        if (!parsed.ExtractOptUInt32(out uint count, 1) || !parsed.IsEmpty || count is 0 or > AiCommands.MaxClones) return false;
        if (GmNpcCommands.SystemOf(context) is not { } system || InspectCommands.TargetCreature(context) is not { } source) return true;
        if (source.IsPet)
        {
            context.Reply("Pets cannot be cloned.");
            return true;
        }

        Player me = context.Player;
        for (int i = 0; i < count; i++)
        {
            float angle = count == 1 ? 0 : MathF.Tau * i / count;
            float radius = count == 1 ? 0 : 2f + (count / 8f);
            system.SpawnTemporary(source.Template, me.X + (MathF.Cos(angle) * radius), me.Y + (MathF.Sin(angle) * radius), me.Z, me.Orientation);
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture, $"Placed {count} temporary {source.Template.Name} (entry {source.Template.Entry})."));
        return true;
    }
}
