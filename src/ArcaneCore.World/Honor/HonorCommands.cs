using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Honor;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Honor;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Honor;

/// <summary>
/// <c>.honor add|addkill|show|setrp|reset</c> (vmangos CharacterCommands.cpp:2321-2570; Chat.cpp:467-475, 1198: the group is
/// SEC_GAMEMASTER, <c>show</c> SEC_TICKETMASTER, the rest SEC_BASIC_ADMIN). Texts are mangos_string 1400-1439 (mangos-classic
/// mangos.sql:4164-4203, which vmangos' base rows share). Deliberate differences: <c>show</c> names the ranks by INTERNAL rank
/// (vmangos indexes its internal-rank name table with the visual rank, which prints the wrong names and, for a negative rank, wraps to
/// a huge index and prints "CrashAlert"); retail's separate <c>.reset honor</c> (SEC_DEVELOPER, Chat.cpp:915) is not provided, the
/// <c>.honor reset</c> below is the same <c>HonorMgr::Reset</c>.
/// </summary>
public sealed class HonorCommands : ICommandGroup
{
    /// <summary>The player-facing text when the honor feature is switched off or absent (no vmangos equivalent).</summary>
    public const string HonorUnavailable = "Honor is not enabled on this server.";

    // Indexed by INTERNAL rank 0..18 (mangos_string 1429, 1433-1430, 1400-1413 / 1414-1427).
    private static readonly string[] AllianceRanks =
    [
        "No Rank ", "Pariah ", "Outlaw ", "Exiled ", "Dishonored ", "Private ", "Corporal ", "Sergeant ", "Master Sergeant ", "Sergeant Major ",
        "Knight ", "Knight-Lieutenant ", "Knight-Captain ", "Knight-Champion ", "Lieutenant Commander ", "Commander ", "Marshal ", "Field Marshal ", "Grand Marshal ",
    ];

