using ArcaneCore.Game.Entities;
using ArcaneCore.Game;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Progression;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Character;

/// <summary>
/// <c>.modify hp</c>, <c>.modify mana</c>, <c>.modify energy</c> and <c>.modify rage</c>
/// (vmangos UnitCommands.cpp:2283-2349, CharacterCommands.cpp:4733-4808, SEC_GAMEMASTER,
/// Chat.cpp:582-583), added under the <c>.modify</c> root of the built-in commands, whose
/// <c>.modify money</c> lives in <see cref="BuiltinCommands"/>. Player targets only: vmangos also
/// accepts a selected creature for HP/mana. <c>.modify scale|faction|speed|aspeed|swim|bwalk|
/// mount|morph|drunk|exhaustion|talentpoints</c> and the rest are not provided.
/// </summary>
public sealed class ModifyExtension : ICommandExtension
{
    public string Path => "modify";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("hp", AccountSecurity.GameMaster, "Syntax: .modify hp #newhp [#newmaxhp]\nChange the HP (and maximum HP) of the selected player, or yours.", (c, a) => Change(c, a, hp: true), RetailLevel: 3),
        new ChatCommand("mana", AccountSecurity.GameMaster, "Syntax: .modify mana #newmana [#newmaxmana]\nChange the mana (and maximum mana) of the selected player, or yours.", (c, a) => Change(c, a, hp: false), RetailLevel: 3),
        new ChatCommand("energy", AccountSecurity.GameMaster, "Syntax: .modify energy #newenergy [#newmaxenergy]\nChange the energy (and maximum energy) of the selected player, or yours.", (c, a) => ChangePower(c, a, PowerType.Energy), RetailLevel: 3),
        new ChatCommand("rage", AccountSecurity.GameMaster, "Syntax: .modify rage #newrage [#newmaxrage]\nChange the rage (and maximum rage) of the selected player, or yours.", (c, a) => ChangePower(c, a, PowerType.Rage), RetailLevel: 3),
    ];

    private static bool Change(CommandContext context, string text, bool hp)
    {
        if (text.Length == 0)
        {
            return false;
        }

        // ExtractInt32 twice, ignoring failure (the values stay 0): UnitCommands.cpp:2289-2292.
        var args = new CommandArgs(text);
        args.ExtractInt32(out int value);
        args.ExtractInt32(out int max);
        if (max < value)
        {
            max = value;
        }

        if (value <= 0)
        {
            context.Reply(GmStrings.BadValue);
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

        string link = GmStrings.PlayerLink(target.Name);
        bool report = !ReferenceEquals(target, context.Player);
        string caller = GmStrings.PlayerLink(context.Player.Name);
        if (hp)
        {
            context.Reply(GmStrings.YouChangeHp(link, value, max));
            if (report)
            {
                target.SendSystemMessage(GmStrings.YoursHpChanged(caller, value, max));
            }

            target.MaxHealth = (uint)max;
            target.Health = (uint)value;
        }
        else
        {
            context.Reply(GmStrings.YouChangeMana(link, value, max));
            if (report)
            {
                target.SendSystemMessage(GmStrings.YoursManaChanged(caller, value, max));
            }

            // Power index 0 (mana) whatever the unit's own power type, as UnitCommands.cpp:2344-2345.
            target.SetUInt32(UpdateFields.UnitFieldMaxpower1, (uint)max);
            target.SetUInt32(UpdateFields.UnitFieldPower1, (uint)value);
        }

        return true;
    }

    private static bool ChangePower(CommandContext context, string text, PowerType power)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint requested))
        {
            return false;
        }

        bool hasExplicitMax = !args.IsEmpty;
        if (!args.ExtractOptUInt32(out uint requestedMax, 0) || !args.IsEmpty)
        {
            return false;
        }

        uint factor = power == PowerType.Rage ? 10u : 1u;
        if (requested > uint.MaxValue / factor)
        {
            context.Reply(GmStrings.BadValue);
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

        uint currentMax = target.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)power);
        uint displayedMax = hasExplicitMax
            ? requestedMax
            : Math.Max(currentMax / factor, requested);
        if (displayedMax < requested || displayedMax > uint.MaxValue / factor)
        {
            context.Reply(GmStrings.BadValue);
            return true;
        }

        uint max = displayedMax * factor;
        uint value = requested * factor;
        string link = GmStrings.PlayerLink(target.Name);
        bool report = !ReferenceEquals(target, context.Player);
        string caller = GmStrings.PlayerLink(context.Player.Name);

        target.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)power, max);
        SpellSystem.SetPower(target, power, value);
        context.World.SavePlayer(target);

        if (power == PowerType.Energy)
        {
            context.Reply(GmStrings.YouChangeEnergy(link, requested, displayedMax));
            if (report)
            {
                target.SendSystemMessage(GmStrings.YoursEnergyChanged(caller, requested, displayedMax));
            }
        }
        else
        {
            context.Reply(GmStrings.YouChangeRage(link, requested, displayedMax));
            if (report)
            {
                target.SendSystemMessage(GmStrings.YoursRageChanged(caller, requested, displayedMax));
            }
        }

        return true;
    }
}

