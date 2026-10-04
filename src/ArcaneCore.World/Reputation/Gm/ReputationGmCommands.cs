using System.Globalization;
using System.Text;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Reputation.Gm;

/// <summary>
/// The reply texts and the shared helpers of the reputation GM commands. Texts are the English rows of the vmangos
/// <c>mangos_string</c> table as the cmangos classic-db dump ships them (ids 305-327,
/// D:\refs\classic-db\Full_DB\ClassicDB_1_12_1_z2815.sql.gz); localisation is not provided (a documented limit). Every
/// command answers with an explicit line, never silently, when no Faction.dbc is loaded.
/// </summary>
internal static class ReputationGmText
{
    /// <summary>LANG_COMMAND_MODIFY_REP (305): "Faction %s (%u) reputation of %s was set to %5d!".</summary>
    public static string ModifyRep(string faction, uint id, string link, int reputation)
        => string.Create(CultureInfo.InvariantCulture, $"Faction {faction} ({id}) reputation of {link} was set to {reputation,5}!");

    public const string FactionNotFound = "No faction found!";

    public static string FactionUnknown(uint id) => string.Create(CultureInfo.InvariantCulture, $"Faction {id} unknown!");

    public static string InvalidParameter(string text) => $"Invalid parameter {text}";

    public static string DeltaRange(int max) => string.Create(CultureInfo.InvariantCulture, $"delta must be between 0 and {max} (inclusive)");

    /// <summary>LANG_COMMAND_FACTION_NOREP_ERROR (326), text as shipped: "Faction %s (%u) can'not have reputation.".</summary>
    public static string NoReputation(string faction, uint id) => string.Create(CultureInfo.InvariantCulture, $"Faction {faction} ({id}) can'not have reputation.");

    public const string NoReputationSuffix = " [no reputation]";

    public const string NotLoaded = "Reputation is not available: no Faction.dbc is loaded (set Reputation:FactionDbcPath).";

    public static string NotChanged(string link) => $"The reputation of {link} could not be changed now (the standings are not loaded or a quest reward is pending).";

    public static ReputationService? ServiceOf(CommandContext context)
    {
        ReputationService service = context.Session.Services.GetRequiredService<ReputationFeature>().Service;
        if (service.Factions.Count > 0)
        {
            return service;
        }

        context.Reply(NotLoaded);
        return null;
    }

    /// <summary>
    /// ChatHandler::ShowFactionListHelper (LookupCommands.cpp:1384-1418): "id - [name enUS] rank (value) [flags]" with the
    /// player's standing, or "id - [name enUS] [no reputation]" without one.
    /// </summary>
    public static string FactionLine(ReputationService service, FactionRecord faction, Player? target)
    {
        var line = new StringBuilder();
        line.Append(CultureInfo.InvariantCulture, $"{faction.Id} - |cffffffff|Hfaction:{faction.Id}|h[{faction.Name} enUS]|h|r");
        if (target is null)
        {
            return line.ToString();
        }

        if (service.For(target)?.State(faction) is { } state)
        {
            ReputationRank rank = service.GetRank(target, faction.Id);
            line.Append(CultureInfo.InvariantCulture, $" {ReputationRankNames.Name(rank)}|h|r ({service.GetReputation(target, faction.Id)})");
            FactionStateFlags flags = state.Flags;
            AppendIf(line, flags, FactionStateFlags.Visible, " [visible]");
            AppendIf(line, flags, FactionStateFlags.AtWar, " [at war]");
            AppendIf(line, flags, FactionStateFlags.PeaceForced, " [peace forced]");
            AppendIf(line, flags, FactionStateFlags.Hidden, " [hidden]");
            AppendIf(line, flags, FactionStateFlags.InvisibleForced, " [invisible forced]");
            AppendIf(line, flags, FactionStateFlags.Inactive, " [inactive]");
        }
        else
        {
            line.Append(NoReputationSuffix);
        }

        return line.ToString();
    }

    private static void AppendIf(StringBuilder line, FactionStateFlags flags, FactionStateFlags flag, string text)
    {
        if ((flags & flag) != 0)
        {
            line.Append(text);
        }
    }
}

