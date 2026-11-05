using System.Globalization;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
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
        new ChatCommand("learn", AccountSecurity.Administrator, "Syntax: .learn #spell — teach a spell to the selected player or yourself.", Learn, RetailLevel: 5),
        new ChatCommand("unlearn", AccountSecurity.GameMaster, "Syntax: .unlearn #spell — make the selected player or yourself forget a spell.", Unlearn),
        new ChatCommand("cast", AccountSecurity.Administrator, "Syntax: .cast #spell — cast a spell (triggered) on the selected player or yourself. Variants: .cast self, .cast target, .cast back, .cast dist.", Cast, Children:
        [
            // vmangos castCommandTable (Chat.cpp:195-203): back, dist, self and target, all SEC_DEVELOPER (here Administrator like .cast).
            new ChatCommand("self", AccountSecurity.Administrator, "Syntax: .cast self #spell [triggered]\nMake the selected unit cast a spell on itself.", CastSelf),
            new ChatCommand("target", AccountSecurity.Administrator, "Syntax: .cast target #spell [triggered]\nMake the selected unit cast a spell on its victim.", CastTarget),
            new ChatCommand("back", AccountSecurity.Administrator, "Syntax: .cast back #spell [triggered]\nMake the selected unit cast a spell on you.", CastBack),
            new ChatCommand("dist", AccountSecurity.Administrator, "Syntax: .cast dist #spell #distance [triggered]\nCast a spell at a point the given distance in front of you.", CastDist),
        ]),
        new ChatCommand("unaura", AccountSecurity.GameMaster, "Syntax: .unaura #spell|all — remove auras from the selected player or yourself.", Unaura),
        new ChatCommand("cooldown", AccountSecurity.Administrator, "Syntax: .cooldown [#spell] — clear one or every spell cooldown of the selected player or yourself.", Cooldown, Children:
        [
            // vmangos cooldownCommandTable (Chat.cpp:281-286): list, clear and clearclientside, SEC_GAMEMASTER.
            new ChatCommand("list", AccountSecurity.GameMaster, "Syntax: .cooldown list\nList the running cooldowns of the selected player or yourself.", CooldownList, RetailLevel: 3),
            new ChatCommand("clear", AccountSecurity.GameMaster, "Syntax: .cooldown clear [#spell]\nClear one or every spell cooldown of the selected player or yourself.", Cooldown, RetailLevel: 3),
            new ChatCommand("clearclientside", AccountSecurity.GameMaster, "Syntax: .cooldown clearclientside\nClear every spell cooldown of the selected player or yourself and tell its client.", CooldownClearAll, RetailLevel: 3),
        ]),
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
        if (!target.CanMutateQuestSettlementState)
        {
            context.Reply($"{target.Name} is settling a quest reward; try again in a moment.");
            return true;
        }

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

        SpellCastTargets targets = context.IsInvoker(target)
            ? SpellCastTargets.ForSelf()
            : SpellCastTargets.ForUnit(target.Guid);
        SpellCastResult result = Feature(context).System.CastSpell(context.Player, spellId, targets, triggered: true);
        context.Reply(result == SpellCastResult.CastOk ? $"Cast spell {spellId}." : $"Spell {spellId} failed: {result}.");
        return true;
    }

    /// <summary>
    /// vmangos ExtractUint32 (spell id, a plain number with an optional leading #; the shift-click link form is not parsed), ExtractFloat and ExtractLiteralArg
    /// "triggered" (UnitCommands.cpp:1212-1385): a spell id, an optional distance, then an optional <c>triggered</c> word. As in vmangos, anything after the
    /// <c>triggered</c> word is ignored; without that word nothing may follow. False on any syntax error.
    /// </summary>
    private static bool TryParseCastArgs(string text, bool withDistance, out uint spellId, out float distance, out bool triggered)
    {
        distance = 0f;
        triggered = false;
        var args = new CommandArgs(text.Trim().TrimStart('#'));
        if (!args.ExtractUInt32(out spellId) || spellId == 0)
        {
            return false;
        }

        if (withDistance && (!args.ExtractFloat(out distance) || !float.IsFinite(distance)))
        {
            return false;
        }

        triggered = args.ExtractLiteral("triggered") is not null;
        return triggered || args.IsEmpty;
    }

    /// <summary>The selected unit, or null after replying (vmangos GetSelectedUnit, LANG_SELECT_CHAR_OR_CREATURE; a selected player must be actable on).</summary>
    private static Unit? SelectedUnit(CommandContext context)
    {
        Player player = context.Player;
        Unit? unit = player.Selection.IsEmpty ? null : player.Map?.Combat.FindUnit(player.Selection);
        if (unit is null)
        {
            context.Reply("Select a character or creature.");
            return null;
        }

        return unit is Player selectedPlayer && !context.CanActOn(selectedPlayer) ? null : unit;
    }

    private static void ReplyCast(CommandContext context, uint spellId, SpellCastResult result)
        => context.Reply(result == SpellCastResult.CastOk ? $"Cast spell {spellId}." : $"Spell {spellId} failed: {result}.");

    private static bool SpellExists(CommandContext context, uint spellId)
    {
        if (Feature(context).System.Store.Get(spellId) is not null)
        {
            return true;
        }

        context.Reply($"Spell {spellId} does not exist.");
        return false;
    }

    private static bool CastSelf(CommandContext context, string args)
    {
        if (!TryParseCastArgs(args, withDistance: false, out uint spellId, out _, out bool triggered))
        {
            return false;
        }

        if (SelectedUnit(context) is not { } caster)
        {
            return true;
        }

        if (SpellExists(context, spellId))
        {
            ReplyCast(context, spellId, Feature(context).System.CastSpell(caster, spellId, SpellCastTargets.ForSelf(), triggered));
        }

        return true;
    }

    private static bool CastTarget(CommandContext context, string args)
    {
        if (!TryParseCastArgs(args, withDistance: false, out uint spellId, out _, out bool triggered))
        {
            return false;
        }

        if (SelectedUnit(context) is not { } caster)
        {
            return true;
        }

        if (caster.Combat.Victim is not { } victim)
        {
            context.Reply("The selected unit has no victim.");
            return true;
        }

        // vmangos has no check here; this repo is stricter (GM.LowerSecurity): a spell may not be forced onto a player the invoker cannot act on.
        if (victim is Player victimPlayer && !context.CanActOn(victimPlayer))
        {
            return true;
        }

        if (SpellExists(context, spellId))
        {
            ReplyCast(context, spellId, Feature(context).System.CastSpell(caster, spellId, SpellCastTargets.ForUnit(victim.Guid), triggered));
        }

        return true;
    }

    private static bool CastBack(CommandContext context, string args)
    {
        if (!TryParseCastArgs(args, withDistance: false, out uint spellId, out _, out bool triggered))
        {
            return false;
        }

        if (SelectedUnit(context) is not { } caster)
        {
            return true;
        }

        if (SpellExists(context, spellId))
        {
            ReplyCast(context, spellId, Feature(context).System.CastSpell(caster, spellId, SpellCastTargets.ForUnit(context.Player.Guid), triggered));
        }

        return true;
    }

    private static bool CastDist(CommandContext context, string args)
    {
        if (!TryParseCastArgs(args, withDistance: true, out uint spellId, out float distance, out bool triggered))
        {
            return false;
        }

        if (!SpellExists(context, spellId))
        {
            return true;
        }

        // vmangos GetClosePoint(x, y, z, dist): the point `dist` yards in front of the invoker (its own size is not added here).
        Player player = context.Player;
        var targets = new SpellCastTargets { Mask = SpellCastTargetFlags.DestLocation, Dest = PointInFront(player.X, player.Y, player.Z, player.Orientation, distance) };
        ReplyCast(context, spellId, Feature(context).System.CastSpell(player, spellId, targets, triggered));
        return true;
    }

    /// <summary>The point <paramref name="distance"/> yards ahead of (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) facing <paramref name="orientation"/> (radians); the height is kept.</summary>
    internal static (float X, float Y, float Z) PointInFront(float x, float y, float z, float orientation, float distance)
        => (x + (MathF.Cos(orientation) * distance), y + (MathF.Sin(orientation) * distance), z);

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

    // vmangos SpellCaster::PrintCooldownList (SpellCaster.cpp:2567): one line per running spell and category cooldown.
    private static bool CooldownList(CommandContext context, string args)
    {
        if (args.Trim().Length > 0)
        {
            return false;
        }

        Player? target = Target(context);
        if (target is null)
        {
            return true;
        }

        SpellSystem system = Feature(context).System;
        IReadOnlyList<InitialSpellCooldown> cooldowns = system.GetActiveCooldowns(target);
        foreach (InitialSpellCooldown cooldown in cooldowns.OrderBy(c => c.SpellId))
        {
            string name = system.Store.Get(cooldown.SpellId)?.Name ?? "?";
            string line = string.Create(CultureInfo.InvariantCulture, $"Spell {cooldown.SpellId} ({name}) has {Duration(cooldown.CooldownMs)} cd");
            if (cooldown.Category != 0 && cooldown.CategoryCooldownMs > 0)
            {
                line += string.Create(CultureInfo.InvariantCulture, $", category {cooldown.Category} has {Duration(cooldown.CategoryCooldownMs)} cd");
            }

            context.Reply(line);
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture, $"{target.Name} has {cooldowns.Count} cooldowns."));
        return true;
    }

    private static bool CooldownClearAll(CommandContext context, string args)
        => args.Trim().Length == 0 && Cooldown(context, string.Empty);

    // vmangos ConvertMillisecondToStr: hours, minutes, seconds and milliseconds, leading zero parts left out.
    internal static string Duration(uint ms)
    {
        var time = TimeSpan.FromMilliseconds(ms);
        var parts = new List<string>();
        if (time.Hours + (time.Days * 24) > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{time.Hours + (time.Days * 24)}h"));
        if (time.Minutes > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}m"));
        if (time.Seconds > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{time.Seconds}s"));
        if (time.Milliseconds > 0 || parts.Count == 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{time.Milliseconds}ms"));
        return string.Join(' ', parts);
    }
}
