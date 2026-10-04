using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Objects;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Npc;

/// <summary>
/// The <c>.npc</c> commands (docs/integration/gm-objects-npc-lane.md): speech and emotes of the selected creature (<c>say</c>, <c>yell</c>,
/// <c>textemote</c>, <c>whisper</c>, <c>playemote</c>), and <c>add</c>, <c>delete</c>, <c>info</c>, <c>near</c>. As in <c>.gobject</c>, nothing is
/// persisted: <c>add</c> places a temporary creature and <c>delete</c> removes only a temporary one (the reference cores edit the
/// <c>creature</c> table; ArcaneCore has no spawn write path). Speech goes through <see cref="CreatureMapSystem.Say"/>, so it uses the same
/// chat packets and ranges as creature scripts (say and text emote 25 yards, yell 300). The reply texts are ArcaneCore's own wording.
/// </summary>
public sealed class GmNpcCommands : ICommandGroup
{
    /// <summary>Rows <c>.npc near</c> prints before it says how many it left out.</summary>
    public const int MaxNearRows = 20;

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("npc", AccountSecurity.GameMaster, "Syntax: .npc $subcommand\nType .npc to see the list of possible subcommands or .help npc $subcommand to see info on subcommands.", Children:
        [
            new ChatCommand("add", AccountSecurity.GameMaster, "Syntax: .npc add #entry\nPlace a temporary creature where you stand. It is not saved and does not respawn.", CreatureCommands.Add, RetailLevel: 3),
            new ChatCommand("delete", AccountSecurity.GameMaster, "Syntax: .npc delete\nRemove the selected creature if it was placed with .npc add. Database spawns are refused.", CreatureCommands.Delete, RetailLevel: 3),
            new ChatCommand("say", AccountSecurity.GameMaster, "Syntax: .npc say $message\nMake the selected creature say the message (heard within 25 yards).", Say, RetailLevel: 3),
            new ChatCommand("yell", AccountSecurity.GameMaster, "Syntax: .npc yell $message\nMake the selected creature yell the message (heard within 300 yards).", Yell, RetailLevel: 3),
            new ChatCommand("textemote", AccountSecurity.GameMaster, "Syntax: .npc textemote $message\nMake the selected creature perform a text emote (seen within 25 yards).", TextEmote, RetailLevel: 3),
            new ChatCommand("whisper", AccountSecurity.GameMaster, "Syntax: .npc whisper $playername $message\nMake the selected creature whisper the message to an online player on the same map.", Whisper, RetailLevel: 3),
            new ChatCommand("playemote", AccountSecurity.GameMaster, "Syntax: .npc playemote #emote\nMake the selected creature play an emote animation. The id is not checked against the client's emote table.", PlayEmote, RetailLevel: 3),
            new ChatCommand("info", AccountSecurity.GameMaster, "Syntax: .npc info\nShow the details of the selected creature.", Info, RetailLevel: 2),
            new ChatCommand("near", AccountSecurity.GameMaster, "Syntax: .npc near [#radius]\nList the creatures within #radius yards (default 10), nearest first.", Near, RetailLevel: 2),
        ], RetailLevel: 2),
    ];

    /// <summary>The creature system of the invoker's map; null when the invoker is in no map.</summary>
    internal static CreatureMapSystem? SystemOf(CommandContext context)
        => context.Player.Map is { } map ? context.Session.Services.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(map) : null;

    public const string SelectCreature = "Select a creature first.";

    public static string NotAlive(string name) => $"{name} is not alive.";

    public static string NotOnThisMap(string name) => $"{name} is not on this map.";

    public static string Done(string verb, string name, uint entry, uint counter)
        => string.Create(CultureInfo.InvariantCulture, $"{name} (entry {entry}, guid {counter}) {verb}.");

    /// <summary>The selected creature of the invoker's map, or null after replying.</summary>
    private static Creature? Selected(CommandContext context, CreatureMapSystem system, bool mustBeAlive)
    {
        Creature? creature = system.FindCreature(context.Player.Selection);
        if (creature is null)
        {
            context.Reply(SelectCreature);
            return null;
        }

        if (mustBeAlive && creature.DeathState != CreatureDeathState.Alive)
        {
            context.Reply(NotAlive(creature.Template.Name));
            return null;
        }

        return creature;
    }

    private static bool Say(CommandContext context, string text) => Speak(context, text, speechType: 0, "said it");

    private static bool Yell(CommandContext context, string text) => Speak(context, text, speechType: 1, "yelled it");

    private static bool TextEmote(CommandContext context, string text) => Speak(context, text, speechType: 2, "emoted it");

    /// <summary>creature_ai_texts type: 0 say, 1 yell, 2 text emote, 4 whisper (<see cref="CreatureMapSystem.Say"/>).</summary>
    private static bool Speak(CommandContext context, string text, byte speechType, string verb)
    {
        string message = text.Trim();
        if (message.Length == 0)
        {
            return false;
        }

        if (SystemOf(context) is not { } system || Selected(context, system, mustBeAlive: true) is not { } creature)
        {
            return true;
        }

        system.Say(creature, new CreatureAiText(0, message, speechType, 0, 0), target: null);
        context.Reply(Done(verb, creature.Template.Name, creature.Template.Entry, creature.Guid.Counter));
        return true;
    }

    private static bool Whisper(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        string? name = args.ExtractArg();
        string message = args.Rest.Trim();
        if (name is null || message.Length == 0)
        {
            return false;
        }

        if (SystemOf(context) is not { } system || Selected(context, system, mustBeAlive: true) is not { } creature)
        {
            return true;
        }

        if (!GmTargets.TryPlayer(context, name, out Player target))
        {
            return true;
        }

        if (!ReferenceEquals(target.Map, creature.Map))
        {
            context.Reply(NotOnThisMap(target.Name));
            return true;
        }

        system.Say(creature, new CreatureAiText(0, message, 4, 0, 0), target);
        context.Reply(Done("whispered it to " + target.Name, creature.Template.Name, creature.Template.Entry, creature.Guid.Counter));
        return true;
    }

    private static bool PlayEmote(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint emote) || !args.IsEmpty)
        {
            return false;
        }

        if (SystemOf(context) is not { } system || Selected(context, system, mustBeAlive: true) is not { } creature)
        {
            return true;
        }

        creature.Map!.BroadcastToObservers(creature, WorldOpcode.SmsgEmote, CreatureChatPackets.BuildEmote(emote, creature.Guid));
        context.Reply(Done(string.Create(CultureInfo.InvariantCulture, $"played emote {emote}"), creature.Template.Name, creature.Template.Entry, creature.Guid.Counter));
        return true;
    }

    private static bool Info(CommandContext context, string text)
    {
        if (text.Trim().Length != 0)
        {
            return false;
        }

        if (SystemOf(context) is not { } system || Selected(context, system, mustBeAlive: false) is not { } creature)
        {
            return true;
        }

        context.Reply(Describe(system, creature));
        return true;
    }

    private static bool Near(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractOptFloat(out float radius, 10f) || !args.IsEmpty || !(radius > 0f) || radius > GmObjectCommands.MaxNearRadius)
        {
            return false;
        }

        if (SystemOf(context) is not { } system)
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
        foreach ((Creature creature, float distance) in near.Take(MaxNearRows))
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture,
                $"{creature.Guid.Counter} entry {creature.Template.Entry} {creature.Template.Name} level {creature.Level} {Describe(creature.DeathState)} at {creature.X:F2} {creature.Y:F2} {creature.Z:F2}, {distance:F1} yards"));
        }

        if (near.Count > MaxNearRows)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"... {near.Count - MaxNearRows} more not shown."));
        }

        return true;
    }

    /// <summary>The life state word used in the listings.</summary>
    public static string Describe(CreatureDeathState state) => state switch
    {
        CreatureDeathState.Alive => "alive",
        CreatureDeathState.Corpse => "corpse",
        _ => "dead",
    };

    /// <summary>The <c>.npc info</c> lines of a creature.</summary>
    public static string Describe(CreatureMapSystem system, Creature creature)
    {
        CreatureTemplate t = creature.Template;
        string origin = creature.Spawn is { } spawn ? string.Create(CultureInfo.InvariantCulture, $"database spawn {spawn.Guid}") : "temporary (not saved)";
        string state = GmSpawnCommands.State(system, creature);
        return string.Create(CultureInfo.InvariantCulture,
            $"{t.Name} entry {t.Entry} guid {creature.Guid.Counter} level {creature.Level} health {creature.Health}/{creature.MaxHealth}\n" +
            $"{origin}; {state}\n" +
            $"display {creature.DisplayId} faction {creature.FactionTemplate} npc flags {creature.NpcFlags} movement {creature.MovementType}\n" +
            $"position {creature.X:F2} {creature.Y:F2} {creature.Z:F2} home {creature.Home.X:F2} {creature.Home.Y:F2} {creature.Home.Z:F2}");
    }
}