    private static readonly string[] HordeRanks =
    [
        "No Rank ", "Pariah ", "Outlaw ", "Exiled ", "Dishonored ", "Scout ", "Grunt ", "Sergeant ", "Senior Sergeant ", "First Sergeant ",
        "Stone Guard ", "Blood Guard ", "Legionnaire ", "Centurion ", "Champion ", "Lieutenant General ", "General ", "Warlord ", "High Warlord ",
    ];

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("honor", AccountSecurity.GameMaster, "Syntax: .honor $subcommand\nType .honor to see the list of possible subcommands or .help honor $subcommand to see info on subcommands.", Children:
        [
            new ChatCommand("add", AccountSecurity.Administrator, "Syntax: .honor add #amount\nAdd honor points to the selected player (or yourself).", Add, RetailLevel: 4),
            new ChatCommand("addkill", AccountSecurity.Administrator, "Syntax: .honor addkill\nGive yourself the honor of killing the selected creature (a civilian, a racial leader).", AddKill, RetailLevel: 4),
            new ChatCommand("show", AccountSecurity.Moderator, "Syntax: .honor show\nShow the honor statistics of the selected player (or yourself).", Show, RetailLevel: 2),
            new ChatCommand("setrp", AccountSecurity.Administrator, "Syntax: .honor setrp #rankpoints\nSet the rank points of the selected player (or yourself).", SetRankPoints, RetailLevel: 4),
            new ChatCommand("reset", AccountSecurity.Administrator, "Syntax: .honor reset\nForget all honor of the selected player (or yourself).", Reset, RetailLevel: 4),
        ], RetailLevel: 3),
    ];

    private static HonorFeature? Feature(CommandContext context) => context.Session.Services.GetService<HonorFeature>();

    private static bool TryHonor(CommandContext context, out HonorService service)
    {
        service = null!;
        if (Feature(context)?.ActiveService is not { } found)
        {
            context.Reply(HonorUnavailable);
            return false;
        }

        service = found;
        return true;
    }

    // vmangos GetSelectedPlayer: the selected online player, the issuer when nothing is selected, null for any other selection.
    private static Player? Selected(CommandContext context)
    {
        Player? target = context.SelectedPlayerOrSelf();
        if (target is null)
        {
            context.Reply(GmStrings.PlayerNotFound);
        }

        return target;
    }

    private static bool Add(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        if (Selected(context) is not { } target || !TryHonor(context, out HonorService honor))
        {
            return true;
        }

        if (!context.CanActOn(target))
        {
            return true;
        }

        // (float)atof: a non-number is 0, which HonorMgr::Add ignores.
        honor.Add(target, AtoF(args), HonorKind.Other, null);
        return true;
    }

    // vmangos: the selected UNIT (the issuer when nothing is selected; selecting yourself shows the usage line) is "killed" through
    // Player::RewardHonor, which only reacts to civilians below gray (dishonor) and racial leaders (488 honor); a player target does nothing.
    private static bool AddKill(CommandContext context, string args)
    {
        Player self = context.Player;
        Unit? target = self.Selection.IsEmpty ? self : self.Map?.Combat.FindUnit(self.Selection);
        if (target is null)
        {
            context.Reply(GmStrings.PlayerNotFound);
            return true;
        }

        if (ReferenceEquals(target, self))
        {
            return false;
        }

        if (!TryHonor(context, out _))
        {
            return true;
        }

        if (target is Creature creature && Feature(context)?.Rewards is { } rewards)
        {
            rewards.RewardHonor(self, creature);
        }

        return true;
    }

    private static bool Show(CommandContext context, string args)
    {
        if (Selected(context) is not { } target || !TryHonor(context, out HonorService honor))
        {
            return true;
        }

        HonorState? state = honor.For(target);
        HonorRankInfo rank = state?.Rank ?? HonorRanks.None;
        HonorRankInfo highest = state?.HighestRank ?? HonorRanks.None;
        string[] names = target.Team == Team.Alliance ? AllianceRanks : HordeRanks;
        uint u(int field) => target.GetUInt32(field);

        context.Reply(string.Create(CultureInfo.InvariantCulture, $"Player: {target.Name} - {names[rank.Rank]} (Rank {rank.VisualRank})"));
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"Today: [Honorable Kills: |c0000ff00{target.GetUInt16(UpdateFields.PlayerFieldSessionKills, 0)}|r] [Dishonorable Kills: |c00ff0000{target.GetUInt16(UpdateFields.PlayerFieldSessionKills, 1)}|r]"));
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"Yesterday: [Kills: |c0000ff00{u(UpdateFields.PlayerFieldYesterdayKills)}|r] [Honor: {u(UpdateFields.PlayerFieldYesterdayContribution)}]"));
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"This Week: [Kills: |c0000ff00{u(UpdateFields.PlayerFieldThisWeekKills)}|r] [Honor: {u(UpdateFields.PlayerFieldThisWeekContribution)}]"));
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"Last Week: [Kills: |c0000ff00{u(UpdateFields.PlayerFieldLastWeekKills)}|r] [Honor: {u(UpdateFields.PlayerFieldLastWeekContribution)}] [Standing: {u(UpdateFields.PlayerFieldLastWeekRank)}]"));
        context.Reply(string.Create(CultureInfo.InvariantCulture,
            $"Life Time: [Rank Points: |c0000ff00{(state?.RankPoints ?? 0f).ToString("F6", CultureInfo.InvariantCulture)}|r] [Honorable Kills: |c0000ff00{u(UpdateFields.PlayerFieldLifetimeHonorbaleKills)}|r] [Dishonorable Kills: |c00ff0000{u(UpdateFields.PlayerFieldLifetimeDishonorbaleKills)}|r] [Highest Rank {highest.VisualRank}: {names[highest.Rank]}]"));
        return true;
    }

    private static bool SetRankPoints(CommandContext context, string args)
    {
        if (Selected(context) is not { } target || !TryHonor(context, out HonorService honor))
        {
            return true;
        }

        if (!new CommandArgs(args).ExtractFloat(out float value))
        {
            return false;
        }

        honor.SetRankPoints(target, value);
        context.Reply($"You have changed rank points of {target.Name} to {FormatG(value)}.");
        return true;
    }

    private static bool Reset(CommandContext context, string args)
    {
        if (Selected(context) is { } target && TryHonor(context, out HonorService honor))
        {
            honor.Reset(target);
        }

        return true;
    }

    /// <summary>
    /// vmangos HandleModifyHonorCommand (CharacterCommands.cpp:2468-2536): writes the honor tab update FIELD directly, as a visual hack
    /// (the next honor update recomputes it). The field word is matched with hasStringAbbr(field, name): the typed word must START with
    /// the full field name. An unknown field changes nothing but still prints the confirmation, like vmangos.
    /// </summary>
    internal static bool ModifyHonor(CommandContext context, string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        if (Selected(context) is not { } target)
        {
            return true;
        }

        var args = new CommandArgs(text);
        if (args.ExtractLiteral() is not { } field || !args.ExtractInt32(out int amount))
        {
            return false;
        }

        bool Is(string name) => field.StartsWith(name, StringComparison.OrdinalIgnoreCase);
        if (Is("points"))
        {
            if (amount is < 0 or > 255)
            {
                return false;
            }

            target.SetByte(UpdateFields.PlayerFieldBytes2, 0, (byte)amount);
        }
        else if (Is("rank"))
        {
            if (amount < 0 || amount >= HonorRanks.RankCount)
            {
                return false;
            }

            target.SetByte(UpdateFields.PlayerBytes3, 3, (byte)amount);
        }
        else if (Is("todaykills"))
        {
            target.SetUInt16(UpdateFields.PlayerFieldSessionKills, 0, (ushort)(uint)amount);
        }
        else if (Is("yesterdaykills"))
        {
            target.SetUInt32(UpdateFields.PlayerFieldYesterdayKills, (uint)amount);
        }
        else if (Is("yesterdayhonor"))
        {
            target.SetUInt32(UpdateFields.PlayerFieldYesterdayContribution, (uint)amount);
        }
        else if (Is("thisweekkills"))
        {
            target.SetUInt32(UpdateFields.PlayerFieldThisWeekKills, (uint)amount);
        }
        else if (Is("thisweekhonor"))
        {
            target.SetUInt32(UpdateFields.PlayerFieldThisWeekContribution, (uint)amount);
        }
        else if (Is("lastweekkills"))
        {
            target.SetUInt32(UpdateFields.PlayerFieldLastWeekKills, (uint)amount);
        }
        else if (Is("lastweekhonor"))
        {
            target.SetUInt32(UpdateFields.PlayerFieldLastWeekContribution, (uint)amount);
        }
        else if (Is("lastweekstanding"))
        {
            target.SetUInt32(UpdateFields.PlayerFieldLastWeekRank, (uint)amount);
        }
        else if (Is("lifetimedishonorablekills"))
        {
            target.SetUInt32(UpdateFields.PlayerFieldLifetimeDishonorbaleKills, (uint)amount);
        }
        else if (Is("lifetimehonorablekills"))
        {
            target.SetUInt32(UpdateFields.PlayerFieldLifetimeHonorbaleKills, (uint)amount);
        }

        // LANG_COMMAND_MODIFY_HONOR (299): "The %s field of %s was set to %u"; the rank prints as the signed number it was typed as.
        context.Reply(string.Create(CultureInfo.InvariantCulture, $"The {field} field of {target.Name} was set to {(Is("rank") ? amount.ToString(CultureInfo.InvariantCulture) : ((uint)amount).ToString(CultureInfo.InvariantCulture))}"));
        return true;
    }

    // C atof: optional sign, digits, an optional fraction and exponent; anything else is 0.
    private static float AtoF(string text)
    {
        string trimmed = text.TrimStart();
        int end = 0;
        while (end < trimmed.Length && (char.IsAsciiDigit(trimmed[end]) || trimmed[end] is '.' or '-' or '+' or 'e' or 'E'))
        {
            end++;
        }

        for (; end > 0; end--)
        {
            if (float.TryParse(trimmed.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value))
            {
                return value;
            }
        }

        return 0f;
    }

    // C printf %g: six significant digits, no trailing zeros, e+NN exponents.
    private static string FormatG(float value)
        => value.ToString("G6", CultureInfo.InvariantCulture).Replace("E+", "e+", StringComparison.Ordinal).Replace("E-", "e-", StringComparison.Ordinal);
}

/// <summary><c>.modify honor</c> (vmangos Chat.cpp:596, SEC_BASIC_ADMIN) under the built-in <c>.modify</c> root.</summary>
public sealed class HonorModifyExtension : ICommandExtension
{
    public string Path => "modify";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("honor", AccountSecurity.Administrator,
            "Syntax: .modify honor $field #value\nFields: points rank todaykills yesterdaykills yesterdayhonor thisweekkills thisweekhonor lastweekkills lastweekhonor lastweekstanding lifetimedishonorablekills lifetimehonorablekills.",
            HonorCommands.ModifyHonor, RetailLevel: 4),
    ];
}
