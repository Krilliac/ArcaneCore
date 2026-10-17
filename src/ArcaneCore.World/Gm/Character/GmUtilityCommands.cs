using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Pets;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Character;

/// <summary>
/// Small vmangos utility commands: .stable and .bank (MiscCommands.cpp:88-100), .dismount (CharacterCommands.cpp:1217), .demorph
/// (UnitCommands.cpp:2078), .combatstop (CharacterCommands.cpp:5680), .freeze and .unfreeze (UnitCommands.cpp:946-962). Levels from Chat.cpp
/// (1242, 1249, 1280, 1298, 1300, 1304, 1305).
/// </summary>
public sealed class GmUtilityCommands : ICommandGroup
{
    /// <summary>The GM freeze spell vmangos casts and removes (9454, "Freeze").</summary>
    public const uint FreezeSpell = 9454;

    /// <summary>mangos_string 18 (LANG_YOU_IN_FLIGHT).</summary>
    public const string YouInFlight = "Cannot do that while flying.";

    /// <summary>mangos_string 22 (LANG_CHAR_NON_MOUNTED).</summary>
    public const string NotMounted = "You are not mounted so you can't dismount.";

    public const string StableNotHunter = "Only hunters have a stable.";
    public const string StableUnavailable = "The pet stable is not available on this server.";

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("stable", AccountSecurity.GameMaster, "Syntax: .stable\nOpen your pet stable.", Stable, RetailLevel: 2),
        new ChatCommand("bank", AccountSecurity.GameMaster, "Syntax: .bank\nOpen your bank.", Bank, RetailLevel: 2),
        new ChatCommand("dismount", AccountSecurity.Player, "Syntax: .dismount\nDismount you, if you are mounted.", Dismount, RetailLevel: 0),
        new ChatCommand("demorph", AccountSecurity.GameMaster, "Syntax: .demorph\nRestore the native model of the selected unit or yourself.", Demorph, RetailLevel: 3),
        new ChatCommand("combatstop", AccountSecurity.GameMaster, "Syntax: .combatstop [$playername]\nStop combat for the selected or named player.", CombatStop, RetailLevel: 3),
        new ChatCommand("freeze", AccountSecurity.GameMaster, "Syntax: .freeze\nFreeze the selected unit (spell 9454).", Freeze, RetailLevel: 2),
        new ChatCommand("unfreeze", AccountSecurity.GameMaster, "Syntax: .unfreeze\nUnfreeze the selected unit.", Unfreeze, RetailLevel: 2),
    ];

    private static bool Stable(CommandContext context, string text)
    {
        Player player = context.Player;
        if (context.Session.Services.GetService<PetStableFeature>()?.Controller is not { } stable)
        {
            context.Reply(StableUnavailable);
            return true;
        }

        if (player.Class != Class.Hunter)
        {
            context.Reply(StableNotHunter);
            return true;
        }

        stable.OpenAsGm(player);
        return true;
    }

    private static bool Bank(CommandContext context, string text)
    {
        Player player = context.Player;

        // vmangos Player::CanUseBank: the own guid as the banker is always in range.
        player.Inventory.CanUseBank = static () => true;
        player.Session.Send(WorldOpcode.SmsgShowBank, NpcPackets.Guid(player.Guid).ToArray());
        return true;
    }

    private static bool Dismount(CommandContext context, string text)
    {
        Player player = context.Player;
        if (!MountService.IsMounted(player))
        {
            context.Reply(NotMounted);
            return false;
        }

        if ((player.UnitFlags & UnitFlags.TaxiFlight) != 0)
        {
            context.Reply(YouInFlight);
            return false;
        }

        SpellSystem spells = context.Session.Services.GetRequiredService<SpellFeature>().System;
        MountService.Unmount(spells, player);
        spells.RemoveAurasByType(player, AuraType.Mounted);
        return true;
    }

    private static bool Demorph(CommandContext context, string text)
    {
        Unit target = GmSelectedUnit.Of(context) ?? context.Player;
        if (target is Player player && !context.CanActOn(player))
        {
            return false;
        }

        target.DisplayId = target.NativeDisplayId; // Unit::DeMorph
        return true;
    }

    private static bool CombatStop(CommandContext context, string text)
    {
        if (!GmTargets.TryPlayer(context, new CommandArgs(text), out Player target))
        {
            return true;
        }

        if (!context.CanActOn(target))
        {
            return false;
        }

        target.Map?.Combat.CombatStop(target);
        HostileRefs.DeleteReferences(target);
        return true;
    }

    private static bool Freeze(CommandContext context, string text)
    {
        if (GmSelectedUnit.Of(context) is not { } target)
        {
            return false;
        }

        if (target is Player player && !context.CanActOn(player))
        {
            return true;
        }

        SpellSystem spells = context.Session.Services.GetRequiredService<SpellFeature>().System;
        if (spells.Store.Get(FreezeSpell) is null)
        {
            context.Reply($"Spell {FreezeSpell} (Freeze) is not loaded.");
            return true;
        }

        spells.CastSpell(target, FreezeSpell, SpellCastTargets.ForSelf(), triggered: true);
        return true;
    }

    private static bool Unfreeze(CommandContext context, string text)
    {
        if (GmSelectedUnit.Of(context) is not { } target)
        {
            return false;
        }

        if (target is Player player && !context.CanActOn(player))
        {
            return true;
        }

        context.Session.Services.GetRequiredService<SpellFeature>().System.RemoveAuras(target, FreezeSpell);
        return true;
    }
}