using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Character;

/// <summary>vmangos HandleResetSpellsCommand (CharacterCommands.cpp:3873-3890) and Player::ResetSpells (Player.cpp:19263-19276).</summary>
public sealed class ResetSpellExtension : ICommandExtension
{
    public string Path => "reset";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("spells", AccountSecurity.Administrator, "Syntax: .reset spells — replace the selected player's spellbook with race/class defaults and rewarded quest spells.", ResetSpells, RetailLevel: 5),
        new ChatCommand("stats", AccountSecurity.Administrator, "Syntax: .reset stats [$playername] — reapply the current level's base stats.", ResetStats, RetailLevel: 5),
    ];

    private static bool ResetSpells(CommandContext context, string text)
    {
        if (text.Trim().Length != 0)
        {
            return false;
        }

        Player? target = context.SelectedPlayerOrSelf();
        if (target is null)
        {
            context.Reply(GmStrings.NoCharSelected);
            return true;
        }

        if (!context.CanActOn(target))
        {
            return true;
        }

        if (!target.CanMutateQuestSettlementState)
        {
            context.Reply($"{target.Name} is settling a quest reward; try again in a moment.");
            return true;
        }

        SpellFeature feature = context.Session.Services.GetRequiredService<SpellFeature>();
        SpellSystem system = feature.System;
        foreach (uint spell in feature.Spellbook.GetSpells(target).ToArray())
        {
            system.RemoveSpell(target, spell);
        }

        SpellVariants.LearnDefaults(context, target);

        target.SendSystemMessage("Your spells have been reset.");
        if (!ReferenceEquals(target, context.Player))
        {
            context.Reply($"Spells of {GmStrings.PlayerLink(target.Name)} reset.");
        }

        return true;
    }

    // vmangos HandleResetStatsCommand (CharacterCommands.cpp:3858-3870) calls InitStatsForLevel(true) and InitTalentForLevel.
    private static bool ResetStats(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!GmTargets.TryPlayer(context, args, out Player target) || !context.CanActOn(target))
        {
            return true;
        }

        if (!args.IsEmpty)
        {
            return false;
        }

        SpellSystem spells = context.Session.Services.GetRequiredService<SpellFeature>().System;
        bool hasPoolAura = spells.GetAuras(target).Any(holder => holder.Spell.Effects.Any(effect => effect.AuraType is
            AuraType.ModIncreaseHealth or AuraType.ModIncreaseHealthPercent or AuraType.ModIncreaseEnergy or AuraType.ModIncreaseEnergyPercent));
        if (!context.Session.Services.GetRequiredService<ProgressionFeature>().Progression.ResetStatsForLevel(target, resetMaximums: !hasPoolAura))
        {
            context.Reply("Level statistics are unavailable for that race and class.");
            return true;
        }

        context.Reply($"Stats of {GmStrings.PlayerLink(target.Name)} reset for level {target.Level}.");
        return true;
    }
}
