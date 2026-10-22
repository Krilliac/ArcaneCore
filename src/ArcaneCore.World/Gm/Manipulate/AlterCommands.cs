using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Gm.Inspect;
using ArcaneCore.World.Gm.Npc;

namespace ArcaneCore.World.Gm.Manipulate;

/// <summary>
/// The <c>.alter</c> setters (no vmangos equivalent; they fill the gaps <c>.modify</c> and <c>.npc set</c> leave): live state of the selected
/// unit (yourself without a selection) that is not saved for creatures and is saved with the character for players only where the field
/// is part of the character (health, power, level are recomputed or saved as usual). <c>health</c>, <c>power</c>, <c>unitflag</c>,
/// <c>standstate</c> take any unit; <c>level</c>, <c>react</c>, <c>threat</c>, <c>home</c>, <c>respawn</c> a creature; <c>field</c> writes a raw
/// update field (administrator).
/// </summary>
public sealed class AlterCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("alter", AccountSecurity.GameMaster,
            "Syntax: .alter $subcommand\nChange live state of the selected unit (yourself without a selection). Type .alter to see the list of possible subcommands.",
            Children:
            [
                new ChatCommand("health", AccountSecurity.GameMaster,
                    "Syntax: .alter health #value|#percent%\nSet the current health of the selected unit (1 to its maximum; use .die to kill).",
                    Health, RetailLevel: 3),
                new ChatCommand("power", AccountSecurity.GameMaster,
                    "Syntax: .alter power #value|#percent%\nSet the current mana, rage or energy (its own power type) of the selected unit, up to its maximum.",
                    Power, RetailLevel: 3),
                new ChatCommand("unitflag", AccountSecurity.GameMaster,
                    "Syntax: .alter unitflag $flag|#hex on|off\nSet or clear one unit flag (by name, e.g. Stunned, Pacified, NotSelectable, ImmuneToPlayer, or as a hex bit) on the selected unit.",
                    UnitFlag, RetailLevel: 3),
                new ChatCommand("standstate", AccountSecurity.GameMaster,
                    "Syntax: .alter standstate $state|#state\nSet the stand state of the selected unit: Stand, Sit, SitChair, Sleep, Kneel, Dead (0-9).",
                    StandStateCommand, RetailLevel: 3),
                new ChatCommand("level", AccountSecurity.GameMaster,
                    "Syntax: .alter level #level\nSet the level of the selected creature (1-255; not saved). For players use .levelup or .reset level.",
                    Level, RetailLevel: 3),
                new ChatCommand("react", AccountSecurity.GameMaster,
                    "Syntax: .alter react passive|defensive|aggressive\nSet the react state of the selected creature (until it respawns or a script changes it).",
                    React, RetailLevel: 3),
                new ChatCommand("threat", AccountSecurity.GameMaster,
                    "Syntax: .alter threat #amount\nSet your threat on the selected creature's threat list (it attacks you when it was not fighting); 0 removes you from the list.",
                    Threat, RetailLevel: 3),
                new ChatCommand("home", AccountSecurity.GameMaster,
                    "Syntax: .alter home\nMake the selected creature's current position its home (where it evades and wanders back to; not saved).",
                    Home, RetailLevel: 3),
                new ChatCommand("respawn", AccountSecurity.GameMaster,
                    "Syntax: .alter respawn #seconds\nSet the delay of the selected creature's next respawn (once).",
                    Respawn, RetailLevel: 3),
                new ChatCommand("field", AccountSecurity.Administrator,
                    "Syntax: .alter field #index #value|#hex|#float f\nWrite a raw update field of the selected unit (sent to the clients that see it). Debugging only: the server may overwrite it at any time.",
                    Field, RetailLevel: 6),
            ], RetailLevel: 3),
    ];

    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>Parse "#value" or "#percent%" against <paramref name="max"/>; false on bad input.</summary>
    internal static bool TryAmount(string text, uint max, out uint value)
    {
        value = 0;
        string t = text.Trim();
        if (t.EndsWith('%'))
        {
            if (!float.TryParse(t[..^1], NumberStyles.Float, C, out float pct) || !float.IsFinite(pct) || pct < 0 || pct > 100)
            {
                return false;
            }

            value = (uint)MathF.Round(max * pct / 100f);
            return true;
        }

        return uint.TryParse(t, NumberStyles.None, C, out value);
    }

    private static bool Health(CommandContext context, string args)
    {
        if (args.Trim().Length == 0) return false;
        if (InspectCommands.Target(context) is not { } unit) return true;
        if (!TryAmount(args, unit.MaxHealth, out uint value)) return false;
        if (!unit.IsAlive)
        {
            context.Reply($"{InspectCommands.Name(unit)} is dead; use .revive or .creature respawn.");
            return true;
        }

        value = Math.Clamp(value, 1, Math.Max(1, unit.MaxHealth));
        unit.Health = value;
        context.Reply(string.Create(C, $"{InspectCommands.Name(unit)} health set to {value}/{unit.MaxHealth}."));
        return true;
    }

    private static bool Power(CommandContext context, string args)
    {
        if (args.Trim().Length == 0) return false;
        if (InspectCommands.Target(context) is not { } unit) return true;
        int type = (int)unit.PowerType;
        if (type is < 0 or > 4)
        {
            context.Reply($"{InspectCommands.Name(unit)} has no power.");
            return true;
        }

        uint max = unit.GetUInt32(UpdateFields.UnitFieldMaxpower1 + type);
        if (!TryAmount(args, max, out uint value)) return false;
        value = Math.Min(value, max);
        unit.SetUInt32(UpdateFields.UnitFieldPower1 + type, value);
        context.Reply(string.Create(C, $"{InspectCommands.Name(unit)} {unit.PowerType} set to {value}/{max}."));
        return true;
    }

    /// <summary>A single <see cref="UnitFlags"/> bit by name or hex; null when unknown or not one bit.</summary>
    internal static uint? ParseUnitFlag(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, C, out uint hex))
        {
            return hex != 0 && (hex & (hex - 1)) == 0 ? hex : null;
        }

        return CommandArgs.TryParseEnumName<UnitFlags, int>(text, requireDefined: false, out UnitFlags flag) && (uint)flag != 0 && ((uint)flag & ((uint)flag - 1)) == 0
            ? (uint)flag
            : null;
    }

    private static bool UnitFlag(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        string? name = parsed.ExtractArg();
        if (name is null || !parsed.ExtractOnOff(out bool on) || !parsed.IsEmpty)
        {
            return false;
        }

        if (ParseUnitFlag(name) is not { } bit)
        {
            context.Reply($"Unknown unit flag '{name}'. Names: {string.Join(", ", Enum.GetNames<UnitFlags>().Where(n => n != nameof(UnitFlags.None)))}.");
            return true;
        }

        if (InspectCommands.Target(context) is not { } unit) return true;
        unit.UnitFlags = on ? unit.UnitFlags | (UnitFlags)bit : unit.UnitFlags & ~(UnitFlags)bit;
        context.Reply(string.Create(C,
            $"{InspectCommands.Name(unit)} unit flag {InspectCommands.FlagNames<UnitFlags>(bit)} {(on ? "set" : "cleared")}; flags now 0x{(uint)unit.UnitFlags:X8}."));
        return true;
    }

    private static bool StandStateCommand(CommandContext context, string args)
    {
        string text = args.Trim();
        if (text.Length == 0) return false;
        StandState state;
        if (byte.TryParse(text, NumberStyles.None, C, out byte raw) && raw <= (byte)StandState.Custom)
        {
            state = (StandState)raw;
        }
        else if (!CommandArgs.TryParseEnumName<StandState, byte>(text, requireDefined: true, out state))
        {
            return false;
        }

        if (InspectCommands.Target(context) is not { } unit) return true;
        unit.StandState = state;
        context.Reply($"{InspectCommands.Name(unit)} stand state set to {state}.");
        return true;
    }

    private static bool Level(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        if (!parsed.ExtractUInt32(out uint level) || !parsed.IsEmpty || level is 0 or > 255) return false;
        if (InspectCommands.TargetCreature(context) is not { } creature) return true;
        creature.Level = (byte)level;
        context.Reply(string.Create(C, $"{InspectCommands.Describe(creature)} level set to {level}."));
        return true;
    }

    private static bool React(CommandContext context, string args)
    {
        string text = args.Trim();
        if (!CommandArgs.TryParseEnumName<CreatureReactState, byte>(text, requireDefined: true, out CreatureReactState state))
        {
            return false;
        }

        if (InspectCommands.TargetCreature(context) is not { } creature) return true;
        creature.ReactState = state;
        context.Reply($"{InspectCommands.Describe(creature)} react state set to {state}.");
        return true;
    }

    private static bool Threat(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        if (!parsed.ExtractFloat(out float amount) || !parsed.IsEmpty || !float.IsFinite(amount) || amount < 0 || amount > 1e9f) return false;
        if (InspectCommands.TargetCreature(context) is not { } creature) return true;
        if (GmNpcCommands.SystemOf(context) is not { } system) return true;
        Player me = context.Player;
        if (amount == 0)
        {
            if (creature.Combat.HasThreatList) creature.Combat.Threat.Remove(me);
            context.Reply($"You are off {InspectCommands.Describe(creature)}'s threat list.");
            return true;
        }

        if (!creature.IsAlive || !me.IsAlive)
        {
            context.Reply("Both you and the creature must be alive.");
            return true;
        }

        if (!creature.Combat.IsInCombat || !creature.Combat.Threat.Contains(me))
        {
            system.AttackStart(creature, me);
        }

        float current = creature.Combat.Threat.GetThreat(me);
        creature.Combat.Threat.AddThreat(me, amount - current);
        context.Reply(string.Create(C, $"Your threat on {InspectCommands.Describe(creature)} is now {creature.Combat.Threat.GetThreat(me):F1}."));
        return true;
    }

    private static bool Home(CommandContext context, string args)
    {
        if (args.Trim().Length != 0) return false;
        if (InspectCommands.TargetCreature(context) is not { } creature) return true;
        if (GmNpcCommands.SystemOf(context) is not { } system) return true;
        system.SetHomePosition(creature, creature.X, creature.Y, creature.Z, creature.Orientation);
        context.Reply(string.Create(C, $"{InspectCommands.Describe(creature)} home set to {creature.X:F2} {creature.Y:F2} {creature.Z:F2}."));
        return true;
    }

    private static bool Respawn(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        if (!parsed.ExtractUInt32(out uint seconds) || !parsed.IsEmpty || seconds > 7 * 24 * 3600) return false;
        if (InspectCommands.TargetCreature(context) is not { } creature) return true;
        if (GmNpcCommands.SystemOf(context) is not { } system) return true;
        system.SetNextRespawnDelay(creature, seconds);
        context.Reply(string.Create(C, $"{InspectCommands.Describe(creature)} respawns {seconds} s after its next death."));
        return true;
    }

    private static bool Field(CommandContext context, string args)
    {
        var parsed = new CommandArgs(args);
        string? indexText = parsed.ExtractArg();
        string? valueText = parsed.ExtractArg();
        string? suffix = parsed.ExtractArg();
        if (indexText is null || valueText is null || !parsed.IsEmpty || (suffix is not null && suffix != "f")
            || !int.TryParse(indexText, NumberStyles.None, C, out int index))
        {
            return false;
        }

        uint value;
        if (suffix == "f")
        {
            if (!float.TryParse(valueText, NumberStyles.Float, C, out float f) || !float.IsFinite(f)) return false;
            value = BitConverter.SingleToUInt32Bits(f);
        }
        else if (valueText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!uint.TryParse(valueText.AsSpan(2), NumberStyles.HexNumber, C, out value)) return false;
        }
        else if (!uint.TryParse(valueText, NumberStyles.None, C, out value))
        {
            if (!int.TryParse(valueText, NumberStyles.AllowLeadingSign, C, out int signed)) return false;
            value = unchecked((uint)signed);
        }

        if (InspectCommands.Target(context) is not { } unit) return true;
        if (index < 0 || index >= unit.ValuesCount)
        {
            context.Reply(string.Create(C, $"{InspectCommands.Name(unit)} has {unit.ValuesCount} update fields (0-{unit.ValuesCount - 1})."));
            return true;
        }

        uint old = unit.GetUInt32(index);
        unit.SetUInt32(index, value);
        context.Reply(string.Create(C, $"{InspectCommands.Name(unit)} field [{index}] 0x{old:X8} -> 0x{value:X8}."));
        return true;
    }
}
