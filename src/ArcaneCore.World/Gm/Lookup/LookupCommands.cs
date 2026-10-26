using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Lookup;

/// <summary>Result-line helpers shared by the lookup commands.</summary>
public static class LookupText
{
    /// <summary>vmangos <c>Utf8FitTo</c>: the case-insensitive substring test (both sides lower-cased).</summary>
    public static bool Fits(string name, string lowerNeedle)
        => !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(lowerNeedle) && name.ToLowerInvariant().Contains(lowerNeedle, StringComparison.Ordinal);

    /// <summary>
    /// The first <paramref name="max"/> lines (all of them when <paramref name="max"/> is 0, the
    /// vmangos behaviour); <paramref name="omitted"/> says lines were left out.
    /// </summary>
    public static List<string> Limit(IEnumerable<string> lines, int max, out bool omitted)
    {
        var result = new List<string>();
        omitted = false;
        foreach (string line in lines)
        {
            if (max > 0 && result.Count >= max)
            {
                omitted = true;
                break;
            }

            result.Add(line);
        }

        return result;
    }

    /// <summary>vmangos ItemQualityColors (SharedDefines.h:205-213), the lower-case hex the link carries.</summary>
    public static string QualityColor(uint quality) => quality switch
    {
        0 => "ff9d9d9d",
        1 => "ffffffff",
        2 => "ff1eff00",
        3 => "ff0070dd",
        4 => "ffa335ee",
        5 => "ffff8000",
        _ => "ffe6cc80",
    };
}

/// <summary>
/// <c>.lookup item|creature|object|tele</c> (vmangos LookupCommands.cpp; levels Chat.cpp:557-575:
/// the root MODERATOR, these sub-commands TICKETMASTER). Lines come out ordered by entry (vmangos
/// walks an unordered map). Locale-specific names are not used (ArcaneCore has one locale).
/// <c>.lookup quest|skill|spell|area|map|taxinode</c> are <see cref="LookupContentExtension"/>'s; <c>itemset</c>, <c>faction</c>,
/// <c>event</c>, <c>pool</c> and <c>player</c> are not provided (see the lane doc).
/// </summary>
public sealed class LookupCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("lookup", AccountSecurity.Moderator, "Syntax: .lookup $subcommand", Children:
        [
            new ChatCommand("item", AccountSecurity.Moderator, "Syntax: .lookup item $itemname\nLooks up an item by name. From the console, the first use replies that item content is not loaded yet while it loads in the background; run the command again shortly.", LookupItem, RetailLevel: 2),
            new ChatCommand("creature", AccountSecurity.Moderator, "Syntax: .lookup creature $namepart\nLooks up a creature by name.", LookupCreature, RetailLevel: 2),
            new ChatCommand("object", AccountSecurity.Moderator, "Syntax: .lookup object $objname\nLooks up a gameobject by name.", LookupObject, RetailLevel: 2),
            new ChatCommand("tele", AccountSecurity.Moderator, "Syntax: .lookup tele $substring\nSearch and output all teleport locations containing $substring.", LookupTele, RetailLevel: 2),
        ], RetailLevel: 1),
    ];

    private static bool LookupItem(CommandContext context, string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            return false;
        }

        string needle = args.ToLowerInvariant();
        Player? caller = context.IsConsole ? null : context.Player;
        IItemTemplateStore? templates = caller?.Inventory.Templates;
        if (templates is null && context.Session.Services.GetService<ArcaneCore.World.Items.ItemsFeature>() is { } items)
        {
            if (!items.IsLoaded)
            {
                // Console commands run on the world thread: load on the pool and ask for a retry instead of waiting here.
                items.StartLoadInBackground();
                context.Reply("Item content is not loaded yet; loading now. Run the command again shortly.");
                return true;
            }

            templates = items.Templates;
        }

        if (templates is null || LiveItemTemplateStore.Unwrap(templates) is not { } store)
        {
            context.Reply(GmStrings.NoItemsFound);
            return true;
        }

        IEnumerable<string> lines = store.All
            .Where(t => LookupText.Fits(t.Name, needle))
            .OrderBy(t => t.Entry)
            .Select(t =>
            {
                // vmangos ShowItemListHelper: "%d - %s %s" with the link and the [usable] marker.
                string usable = caller is not null && caller.Inventory.CanUseItem(t) == InventoryResult.Ok ? GmStrings.Usable : string.Empty;
                string link = $"|c{LookupText.QualityColor(t.Quality)}|Hitem:{t.Entry}:0:0:0:0:0:0:0|h[{t.Name}]|h|r";
                return $"{t.Entry} - {link} {usable}";
            });
        return Send(context, lines, GmStrings.NoItemsFound);
    }

    private static bool LookupCreature(CommandContext context, string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            return false;
        }

        if (context.Session.Services.GetService<CreatureWorldFeature>() is not { } feature)
        {
            context.Reply(GmStrings.NoCreaturesFound);
            return true;
        }

        string needle = args.ToLowerInvariant();
        IEnumerable<string> lines = feature.Content.Templates
            .Where(t => LookupText.Fits(t.Name, needle))
            .OrderBy(t => t.Entry)
            .Select(t => $"{t.Entry} - |cffffffff|Hcreature_entry:{t.Entry}|h[{t.Name}]|h|r ");
        return Send(context, lines, GmStrings.NoCreaturesFound);
    }

    private static bool LookupObject(CommandContext context, string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            return false;
        }

        if (context.Session.Services.GetService<GameObjectLootFeature>() is not { } feature)
        {
            context.Reply(GmStrings.NoGameObjectsFound);
            return true;
        }

        string needle = args.ToLowerInvariant();
        IEnumerable<string> lines = feature.Content.Templates
            .Where(t => LookupText.Fits(t.Name, needle))
            .OrderBy(t => t.Entry)
            .Select(t => $"{t.Entry} - |cffffffff|Hgameobject_entry:{t.Entry}|h[{t.Name}]|h|r ");
        return Send(context, lines, GmStrings.NoGameObjectsFound);
    }

    private static bool LookupTele(CommandContext context, string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            return false;   // vmangos answers LANG_COMMAND_TELE_PARAMETER then the syntax; ours is the syntax text
        }

        if (context.Session.Services.GetService<TeleportFeature>() is not { } feature)
        {
            context.Reply(GmStrings.TeleNoLocation);
            return true;
        }

        string needle = args.ToLowerInvariant();
        List<string> lines = [.. feature.Maps.GameTeles
            .Where(t => LookupText.Fits(t.Name, needle))
            .OrderBy(t => t.Id)
            .Select(t => $"  |cffffffff|Htele:{t.Id}|h[{t.Name}]|h|r")];
        if (lines.Count == 0)
        {
            context.Reply(GmStrings.TeleNoLocation);
            return true;
        }

        context.Reply(GmStrings.TeleLocationsFound);
        return Send(context, lines, GmStrings.TeleNoLocation);
    }

    private static bool Send(CommandContext context, IEnumerable<string> lines, string none)
    {
        int maxResults = Math.Max(0, context.Commands.Gm.LookupMaxResults);
        List<string> shown = LookupText.Limit(lines, maxResults, out bool omitted);
        if (shown.Count == 0)
        {
            context.Reply(none);
            return true;
        }

        foreach (string line in shown)
        {
            context.Reply(line);
        }

        if (omitted)
        {
            context.Reply(GmStrings.LookupOmitted);
        }

        return true;
    }
}
