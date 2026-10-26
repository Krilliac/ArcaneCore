using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Features;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Character;

/// <summary>
/// vmangos <c>.cheat</c> (CharacterCommands.cpp, HandleCheat*Command; every entry SEC_GAMEMASTER in Chat.cpp): god, cooldown, casttime,
/// powercost, waterwalk, immunepc, immunenpc, untargetable, status, and the movement cheats fly, fixedz and wallclimb (plus
/// <c>.featherfall</c>, which vmangos does not have), <c>.hover</c> (SEC_TICKETMASTER, HandleHoverCommand) and <c>.knockback</c>
/// (SEC_GAMEMASTER, HandleKnockBackCommand, UnitCommands.cpp:2761). The cooldown/cast time/power options live in
/// <see cref="Player.Cheats"/> (the spell side is <c>GmCheatSpellModule</c>); god is <c>InvincibilityHpThreshold</c> 1
/// (Player::SetCheatGod). Fly and fixed Z port vmangos <c>Player::SetCheatFly</c> / <c>SetCheatFixedZ</c> (see
/// <see cref="MovementCheats"/>), and the movement anticheat and the fall, void and speed observers stand down while either is on.
/// Wallclimb is UNIT_FLAG_SERVER_CONTROLLED on the target (HandleCheatWallclimbCommand, CharacterCommands.cpp:509-541). Feather fall has no
/// vmangos command: it sends SMSG_MOVE_FEATHER_FALL / SMSG_MOVE_NORMAL_FALL (mangoszero <c>Player::SetFeatherFall</c>,
/// PlayerMovement.cpp:157) through the existing movement-change path, as <c>.cheat waterwalk</c> does for water walking. The dev cheat
/// opcodes (toggle gravity, toggle collision, start swim) are never sent: there is no evidence a 1.12.1 client handles them from the
/// server, and <c>StrippedDevOpcodes</c> keeps them out. None of it is saved (vmangos does not save the cheat options either).
/// </summary>
public sealed class GmCheatCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("cheat", AccountSecurity.GameMaster, "Syntax: .cheat $subcommand\nGM cheats on the selected player or yourself. Type .cheat to see the list.", null, RetailLevel: 3, Children:
        [
            new ChatCommand("god", AccountSecurity.GameMaster, "Syntax: .cheat god on|off [$name]\nThe player takes damage but cannot drop below 1 health.", God, RetailLevel: 3),
            new ChatCommand("cooldown", AccountSecurity.GameMaster, "Syntax: .cheat cooldown on|off [$name]\nNo spell, category or global cooldowns.", Cooldown, RetailLevel: 3),
            new ChatCommand("casttime", AccountSecurity.GameMaster, "Syntax: .cheat casttime on|off [$name]\nEvery spell casts instantly.", CastTime, RetailLevel: 3),
            new ChatCommand("power", AccountSecurity.GameMaster, "Syntax: .cheat power on|off [$name]\nSpells cost no mana, rage or energy.", PowerCost, RetailLevel: 3),
            new ChatCommand("waterwalk", AccountSecurity.GameMaster, "Syntax: .cheat waterwalk on|off\nThe selected player or you walk on water.", WaterWalk, RetailLevel: 3),
            new ChatCommand("immunepc", AccountSecurity.GameMaster, "Syntax: .cheat immunepc on|off [$name]\nPlayers cannot attack the player.", ImmuneToPlayers, RetailLevel: 3),
            new ChatCommand("immunenpc", AccountSecurity.GameMaster, "Syntax: .cheat immunenpc on|off [$name]\nCreatures cannot attack the player.", ImmuneToCreatures, RetailLevel: 3),
            new ChatCommand("untargetable", AccountSecurity.GameMaster, "Syntax: .cheat untargetable on|off [$name]\nNobody can select the player.", Untargetable, RetailLevel: 3),
            new ChatCommand("fly", AccountSecurity.GameMaster, FlySyntax, Fly, RetailLevel: 3),
            new ChatCommand("fixedz", AccountSecurity.GameMaster, "Syntax: .cheat fixedz on|off\nThe selected player or you get a fixed height: a jump glides across the map (MOVEFLAG_FIXED_Z).", FixedZ, RetailLevel: 3),
            new ChatCommand("wallclimb", AccountSecurity.GameMaster, "Syntax: .cheat wallclimb on|off\nThe selected player or you climb walls (UNIT_FLAG_SERVER_CONTROLLED).", WallClimb, RetailLevel: 3),
            new ChatCommand("featherfall", AccountSecurity.GameMaster, "Syntax: .cheat featherfall on|off [$name]\nFalls never hurt (not a vmangos command).", FeatherFall, RetailLevel: 3),
            new ChatCommand("status", AccountSecurity.GameMaster, "Syntax: .cheat status [$name]\nList the cheats active on the player.", Status, RetailLevel: 3),
        ]),
        new ChatCommand("hover", AccountSecurity.GameMaster, "Syntax: .hover [0|1]\nEnable (default) or disable hover mode on yourself.", Hover, RetailLevel: 2),
        new ChatCommand("knockback", AccountSecurity.GameMaster, "Syntax: .knockback [#horizontal_speed [#vertical_speed]]\nKnock the selected player or yourself back from you (10 and 10 by default); a creature is not moved.", KnockBack, RetailLevel: 3),
    ];

    internal const string FlySyntax = "Syntax: .cheat fly on|off\nThe selected player or you swim in the air. Do not jump or flying mode will be removed.";

    // LANG_CHEAT_* (vmangos mangos_string 349-354, 370-371, 621-622) and the LANG_YOU_SET_* / LANG_YOUR_*_SET pairs (355-356, 368-369,
    // 619-620, 623-624, 338-339, 688-693).
    internal const string GodOn = "GOD mode is ON";
    internal const string GodOff = "GOD mode is OFF";
    internal const string CooldownOn = "NO COOLDOWN cheat is ON";
    internal const string CooldownOff = "NO COOLDOWN cheat is OFF";
    internal const string CastTimeOn = "INSTACAST cheat is ON";
    internal const string CastTimeOff = "INSTACAST cheat is OFF";
    internal const string PowerOn = "NO POWER COST cheat is ON";
    internal const string PowerOff = "NO POWER COST cheat is OFF";
    internal const string HoverEnabled = "Hover enabled";
    internal const string HoverDisabled = "Hover disabled";

    private static bool God(CommandContext context, string text)
        => Toggle(context, text, "god mode", (target, on) =>
        {
            target.InvincibilityHpThreshold = on ? 1u : 0u;
            Notify(target, on ? GodOn : GodOff);
        });

    private static bool Cooldown(CommandContext context, string text)
        => Toggle(context, text, "no cooldown mode", (target, on) => SetCheat(target, PlayerCheats.NoCooldown, on, on ? CooldownOn : CooldownOff));

    private static bool CastTime(CommandContext context, string text)
        => Toggle(context, text, "instant casting mode", (target, on) => SetCheat(target, PlayerCheats.NoCastTime, on, on ? CastTimeOn : CastTimeOff));

    private static bool PowerCost(CommandContext context, string text)
        => Toggle(context, text, "no power cost mode", (target, on) => SetCheat(target, PlayerCheats.NoPowerCost, on, on ? PowerOn : PowerOff));

    private static bool ImmuneToPlayers(CommandContext context, string text)
        => Toggle(context, text, "immune to players mode", (target, on) => SetFlag(target, UnitFlags.ImmuneToPlayer, on));

    private static bool ImmuneToCreatures(CommandContext context, string text)
        => Toggle(context, text, "immune to creatures mode", (target, on) => SetFlag(target, UnitFlags.ImmuneToNpc, on));

    private static bool Untargetable(CommandContext context, string text)
        => Toggle(context, text, "untargetable mode", (target, on) => SetFlag(target, UnitFlags.NotSelectable, on));

    // HandleCheatWaterwalkCommand: the selected player only (no name argument), "You set waterwalk mode %s for %s." / "Your waterwalk mode %s by %s.".
    private static bool WaterWalk(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractOnOff(out bool on))
        {
            context.Reply(GmStrings.UseOnOff);
            return true;
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

        MovementControl.Request(target, MovementChangeType.WaterWalk, on);
        target.Cheats = on ? target.Cheats | PlayerCheats.WaterWalk : target.Cheats & ~PlayerCheats.WaterWalk;
        string state = on ? "on" : "off";
        context.Reply($"You set waterwalk mode {state} for {GmStrings.PlayerLink(target.Name)}.");
        if (!context.IsInvoker(target))
        {
            target.SendSystemMessage($"Your waterwalk mode {state} by {GmStrings.PlayerLink(context.InvokerName)}.");
        }

        return true;
    }

    /// <summary>
    /// HandleCheatFlyCommand (CharacterCommands.cpp:95-119), also <c>.gm fly</c> (Chat.cpp:387): ExtractOnOff, the selected player or you,
    /// Player::SetCheatFly (which notifies "FLY MODE cheat is ON|OFF"), "You set fly mode to %s for %s.", the report to anyone else and,
    /// when turned on, the warning that a jump removes fly mode. vmangos has no security check here; this core refuses a target of
    /// higher security, like every other cheat.
    /// </summary>
    internal static bool Fly(CommandContext context, string text)
    {
        if (!TryOnOffTarget(context, text, out bool on, out Player target))
        {
            return true;
        }

        MovementCheats.SetFly(target, on);
        BaselineAntiCheat(context, target);
        Notify(target, on ? GmStrings.CheatFlyOn : GmStrings.CheatFlyOff);
        ReportMovementCheat(context, target, on, GmStrings.YouSetFly, GmStrings.YourFlySet);
        if (on)
        {
            target.SendSystemMessage(GmStrings.FlyWarning);
        }

        return true;
    }

    // HandleCheatFixedZCommand (CharacterCommands.cpp:121-142) over Player::SetCheatFixedZ (Player.cpp:2744-2760).
    private static bool FixedZ(CommandContext context, string text)
    {
        if (!TryOnOffTarget(context, text, out bool on, out Player target))
        {
            return true;
        }

        MovementCheats.SetFixedZ(target, on);
        BaselineAntiCheat(context, target);
        Notify(target, on ? GmStrings.CheatFixedZOn : GmStrings.CheatFixedZOff);
        ReportMovementCheat(context, target, on, GmStrings.YouSetFixedZ, GmStrings.YourFixedZSet);
        return true;
    }

    // HandleCheatWallclimbCommand (CharacterCommands.cpp:509-541): the selected player (or you), the security check, UNIT_FLAG_SERVER_CONTROLLED.
    private static bool WallClimb(CommandContext context, string text)
    {
        if (!TryOnOffTarget(context, text, out bool on, out Player target))
        {
            return true;
        }

        SetFlag(target, UnitFlags.ServerControlled, on);
        ReportMovementCheat(context, target, on, GmStrings.YouSetWallClimb, GmStrings.YourWallClimbSet);
        return true;
    }

    // Not a vmangos command (mangoszero Player::SetFeatherFall is the packet source): the Toggle shape of the other cheats, with a name argument.
    private static bool FeatherFall(CommandContext context, string text)
        => Toggle(context, text, "feather fall mode", (target, on) => MovementCheats.SetFeatherFall(target, on));

    // HandleKnockBackCommand (UnitCommands.cpp:2761-2778): GetSelectedUnit (the selection, else you), two optional floats (10 each; a word that
    // is not a number leaves the default), Unit::KnockBackFrom(player, horizontal, vertical). Only a player is actually moved
    // (Knockback.cs); the invoker's own knockback runs away from where it faces. Extra arguments are ignored, as vmangos does.
    private static bool KnockBack(CommandContext context, string text)
    {
        Unit? target = GmSelectedUnit.Require(context);
        if (target is null || target is Player other && !context.CanActOn(other))
        {
            return true;
        }

        var args = new CommandArgs(text);
        float horizontalSpeed = args.ExtractFloat(out float h) ? h : 10.0f;
        float verticalSpeed = args.ExtractFloat(out float v) ? v : 10.0f;
        SpellSystem system = context.Session.Services.GetRequiredService<SpellFeature>().System;
        KnockbackService.KnockBackFrom(system, target, context.Player, horizontalSpeed, verticalSpeed);
        return true;
    }

    // ExtractOnOff then the selected player or you; replies and returns false when there is no usable target or the invoker is outranked.
    private static bool TryOnOffTarget(CommandContext context, string text, out bool on, out Player target)
    {
        target = null!;
        if (!new CommandArgs(text).ExtractOnOff(out on))
        {
            context.Reply(GmStrings.UseOnOff);
            return false;
        }

        Player? selected = context.SelectedPlayerOrSelf();
        if (selected is null)
        {
            context.Reply(GmStrings.NoCharSelected);
            return false;
        }

        if (!context.CanActOn(selected))
        {
            return false;
        }

        target = selected;
        return true;
    }

    // "You set X mode to %s for %s." and, to anyone else, "Your X mode has been turned %s by %s." (needReportToTarget).
    private static void ReportMovementCheat(CommandContext context, Player target, bool on, Func<string, string, string> you, Func<string, string, string> your)
    {
        string state = on ? "on" : "off";
        context.Reply(you(state, GmStrings.PlayerLink(target.Name)));
        if (!context.IsInvoker(target))
        {
            target.SendSystemMessage(your(state, GmStrings.PlayerLink(context.InvokerName)));
        }
    }

    // The server changed the player's movement flags: the next block the anticheat judges is a new baseline, not a step from before the cheat.
    private static void BaselineAntiCheat(CommandContext context, Player target)
        => context.Session.Services.GetService<AntiCheatFeature>()?.OnServerRelocation(target);

    // HandleCheatStatusCommand (CharacterCommands.cpp:566-613): the vmangos lines for the cheats this core has, in its order; "- Feather fall" is ours.
    private static bool Status(CommandContext context, string text)
    {
        if (!GmTargets.TryPlayer(context, new CommandArgs(text), out Player target))
        {
            return true;
        }

        context.Reply($"Cheats active on {target.Name}:");
        UnitFlags flags = target.UnitFlags;
        if (target.HasCheat(PlayerCheats.Fly))
        {
            context.Reply("- Fly");
        }

        if (target.InvincibilityHpThreshold != 0)
        {
            context.Reply("- God");
        }

        if (target.HasCheat(PlayerCheats.NoCooldown))
        {
            context.Reply("- No cooldowns");
        }

        if (target.HasCheat(PlayerCheats.NoCastTime))
        {
            context.Reply("- No cast time");
        }

        if (target.HasCheat(PlayerCheats.NoPowerCost))
        {
            context.Reply("- No power costs");
        }

        if ((flags & UnitFlags.ImmuneToPlayer) != 0)
        {
            context.Reply("- Immune to players");
        }

        if ((flags & UnitFlags.ImmuneToNpc) != 0)
        {
            context.Reply("- Immune to creatures");
        }

        if ((flags & UnitFlags.NotSelectable) != 0)
        {
            context.Reply("- Untargetable");
        }

        if (target.HasCheat(PlayerCheats.WaterWalk))
        {
            context.Reply("- Water walking");
        }

        if ((flags & UnitFlags.ServerControlled) != 0)
        {
            context.Reply("- Wall climbing");
        }

        if (target.HasCheat(PlayerCheats.FixedZ))
        {
            context.Reply("- Fixed Z");
        }

        if (target.HasCheat(PlayerCheats.FeatherFall))
        {
            context.Reply("- Feather fall");
        }

        return true;
    }

    // HandleHoverCommand: ExtractOptUInt32 (default 1), always yourself.
    private static bool Hover(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractOptUInt32(out uint flag, 1) || !args.IsEmpty)
        {
            return false;
        }

        MovementControl.Request(context.Player, MovementChangeType.Hover, flag != 0);
        context.Reply(flag != 0 ? HoverEnabled : HoverDisabled);
        return true;
    }

    // The shared shape of the on/off cheats: ExtractOnOff then ExtractPlayerTarget (name, selection or self, with the security check),
    // "You set %s to %s for %s." and, to anyone else, "Your %s has been turned %s by %s.".
    private static bool Toggle(CommandContext context, string text, string what, Action<Player, bool> apply)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractOnOff(out bool on))
        {
            context.Reply(GmStrings.UseOnOff);
            return true;
        }

        if (!GmTargets.TryPlayer(context, args, out Player target) || !context.CanActOn(target))
        {
            return true;
        }

        apply(target, on);
        string state = on ? "on" : "off";
        context.Reply(string.Create(CultureInfo.InvariantCulture, $"You set {what} to {state} for {GmStrings.PlayerLink(target.Name)}."));
        if (!context.IsInvoker(target))
        {
            target.SendSystemMessage(string.Create(CultureInfo.InvariantCulture, $"Your {what} has been turned {state} by {GmStrings.PlayerLink(context.InvokerName)}."));
        }

        return true;
    }

    private static void SetCheat(Player target, PlayerCheats cheat, bool on, string notification)
    {
        target.Cheats = on ? target.Cheats | cheat : target.Cheats & ~cheat;
        Notify(target, notification);
    }

    private static void SetFlag(Player target, UnitFlags flag, bool on)
        => target.UnitFlags = on ? target.UnitFlags | flag : target.UnitFlags & ~flag;

    private static void Notify(Player target, string text)
        => target.Session.Send(WorldOpcode.SmsgNotification, ChatPackets.BuildNotification(text));
}

