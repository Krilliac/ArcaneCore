using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;

namespace ArcaneCore.World.Gm.Npc;

/// <summary>Selected-creature controls from vmangos CreatureCommands.cpp:557-578, Chat.cpp:672-680.</summary>
public sealed class GmNpcSetCommands : ICommandExtension
{
    public string Path => "npc";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("set", AccountSecurity.GameMaster, "Syntax: .npc set $subcommand", Children:
        [
            new ChatCommand("flag", AccountSecurity.GameMaster, "Syntax: .npc set flag #flags\nChange the selected creature's NPC service flags for its current life.", SetFlag, RetailLevel: 3),
        ], RetailLevel: 3),
    ];

    private static bool SetFlag(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint flags) || !args.IsEmpty)
        {
            return false;
        }

        Creature? creature = GmNpcCommands.SystemOf(context)?.FindCreature(context.Player.Selection);
        if (creature is null)
        {
            context.Reply(GmNpcCommands.SelectCreature);
            return true;
        }

        creature.NpcFlags = flags;
        context.Reply($"Npc flags updated to {flags}.");
        return true;
    }
}
