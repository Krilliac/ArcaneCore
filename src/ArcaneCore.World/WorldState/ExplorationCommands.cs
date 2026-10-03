using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Exploration;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Packets;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// <c>.explorecheat</c>, <c>.showarea</c>, <c>.hidearea</c> (vmangos CharacterCommands.cpp:640-679,
/// :764-830; Chat.cpp:1272-1276 SEC_TICKETMASTER, the nearest tier here is Moderator).
/// </summary>
public sealed class ExplorationCommands : ICommandGroup
{
    /// <summary>mangos_string 116 LANG_NO_CHAR_SELECTED.</summary>
    public const string NoCharSelectedText = "No character selected.";

    /// <summary>mangos_string 115 LANG_BAD_VALUE.</summary>
    public const string BadValueText = "Incorrect values.";

    /// <summary>mangos_string 551 / 552 / 553 / 554 (the placeholder is the player name link).</summary>
    public const string YouSetExploreAllText = "{0} has explored all zones now.";
    public const string YouSetExploreNothingText = "{0} has no more explored zones.";
    public const string YoursExploreSetAllText = "{0} has explored all zones for you.";
    public const string YoursExploreSetNothingText = "{0} has hidden all zones from you.";

    /// <summary>mangos_string 560 / 561.</summary>
    public const string ExploreAreaText = "The area has been set as explored.";
    public const string UnexploreAreaText = "The area has been set as not explored.";

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("explorecheat", AccountSecurity.Moderator, "Syntax: .explorecheat #flag - 1 reveals every zone, 0 hides them (vmangos: the effect lands on you).", ExploreCheat),
        new ChatCommand("showarea", AccountSecurity.Moderator, "Syntax: .showarea #areaid - reveal an area on the selected player.", ShowArea),
        new ChatCommand("hidearea", AccountSecurity.Moderator, "Syntax: .hidearea #areaid - toggle an area's explored bit on the selected player (vmangos XORs it).", HideArea),
    ];

    private static string Link(Player player) => $"|Hplayer:{player.Name}|h[{player.Name}]|h";

    // vmangos HandleExploreCheatCommand: atoi flag; announces the SELECTED player but changes the issuer's
    // fields, and flag 0 ORs in 0 (a no-op). World:Exploration:CorrectExploreCheat applies it to the target and clears on 0.
    private static bool ExploreCheat(CommandContext context, string args)
    {
        string text = args.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        int flag = AtoI(text);
        Player? target = context.SelectedPlayerOrSelf();
        if (target is null)
        {
            context.Reply(NoCharSelectedText);
            return true;
        }

        context.Reply(string.Format(CultureInfo.InvariantCulture, flag != 0 ? YouSetExploreAllText : YouSetExploreNothingText, Link(target)));
        if (!ReferenceEquals(target, context.Player))
        {
            target.Session.Send(WorldOpcode.SmsgMessagechat, ChatPackets.BuildSystemMessage(
                string.Format(CultureInfo.InvariantCulture, flag != 0 ? YoursExploreSetAllText : YoursExploreSetNothingText, Link(context.Player))));
        }

        WorldStateHooks hooks = WorldStateHooks.For(context.World);
        bool correct = hooks.ExplorationSettings.CorrectExploreCheat;
        Player subject = correct ? target : context.Player;
        for (int i = 0; i < ExploredZones.WordCount; i++)
        {
            uint current = subject.GetUInt32(UpdateFields.PlayerExploredZones1 + i);
            subject.SetUInt32(UpdateFields.PlayerExploredZones1 + i, flag != 0 ? 0xFFFFFFFF : correct ? 0u : current);
        }

        hooks.ExploredZonesSink?.Changed(subject);
        return true;
    }

    private static bool ShowArea(CommandContext context, string args) => SetArea(context, args, toggle: false);

    private static bool HideArea(CommandContext context, string args) => SetArea(context, args, toggle: true);

    // vmangos HandleShowAreaCommand / HandleHideAreaCommand: the area id becomes its explore flag
    // (AreaEntry::GetFlagById); show ORs the bit in, hide XORs it (so hiding an unexplored area explores it).
    private static bool SetArea(CommandContext context, string args, bool toggle)
    {
        string text = args.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        Player? target = context.SelectedPlayerOrSelf();
        if (target is null)
        {
            context.Reply(NoCharSelectedText);
            return true;
        }

        WorldStateHooks hooks = WorldStateHooks.For(context.World);
        AreaTemplate? area = hooks.Locator.Find((uint)AtoI(text));
        // AreaEntry::GetFlagById returns -1 for an unknown id (vmangos Map.h:121-128); that and an offset of 64 and above are a "bad value".
        uint flag = area?.ExploreFlag ?? 0;
        if (area is null)
        {
            context.Reply(BadValueText);
            return true;
        }

        if (!ExploredZones.TryLocate(flag, out int offset, out uint mask))
        {
            context.Reply(BadValueText);
            return true;
        }

        uint current = target.GetUInt32(UpdateFields.PlayerExploredZones1 + offset);
        target.SetUInt32(UpdateFields.PlayerExploredZones1 + offset, toggle ? current ^ mask : current | mask);
        hooks.ExploredZonesSink?.Changed(target);
        context.Reply(toggle ? UnexploreAreaText : ExploreAreaText);
        return true;
    }

    // C atoi: optional sign and leading digits, anything else is 0.
    private static int AtoI(string text)
    {
        int end = 0;
        if (end < text.Length && (text[end] is '-' or '+'))
        {
            end++;
        }

        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        return int.TryParse(text.AsSpan(0, end), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value) ? value : 0;
    }
}