/// <summary>
/// vmangos <c>.gm visible [on/off]</c> (HandleGMVisibleCommand, MiscCommands.cpp; SEC_GAMEMASTER): no argument answers "You are: visible|invisible";
/// off hides you from every player of a lower account security (<see cref="GmInvisibilityRule"/>), on shows you again. <c>.gm fly</c> is
/// <c>.cheat fly</c> under another name (Chat.cpp:387, the same HandleCheatFlyCommand).
/// </summary>
public sealed class GmVisibleExtension : ICommandExtension
{
    // LANG_YOU_ARE 40, LANG_VISIBLE 41, LANG_INVISIBLE 42, LANG_INVISIBLE_INVISIBLE 577, LANG_INVISIBLE_VISIBLE 578.
    internal const string NowInvisible = "You are now invisible.";
    internal const string NowVisible = "You are now visible.";

    public string Path => "gm";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("visible", AccountSecurity.GameMaster, "Syntax: .gm visible [on/off]\nShow or hide yourself from players of a lower security level, or show the current state.", Visible, RetailLevel: 3),
        new ChatCommand("fly", AccountSecurity.GameMaster, GmCheatCommands.FlySyntax.Replace(".cheat fly", ".gm fly", StringComparison.Ordinal), GmCheatCommands.Fly, RetailLevel: 3),
    ];

    private static bool Visible(CommandContext context, string text)
    {
        Player gm = context.Player;
        if (text.Trim().Length == 0)
        {
            context.Reply($"You are: {(gm.GmInvisible ? "invisible" : "visible")}");
            return true;
        }

        if (!new CommandArgs(text).ExtractOnOff(out bool on))
        {
            context.Reply(GmStrings.UseOnOff);
            return true;
        }

        gm.GmInvisible = !on;
        gm.Session.Send(WorldOpcode.SmsgNotification, ChatPackets.BuildNotification(on ? NowVisible : NowInvisible));
        gm.Map?.RefreshVisibility(gm);
        return true;
    }
}

/// <summary>Attaches <see cref="GmInvisibilityRule"/> to every map, as <c>GhostVisibilityFeature</c> does for the ghost rule.</summary>
public sealed class GmInvisibilityFeature : IWorldFeature
{
    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        void Install(Map map) => map.AddVisibilityRule(GmInvisibilityRule.Instance);

        world.MapCreated += Install;
        foreach (Map map in world.Maps)
        {
            Install(map);
        }
    }
}