/// <summary>
/// <c>.modify rep</c> (vmangos CharacterCommands.cpp:4330-4422, SEC_BASIC_ADMIN): sets the selected player's reputation with a
/// faction (a number or a <c>|Hfaction:|</c> link) to an absolute value or to the start of a rank plus an optional delta
/// (<c>.modify rep 72 honored 100</c>). It goes through <c>ReputationMgr::SetReputation(faction, amount)</c>, so the
/// spillover applies with the absolute value times the rate: a vmangos quirk kept on purpose.
/// </summary>
public sealed class ReputationModifyExtension : ICommandExtension
{
    public string Path => "modify";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("rep", AccountSecurity.Administrator,
            "Syntax: .modify rep #repId (#repvalue | $rankname [#delta])\nSets the reputation of the selected player with the faction to the value, or to the beginning of the rank plus the delta.",
            ModifyRep, RetailLevel: 4),
    ];

    private static bool ModifyRep(CommandContext context, string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        if (context.SelectedPlayerOrSelf() is not { } target)
        {
            context.Reply(GmStrings.PlayerNotFound);
            return true;
        }

        if (!context.CanActOn(target))
        {
            return true;
        }

        var args = new CommandArgs(text);
        if (!args.ExtractUInt32KeyFromLink("Hfaction", out uint factionId) || factionId == 0)
        {
            return false;
        }

        int amount = 0;
        if (!args.ExtractInt32(out amount))
        {
            string? rankText = args.ExtractLiteral();
            if (rankText is null)
            {
                return false;
            }

            // CharacterCommands.cpp:4357-4395: the first rank whose name starts with the text, then the optional delta.
            if (!ReputationRankNames.TryParsePrefix(rankText, out ReputationRank rank))
            {
                context.Reply(ReputationGmText.InvalidParameter(rankText));
                return true;
            }

            amount = ReputationMath.Bottom;
            for (int r = 0; r < (int)rank; r++)
            {
                amount += ReputationMath.PointsInRank[r];
            }

            int points = ReputationMath.PointsInRank[(int)rank];
            if (!args.ExtractOptInt32(out int delta, 0) || delta < 0 || delta > points - 1)
            {
                context.Reply(ReputationGmText.DeltaRange(points - 1));
                return true;
            }

            amount += delta;
        }

        if (ReputationGmText.ServiceOf(context) is not { } service)
        {
            return true;
        }

        if (service.Factions.Find(factionId) is not { } faction)
        {
            context.Reply(ReputationGmText.FactionUnknown(factionId));
            return true;
        }

        if (!faction.CanHaveReputation)
        {
            context.Reply(ReputationGmText.NoReputation(faction.Name, factionId));
            return true;
        }

        string link = GmStrings.PlayerLink(target.Name);
        context.Reply(service.SetReputation(target, factionId, amount)
            ? ReputationGmText.ModifyRep(faction.Name, factionId, link, service.GetReputation(target, factionId))
            : ReputationGmText.NotChanged(link));
        return true;
    }
}

/// <summary>
/// <c>.lookup faction $namepart</c> (vmangos LookupCommands.cpp:1420-1478, SEC_TICKETMASTER): every faction whose name contains the
/// text, with the selected player's standing and flags.
/// </summary>
public sealed class ReputationLookupExtension : ICommandExtension
{
    public string Path => "lookup";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("faction", AccountSecurity.Moderator,
            "Syntax: .lookup faction $name\nAttempts to find the ID of the faction with the provided $name (a substring, case-insensitive), with the standing of the selected player.",
            LookupFaction, RetailLevel: 2),
    ];

    private static bool LookupFaction(CommandContext context, string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        if (ReputationGmText.ServiceOf(context) is not { } service)
        {
            return true;
        }

        Player? target = context.SelectedPlayerOrSelf();
        string needle = text.ToLowerInvariant();
        int counter = 0;
        foreach (FactionRecord faction in service.Factions.All)
        {
            if (faction.Name.Length == 0 || !faction.Name.ToLowerInvariant().Contains(needle, StringComparison.Ordinal))
            {
                continue;
            }

            context.Reply(ReputationGmText.FactionLine(service, faction, target));
            counter++;
        }

        if (counter == 0)
        {
            context.Reply(ReputationGmText.FactionNotFound);
        }

        return true;
    }
}

/// <summary>
/// <c>.character reputation [$name]</c> (vmangos CharacterCommands.cpp:1955-1969, SEC_TICKETMASTER): the standing and flags of every
/// faction of the player, in reputation-list order. The <c>character</c> root is this one in the codebase so far.
/// </summary>
public sealed class ReputationCharacterCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("character", AccountSecurity.Moderator, "Syntax: .character $subcommand", Children:
        [
            new ChatCommand("reputation", AccountSecurity.Moderator,
                "Syntax: .character reputation [$player_name]\nShows the reputation of the selected player or of the named online player.",
                ShowReputation, RetailLevel: 2),
        ], RetailLevel: 2),
    ];

    private static bool ShowReputation(CommandContext context, string text)
    {
        if (!GmTargets.TryPlayer(context, new CommandArgs(text), out Player target))
        {
            return true;
        }

        if (ReputationGmText.ServiceOf(context) is not { } service)
        {
            return true;
        }

        if (service.For(target) is not { } reputation)
        {
            context.Reply(ReputationGmText.NotChanged(GmStrings.PlayerLink(target.Name)));
            return true;
        }

        foreach (FactionState state in reputation.States)
        {
            context.Reply(ReputationGmText.FactionLine(service, state.Faction, target));
        }

        return true;
    }
}
