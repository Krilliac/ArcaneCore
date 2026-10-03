using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Creatures;

/// <summary>
/// GM creature commands, modelled on cmangos/vmangos <c>.npc add</c>, <c>.npc info</c> and
/// <c>.respawn</c>. The root is <c>.creature</c> rather than <c>.npc</c> so the NPC-services area
/// can own <c>.npc</c> without a duplicate-root clash (docs/integration/creatures.md).
/// </summary>
public sealed class CreatureCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("creature", AccountSecurity.GameMaster, "Creature commands.", Children:
        [
            new ChatCommand("add", AccountSecurity.GameMaster, "Syntax: .creature add <entry> — spawn a temporary creature where you stand (not saved).", Add),
            new ChatCommand("info", AccountSecurity.GameMaster, "Syntax: .creature info — details of the selected creature.", Info),
            new ChatCommand("kill", AccountSecurity.GameMaster, "Syntax: .creature kill — kill the selected creature (corpse, then respawn timer).", Kill),
            new ChatCommand("respawn", AccountSecurity.GameMaster, "Syntax: .creature respawn — respawn the selected dead creature now.", Respawn),
            new ChatCommand("delete", AccountSecurity.GameMaster, "Syntax: .creature delete — remove the selected temporary creature.", Delete),
        ]),
    ];

    private static bool Add(CommandContext context, string args)
    {
        if (!uint.TryParse(args.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint entry))
        {
            return false;
        }

        CreatureWorldFeature feature = context.Session.Services.GetRequiredService<CreatureWorldFeature>();
        if (feature.Content.FindTemplate(entry) is not { } template)
        {
            context.Reply($"Creature template {entry} does not exist.");
            return true;
        }

        var player = context.Player;
        Creature creature = (player.Map is { } map ? feature.GetOrCreateSystem(map) : feature.GetOrCreateSystem(player.MapId))
            .SpawnTemporary(template, player.X, player.Y, player.Z, player.Orientation);
        context.Reply($"Spawned {template.Name} ({creature.Guid}).");
        return true;
    }

    private static bool Info(CommandContext context, string args)
    {
        if (Selected(context) is not { } creature)
        {
            return true;
        }

        CreatureTemplate t = creature.Template;
        context.Reply(
            $"{t.Name} entry {t.Entry} guid {creature.Guid} spawn {creature.Spawn?.Guid.ToString(CultureInfo.InvariantCulture) ?? "temporary"}\n" +
            $"level {creature.Level} health {creature.Health}/{creature.MaxHealth} display {creature.DisplayId} faction {creature.FactionTemplate}\n" +
            $"movement {creature.MovementType} state {creature.DeathState} home {creature.Home.X:F2} {creature.Home.Y:F2} {creature.Home.Z:F2}");
        return true;
    }

    private static bool Kill(CommandContext context, string args)
    {
        if (Selected(context) is { } creature)
        {
            System(context).KillCreature(creature);
        }

        return true;
    }

    private static bool Respawn(CommandContext context, string args)
    {
        if (Selected(context) is { } creature)
        {
            System(context).ForceRespawn(creature);
        }

        return true;
    }

    private static bool Delete(CommandContext context, string args)
    {
        if (Selected(context) is not { } creature)
        {
            return true;
        }

        if (creature.Spawn is not null)
        {
            context.Reply("Database spawns cannot be deleted in game yet.");
            return true;
        }

        System(context).Despawn(creature);
        return true;
    }

    private static CreatureMapSystem System(CommandContext context)
        => context.Player.Map is { } map
            ? context.Session.Services.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(map)
            : context.Session.Services.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(context.Player.MapId);

    private static Creature? Selected(CommandContext context)
    {
        Creature? creature = System(context).FindCreature(context.Player.Selection);
        if (creature is null)
        {
            context.Reply("Select a creature first.");
        }

        return creature;
    }
}
