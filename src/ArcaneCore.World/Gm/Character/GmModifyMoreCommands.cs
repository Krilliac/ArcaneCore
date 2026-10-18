using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Chat;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Character;

/// <summary>
/// More vmangos <c>.modify</c> sub-commands: swim, bwalk (CharacterCommands.cpp:4596-4688), aspeed, morph and emotestate (UnitCommands.cpp:2092-2138,
/// 2203-2243) and drunk (CharacterCommands.cpp:4266-4289). Levels from Chat.cpp:588-601.
/// </summary>
public sealed class GmModifyMoreExtension : ICommandExtension
{
    /// <summary>"Display Id %u does not exist." (vmangos HandleModifyMorphCommand, a literal there).</summary>
    internal static string DisplayIdMissing(uint id) => string.Create(CultureInfo.InvariantCulture, $"Display Id {id} does not exist.");

    public string Path => "modify";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("swim", AccountSecurity.Moderator, "Syntax: .modify swim #rate\nSet the selected player's swim speed rate.", Swim, RetailLevel: 2),
        new ChatCommand("bwalk", AccountSecurity.Moderator, "Syntax: .modify bwalk #rate\nSet the selected player's backwards run speed rate.", BackWalk, RetailLevel: 2),
        new ChatCommand("aspeed", AccountSecurity.Moderator, "Syntax: .modify aspeed #rate\nSet the walk, run and swim speed rate of the selected unit or yourself.", AllSpeeds, RetailLevel: 2),
        new ChatCommand("drunk", AccountSecurity.Moderator, "Syntax: .modify drunk #percent\nSet how drunk the selected player or you are (0-100).", Drunk, RetailLevel: 2),
        new ChatCommand("morph", AccountSecurity.GameMaster, "Syntax: .modify morph #displayid\nChange the model of the selected unit or yourself to #displayid.", Morph, RetailLevel: 3),
        new ChatCommand("emotestate", AccountSecurity.GameMaster, "Syntax: .modify emotestate #emoteid\nSet the state emote of the selected unit (0 ends it).", EmoteState, RetailLevel: 3),
    ];

    private static bool Swim(CommandContext context, string text)
        => PlayerSpeed(context, text, MoveType.Swim, 0.01f, "swim speed");

    private static bool BackWalk(CommandContext context, string text)
        => PlayerSpeed(context, text, MoveType.RunBack, 0.1f, "backwards run speed");

    // LANG_YOU_CHANGE_SWIM_SPEED 141 / LANG_YOURS_SWIM_SPEED_CHANGED 142, LANG_YOU_CHANGE_BACK_SPEED 143 / LANG_YOURS_BACK_SPEED_CHANGED 144
    // (mangos-classic mangos.sql:3529-3532).
    private static bool PlayerSpeed(CommandContext context, string text, MoveType type, float minimum, string what)
    {
        if (!ReadRate(context, text, out float rate))
        {
            return false;
        }

        if (rate > 100f || rate < minimum)
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
        if (IsTaxiFlying(context, target))
        {
            context.Reply($"{link} is flying command failed.");
            return true;
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture, $"You set the {what} to {rate:0.00} from normal of {link}."));
        if (!context.IsInvoker(target))
        {
            target.SendSystemMessage(string.Create(CultureInfo.InvariantCulture,
                $"{GmStrings.PlayerLink(context.InvokerName)} set your {what} to {rate:0.00} from normal."));
        }

        UnitSpeed.SetRate(target, type, rate);
        return true;
    }

    // LANG_YOU_CHANGE_ASPEED 137 (mangos-classic mangos.sql:3525); vmangos names a creature "<creature>" and tells nobody else.
    private static bool AllSpeeds(CommandContext context, string text)
    {
        if (!ReadRate(context, text, out float rate))
        {
            return false;
        }

        if (rate > 100f || rate < 0f)
        {
            context.Reply(GmStrings.BadValue);
            return true;
        }

        Unit target = GmSelectedUnit.Of(context) ?? context.Player;
        if (target is Player player && !context.CanActOn(player))
        {
            return true;
        }

        string name = target is Player named ? named.Name : "<creature>";
        if (target is Player flying && IsTaxiFlying(context, flying))
        {
            context.Reply($"{name} is flying command failed.");
            return true;
        }

        context.Reply(string.Create(CultureInfo.InvariantCulture, $"You set all speeds to {rate:0.00} from normal of {name}."));
        UnitSpeed.SetRate(target, MoveType.Walk, rate);
        UnitSpeed.SetRate(target, MoveType.Run, rate);
        UnitSpeed.SetRate(target, MoveType.Swim, rate);
        return true;
    }

    private static bool Drunk(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint level) || !args.IsEmpty)
        {
            return false;
        }

        Player? target = context.SelectedPlayerOrSelf();
        if (target is null)
        {
            context.Reply(GmStrings.PlayerNotFound);
            return true;
        }

        if (!context.CanActOn(target))
        {
            return true;
        }

        level = Math.Min(level, 100u);
        context.Session.Services.GetRequiredService<SpellFeature>().System.SetDrunkValue(target, (ushort)(level * 0xFFFF / 100));
        return true;
    }

    private static bool Morph(CommandContext context, string text)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint displayId) || !args.IsEmpty)
        {
            return false;
        }

        // sCreatureDisplayInfoStore.LookupEntry: checked when the client's CreatureDisplayInfo.dbc is loaded.
        var spells = context.Session.Services.GetRequiredService<SpellFeature>().System;
        if (displayId == 0
            || context.Session.Services.GetService<CreatureDisplayModelMetadataFeature>()?.Content is { Count: > 0 } displays && displays.Find(displayId) is null)
        {
            context.Reply(DisplayIdMissing(displayId));
            return true;
        }

        Unit target = GmSelectedUnit.Of(context) ?? context.Player;
        if (target is Player player && !context.CanActOn(player))
        {
            return true;
        }

        target.DisplayId = displayId;
        spells.UpdateDisplayModel(target);
        return true;
    }

    private static bool EmoteState(CommandContext context, string text)
    {
        Unit? target = GmSelectedUnit.Require(context);
        if (target is null)
        {
            return true;
        }

        var args = new CommandArgs(text);
        if (!args.ExtractUInt32(out uint emote) || !args.IsEmpty)
        {
            return false;
        }

        // sEmotesStore.LookupEntry, when the client's Emotes.dbc is loaded.
        if (emote != 0 && context.Session.Services.GetService<ChatFeature>()?.Emotes is { } emotes && emotes.Emote(emote) is null)
        {
            return false;
        }

        if (target is Player player && !context.CanActOn(player))
        {
            return true;
        }

        target.SetUInt32(UpdateFields.UnitNpcEmotestate, emote); // Unit::HandleEmoteState
        return true;
    }

    private static bool ReadRate(CommandContext context, string text, out float rate)
    {
        var args = new CommandArgs(text);
        if (!args.ExtractFloat(out rate) || !args.IsEmpty || !float.IsFinite(rate))
        {
            return false;
        }

        // vmangos: below SEC_BASIC_ADMIN the rate is capped at 4.
        if (context.Commands.Gm.LevelOf(context.Security) < 4 && rate > 4f)
        {
            rate = 4f;
        }

        return true;
    }

    private static bool IsTaxiFlying(CommandContext context, Player player)
        => context.Session.Services.GetService<NpcServicesFeature>()?.Flights is { } flights && flights.IsFlying(player);
}