using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Character;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.DebugTools;

/// <summary>
/// <c>.nameaura #spell [$player] [#seconds]</c> (vmangos HandleNameAuraCommand, UnitCommands.cpp:977; HandleAuraHelper): put a spell's
/// auras on a player picked by name (the selected player, or the invoker, when no name is given), optionally for a set number of
/// seconds. The aura is applied like <c>.aura</c> (<see cref="SpellSystem.AddAura"/>: no cast, the invoker is the caster). Same
/// narrowing as <c>.aura</c>: only SPELL_EFFECT_APPLY_AURA effects are built.
/// </summary>
public sealed class NameAuraCommand : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("nameaura", AccountSecurity.Administrator, "Syntax: .nameaura #spell [$playername] [#seconds]\nApply a spell's auras to the named online player (the selected player or yourself without a name), for #seconds (the spell's own duration without it).", NameAura, RetailLevel: 4),
    ];

    private static bool NameAura(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32KeyFromLink("Hspell", out uint spellId) || spellId == 0)
        {
            return false;
        }

        // vmangos: the name is the next argument; an absent one falls back to the selected player.
        string? nameArg = args.ExtractArg();
        if (!PlayerTargetResolver.TryExtract(new CommandArgs(nameArg ?? string.Empty), name => context.World.FindOnlinePlayer(name),
                context.SelectedPlayerOrSelf, out Player? target, out _) || target is null)
        {
            // PlayerTargetResolver contract: the caller answers "Player not found!" and the command counts as handled.
            context.Reply(GmStrings.PlayerNotFound);
            return true;
        }

        // ExtractInt32 failing leaves the duration 0 (the spell's own), as the reference ignores its result.
        args.ExtractInt32(out int seconds);

        SpellSystem spells = context.Session.Services.GetRequiredService<SpellFeature>().System;
        if (spells.Store.Get(spellId) is null)
        {
            return false;
        }

        if (!context.CanActOn(target))
        {
            return true;
        }

        if (!spells.AddAura(target, spellId, caster: context.Player))
        {
            context.Reply(GmParityCommands.SpellNoHaveAuras(spellId));
            return true;
        }

        if (seconds > 0 && seconds <= int.MaxValue / 1000)
        {
            // HandleAuraHelper: holder->SetAuraDuration(duration * IN_MILLISECONDS). The holder is already applied here, so
            // GmSetAuraDuration also resends SMSG_UPDATE_AURA_DURATION and the target's client shows the overridden timer.
            SpellAuraHolder? holder = spells.GetAuras(target).LastOrDefault(h => h.Spell.Id == spellId && !h.IsPermanent);
            if (holder is not null)
            {
                spells.GmSetAuraDuration(holder, seconds * 1000);
            }
        }

        return true;
    }
}
