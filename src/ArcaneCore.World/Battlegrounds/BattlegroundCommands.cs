using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Battlegrounds;

/// <summary>vmangos .bg status/start/stop (MiscCommands.cpp:1715-1840, Chat.cpp:1066-1068).</summary>
public sealed class BattlegroundCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("bg", AccountSecurity.GameMaster, "Syntax: .bg status|start|stop", Children:
        [
            new ChatCommand("status", AccountSecurity.GameMaster, "Show running battlegrounds and queue counts for your bracket.", Status, RetailLevel: 3),
            new ChatCommand("start", AccountSecurity.GameMaster, "Start the battleground you are in now.", Start, RetailLevel: 3),
            new ChatCommand("stop", AccountSecurity.GameMaster, "Stop the battleground you are in after a short countdown.", Stop, RetailLevel: 3),
        ], RetailLevel: 3),
    ];

    private static BattlegroundFeature Feature(CommandContext context) => context.Session.Services.GetRequiredService<BattlegroundFeature>();

    private static bool Status(CommandContext context, string text)
    {
        if (text.Trim().Length != 0)
        {
            return false;
        }

        BattlegroundManager manager = Feature(context).Manager;
        Battleground[] matches = [.. manager.RunningBattlegrounds.OrderBy(bg => bg.Type).ThenBy(bg => bg.InstanceId)];
        context.Reply($"Currently running battlegrounds: {matches.Length}");
        foreach (Battleground bg in matches)
        {
            context.Reply($"{bg.Name} [{bg.InstanceId}] level {bg.MinLevel}-{bg.MaxLevel} {bg.Status}: "
                + $"Alliance {bg.PlayersCountByTeam(ArcaneCore.Game.Entities.Team.Alliance)}, "
                + $"Horde {bg.PlayersCountByTeam(ArcaneCore.Game.Entities.Team.Horde)}");
        }

        foreach (BattlegroundType type in new[] { BattlegroundType.AlteracValley, BattlegroundType.WarsongGulch, BattlegroundType.ArathiBasin })
        {
            if (manager.TemplateOf(type) is { } template)
            {
                (int alliance, int horde) = manager.QueuedTeamCounts(type, context.Player.Level);
                context.Reply($"{template.Name} queue: Alliance {alliance}, Horde {horde}");
            }
        }

        return true;
    }

    private static bool Start(CommandContext context, string text) => Act(context, text, stop: false);

    private static bool Stop(CommandContext context, string text) => Act(context, text, stop: true);

    private static bool Act(CommandContext context, string text, bool stop)
    {
        if (text.Trim().Length != 0)
        {
            return false;
        }

        Battleground? bg = Feature(context).BattlegroundOf(context.Player.Guid);
        if (bg is null)
        {
            context.Reply("You are not in a battleground.");
            return true;
        }

        if (stop)
        {
            bg.ForceStop();
        }
        else
        {
            bg.ForceStart();
        }

        context.Reply($"Battleground {(stop ? "stopping" : "starting")} [{bg.Name}][{bg.InstanceId}].");
        return true;
    }
}