/// <summary>
/// <c>.levelup</c>, <c>.replenish</c> and <c>.deplenish</c> (vmangos CharacterCommands.cpp:695-762,
/// 1849-1872; UnitCommands.cpp:2351-2403; all SEC_GAMEMASTER, Chat.cpp:1194-1195,1274).
/// <para>
/// Differences, documented in docs/integration/gm-commands.md: all three apply the target-rank check
/// (<see cref="CommandContext.CanActOn"/>; vmangos has none on these commands); the level is capped at the
/// progression maximum (<c>Progression:MaxPlayerLevel</c>, default 60) instead of vmangos' hard 255,
/// because the level-stat table ends there; the talent recalculation (<c>InitTalentForLevel</c>)
/// is not done (the talents area does not exist yet); a selected creature is not levelled and
/// offline characters are not edited (vmangos updates the characters table); and a target is told
/// about the change unless it is the invoker (vmangos also stays quiet for GMs that are invisible
/// to it, which needs the GM-visibility state).
/// </para>
/// </summary>
public sealed class LevelCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("levelup", AccountSecurity.GameMaster, "Syntax: .levelup [$playername] [#numberoflevels]\nIncrease/decrease the level of the selected player or the named one (default +1); the experience of the level is reset.", LevelUp, RetailLevel: 3),
        new ChatCommand("replenish", AccountSecurity.GameMaster, "Syntax: .replenish\nRestore the health, and the mana of a mana user, of the selected unit or yourself.", Replenish, RetailLevel: 3),
        new ChatCommand("deplenish", AccountSecurity.GameMaster, "Syntax: .deplenish\nSet the health of the selected unit or yourself to 1 and its power to 0.", Deplenish, RetailLevel: 3),
    ];

    private static bool LevelUp(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        int addLevel = 1;
        string? nameArg = null;
        if (!args.IsEmpty)
        {
            nameArg = args.ExtractOptNotLastArg();

            // Exception to the optional second argument: ".levelup $name".
            if (!args.ExtractInt32(out addLevel))
            {
                if (nameArg is not null)
                {
                    return false;
                }

                addLevel = 1;
                nameArg = args.ExtractArg();
            }
        }

        if (nameArg is null && !context.Player.Selection.IsEmpty && context.SelectedPlayerOrSelf() is null)
        {
            context.Reply(GmStrings.LevelingCreaturesUnsupported);
            return true;
        }

        if (!GmTargets.TryPlayer(context, nameArg, out Player? target) || !context.CanActOn(target))
        {
            return true;
        }

        PlayerProgression progression = context.Session.Services.GetRequiredService<ProgressionFeature>().Progression;
        int oldLevel = target.Level;
        int newLevel = Math.Clamp(oldLevel + addLevel, 1, progression.MaxPlayerLevel);
        progression.GiveLevel(target, (byte)newLevel);
        target.SetUInt32(UpdateFields.PlayerXp, 0);

        if (!ReferenceEquals(target, context.Player))
        {
            string caller = GmStrings.PlayerLink(context.Player.Name);
            target.SendSystemMessage(oldLevel == newLevel ? GmStrings.YoursLevelProgressReset(caller)
                : oldLevel < newLevel ? GmStrings.YoursLevelUp(caller, newLevel)
                : GmStrings.YoursLevelDown(caller, newLevel));
            context.Reply(GmStrings.YouChangeLevel(GmStrings.PlayerLink(target.Name), newLevel));
        }

        return true;
    }

    private static bool Replenish(CommandContext context, string text)
    {
        if (context.SelectedPlayerOrSelf() is not { IsAlive: true } unit)
        {
            context.Reply(GmStrings.SelectCharOrCreature);
            return true;
        }

        if (!context.CanActOn(unit))
        {
            return true;
        }

        unit.Health = unit.MaxHealth;
        if (unit.PowerType == PowerType.Mana)
        {
            unit.SetUInt32(UpdateFields.UnitFieldPower1, unit.GetUInt32(UpdateFields.UnitFieldMaxpower1));
        }

        return true;
    }

    private static bool Deplenish(CommandContext context, string text)
    {
        if (context.SelectedPlayerOrSelf() is not { IsAlive: true } unit)
        {
            context.Reply(GmStrings.SelectCharOrCreature);
            return true;
        }

        if (!context.CanActOn(unit))
        {
            return true;
        }

        unit.Health = 1;
        unit.SetUInt32(UpdateFields.UnitFieldPower1 + (int)unit.PowerType, 0);
        return true;
    }
}
