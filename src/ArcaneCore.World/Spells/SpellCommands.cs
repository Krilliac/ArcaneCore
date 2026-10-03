using System.Globalization;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Spells;

/// <summary>
/// GM spell commands (vmangos Level2/Level3 .learn, .unlearn, .cast, .unaura, .cooldown). Each
/// acts on the selected player, or on the invoker when nothing is selected. The security levels
/// follow vmangos command table: learn/unlearn/unaura are GameMaster; cast/cooldown are Administrator.
/// </summary>
public sealed class SpellCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("learn", AccountSecurity.GameMaster, "Syntax: .learn #spell — teach a spell to the selected player or yourself.", Learn),
        new ChatCommand("unlearn", AccountSecurity.GameMaster, "Syntax: .unlearn #spell — make the selected player or yourself forget a spell.", Unlearn),
        new ChatCommand("cast", AccountSecurity.Administrator, "Syntax: .cast #spell — cast a spell (triggered) on the selected player or yourself.", Cast),
        new ChatCommand("unaura", AccountSecurity.GameMaster, "Syntax: .unaura #spell|all — remove auras from the selected player or yourself.", Unaura),
        new ChatCommand("cooldown", AccountSecurity.Administrator, "Syntax: .cooldown [#spell] — clear one or every spell cooldown of the selected player or yourself.", Cooldown),
    ];

    private static SpellFeature Feature(CommandContext context) => context.Session.Services.GetRequiredService<SpellFeature>();

    private static bool TryParseSpell(string args, out uint spellId)
        => uint.TryParse(args.Trim().TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out spellId);

    private static Player? Target(CommandContext context)
    {
        Player? target = context.SelectedPlayerOrSelf();
        if (target is null)
        {
            context.Reply("No player selected.");
            return null;
        }

        return context.CanActOn(target) ? target : null;
    }

    private static bool Learn(CommandContext context, string args)
    {
        if (!TryParseSpell(args, out uint spellId))
        {
            return false;
        }

        Player? target = Target(context);
        if (target is null)
        {
            return true;
        }

        SpellFeature feature = Feature(context);
        if (feature.System.Store.Get(spellId) is null)
        {
            context.Reply($"Spell {spellId} does not exist.");
            return true;
        }

        context.Reply(feature.System.LearnSpell(target, spellId)
            ? $"{target.Name} learned spell {spellId}."
            : $"{target.Name} already knows spell {spellId}.");
        return true;
    }

    private static bool Unlearn(CommandContext context, string args)
    {
        if (!TryParseSpell(args, out uint spellId))
        {
            return false;
        }

        Player? target = Target(context);
        if (target is null)
        {
            return true;
        }

        SpellFeature feature = Feature(context);
        if (!feature.Spellbook.ForgetSpell(target, spellId))
        {
            context.Reply($"{target.Name} does not know spell {spellId}.");
            return true;
        }

        // vmangos Player::removeSpell: SMSG_REMOVED_SPELL, and passive auras go with the spell.
        target.Session.Send(WorldOpcode.SmsgRemovedSpell, SpellPackets.BuildRemovedSpell(spellId));
        feature.System.RemoveAuras(target, spellId);
        context.Reply($"{target.Name} forgot spell {spellId}.");
        return true;
    }

    private static bool Cast(CommandContext context, string args)
    {
        if (!TryParseSpell(args, out uint spellId))
        {
            return false;
        }

        Player? target = Target(context);
        if (target is null)
        {
            return true;
        }

        SpellCastTargets targets = ReferenceEquals(target, context.Player)
            ? SpellCastTargets.ForSelf()
            : SpellCastTargets.ForUnit(target.Guid);
        SpellCastResult result = Feature(context).System.CastSpell(context.Player, spellId, targets, triggered: true);
        context.Reply(result == SpellCastResult.CastOk ? $"Cast spell {spellId}." : $"Spell {spellId} failed: {result}.");
        return true;
    }

    private static bool Unaura(CommandContext context, string args)
    {
        Player? target;
        SpellSystem system;
        if (args.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            target = Target(context);
            if (target is null)
            {
                return true;
            }

            system = Feature(context).System;
            foreach (uint spellId in system.GetAuras(target).Select(h => h.Spell.Id).Distinct().ToArray())
            {
                system.RemoveAuras(target, spellId);
            }

            context.Reply($"Removed every aura of {target.Name}.");
            return true;
        }

        if (!TryParseSpell(args, out uint id))
        {
            return false;
        }

        target = Target(context);
        if (target is null)
        {
            return true;
        }

        system = Feature(context).System;
        bool had = system.HasAura(target, id);
        system.RemoveAuras(target, id);
        context.Reply(had ? $"Removed aura {id} from {target.Name}." : $"{target.Name} has no aura {id}.");
        return true;
    }

    private static bool Cooldown(CommandContext context, string args)
    {
        uint spellId = 0;
        if (args.Trim().Length > 0 && !TryParseSpell(args, out spellId))
        {
            return false;
        }

        Player? target = Target(context);
        if (target is null)
        {
            return true;
        }

        SpellSystem system = Feature(context).System;
        uint[] spells = spellId != 0 ? [spellId] : [.. system.GetActiveCooldowns(target).Select(c => c.SpellId)];
        foreach (uint id in spells)
        {
            system.ClearCooldown(target, id);
        }

        context.Reply(spellId != 0 ? $"Cleared the cooldown of spell {spellId} for {target.Name}." : $"Cleared {spells.Length} cooldowns for {target.Name}.");
        return true;
    }
}
