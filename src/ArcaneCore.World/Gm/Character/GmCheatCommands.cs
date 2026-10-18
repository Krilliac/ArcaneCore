using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Features;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Packets;

namespace ArcaneCore.World.Gm.Character;

/// <summary>
/// vmangos <c>.cheat</c> (CharacterCommands.cpp, HandleCheat*Command; every entry SEC_GAMEMASTER in Chat.cpp): god, cooldown, casttime,
/// powercost, waterwalk, immunepc, immunenpc, untargetable and status, plus <c>.hover</c> (SEC_TICKETMASTER, HandleHoverCommand). The
/// cooldown/cast time/power options live in <see cref="Player.Cheats"/> (the spell side is <c>GmCheatSpellModule</c>); god is
/// <c>InvincibilityHpThreshold</c> 1 (Player::SetCheatGod). Fly and the other movement hacks are not ported: the anticheat would
/// flag a server-forced fly flag. None of it is saved (vmangos does not save the cheat options either).
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
            new ChatCommand("status", AccountSecurity.GameMaster, "Syntax: .cheat status [$name]\nList the cheats active on the player.", Status, RetailLevel: 3),
        ]),
        new ChatCommand("hover", AccountSecurity.GameMaster, "Syntax: .hover [0|1]\nEnable (default) or disable hover mode on yourself.", Hover, RetailLevel: 2),
    ];

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

    // HandleCheatStatusCommand: the vmangos lines for the cheats this core has.
    private static bool Status(CommandContext context, string text)
    {
        if (!GmTargets.TryPlayer(context, new CommandArgs(text), out Player target))
        {
            return true;
        }

        context.Reply($"Cheats active on {target.Name}:");
        UnitFlags flags = target.UnitFlags;
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
/// off hides you from every player of a lower account security (<see cref="GmInvisibilityRule"/>), on shows you again.
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