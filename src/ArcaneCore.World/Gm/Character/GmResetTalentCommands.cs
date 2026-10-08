using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Talents;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Character;

/// <summary>vmangos CharacterCommands.cpp:3892-3918, Chat.cpp:913-923; online targets only.</summary>
public sealed class GmResetTalentCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("reset", AccountSecurity.GameMaster, "Syntax: .reset $subcommand", Children:
        [
            new ChatCommand("talents", AccountSecurity.GameMaster,
                "Syntax: .reset talents [$playername]\nReturn the selected or named online player's spent talent points without a respec charge.",
                ResetTalents, RetailLevel: 3),
        ], RetailLevel: 3),
    ];

    private static bool ResetTalents(CommandContext context, string text)
    {
        if (!GmTargets.TryPlayer(context, new CommandArgs(text), out Player? target) || !context.CanActOn(target))
        {
            return true;
        }

        if (context.Session.Services.GetRequiredService<TalentFeature>().Service is not { } service)
        {
            context.Reply("Talent data is unavailable; configure the client Talent.dbc and TalentTab.dbc paths.");
            return true;
        }

        if (!target.CanMutateQuestSettlementState)
        {
            context.Reply($"{target.Name} is settling a quest reward; try again in a moment.");
            return true;
        }

        // LANG_RESET_TALENTS (216) / LANG_RESET_TALENTS_ONLINE (213), mangos-classic mangos.sql:3582,3585.
        service.ResetTalents(target, noCost: true);
        target.SendSystemMessage("Your talents have been reset.");
        if (!ReferenceEquals(target, context.Player))
        {
            context.Reply($"Talents of {GmStrings.PlayerLink(target.Name)} reset.");
        }

        return true;
    }
}
