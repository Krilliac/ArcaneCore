using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Lookup;

/// <summary>Result-line helpers shared by the lookup commands.</summary>
public static class LookupText
{
    /// <summary>vmangos <c>Utf8FitTo</c>: the case-insensitive substring test (both sides lower-cased).</summary>
    public static bool Fits(string name, string lowerNeedle)
        => name.Length > 0 && name.ToLowerInvariant().Contains(lowerNeedle, StringComparison.Ordinal);

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
/// <c>.lookup item|spell|creature|object|tele</c> (vmangos LookupCommands.cpp; levels Chat.cpp:557-575:
/// the root MODERATOR, these sub-commands TICKETMASTER). Lines come out ordered by entry (vmangos
/// walks an unordered map). Locale-specific names are not used (ArcaneCore has one locale).
/// <c>.lookup itemset</c>, <c>quest</c>, <c>area</c>, <c>faction</c>, <c>skill</c>,
/// <c>taxinode</c>, <c>event</c>, <c>pool</c> and <c>player</c> are not provided (see the lane doc).
/// </summary>
public sealed class LookupCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("lookup", AccountSecurity.Moderator, "Syntax: .lookup $subcommand", Children:
        [
            new ChatCommand("item", AccountSecurity.Moderator, "Syntax: .lookup item $itemname\nLooks up an item by name.", LookupItem, RetailLevel: 2),
            new ChatCommand("spell", AccountSecurity.Moderator, "Syntax: .lookup spell $spellname\nLooks up a spell by name.", LookupSpell, RetailLevel: 2),
            new ChatCommand("creature", AccountSecurity.Moderator, "Syntax: .lookup creature $namepart\nLooks up a creature by name.", LookupCreature, RetailLevel: 2),
            new ChatCommand("object", AccountSecurity.Moderator, "Syntax: .lookup object $objname\nLooks up a gameobject by name.", LookupObject, RetailLevel: 2),
            new ChatCommand("tele", AccountSecurity.Moderator, "Syntax: .lookup tele $substring\nSearch and output all teleport locations containing $substring.", LookupTele, RetailLevel: 2),
        ], RetailLevel: 1),
    ];

    private static bool LookupItem(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        string needle = args.ToLowerInvariant();
        Player caller = context.Player;
        if (LiveItemTemplateStore.Unwrap(caller.Inventory.Templates) is not { } store)
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
                string usable = caller.Inventory.CanUseItem(t) == InventoryResult.Ok ? GmStrings.Usable : string.Empty;
                string link = $"|c{LookupText.QualityColor(t.Quality)}|Hitem:{t.Entry}:0:0:0:0:0:0:0|h[{t.Name}]|h|r";
                return $"{t.Entry} - {link} {usable}";
            });
        return Send(context, lines, GmStrings.NoItemsFound);
    }

    /// <summary>
    /// The classic <c>HandleLookupSpellCommand</c> walks Spell.dbc and applies the same
    /// case-insensitive substring test as item lookup (mangos-classic Level3.cpp:2633-2689;
    /// vmangos Commands/LookupCommands.cpp:445-492). ArcaneCore has one resolved locale, so the
    /// immutable SpellStore is the authoritative catalog. Results use the classic clickable
    /// spell link shape and are ordered by spell id before the shared result cap is applied.
    /// </summary>
    private static bool LookupSpell(CommandContext context, string args)
    {
        string needle = args.Trim().ToLowerInvariant();
        if (needle.Length == 0)
        {
            return false;
        }

        SpellFeature feature = context.Session.Services.GetRequiredService<SpellFeature>();
        IEnumerable<string> lines = feature.System.Store.All
            .Where(spell => LookupText.Fits(spell.Name, needle))
            .OrderBy(spell => spell.Id)
            .Select(spell => $"{spell.Id} - |cffffffff|Hspell:{spell.Id}|h[{spell.Name}]|h|r");
        return Send(context, lines, "No spells found!");
    }

    private static bool LookupCreature(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        string needle = args.ToLowerInvariant();
        var feature = context.Session.Services.GetRequiredService<CreatureWorldFeature>();
        IEnumerable<string> lines = feature.Content.Templates
            .Where(t => LookupText.Fits(t.Name, needle))
            .OrderBy(t => t.Entry)
            .Select(t => $"{t.Entry} - |cffffffff|Hcreature_entry:{t.Entry}|h[{t.Name}]|h|r ");
        return Send(context, lines, GmStrings.NoCreaturesFound);
    }

    private static bool LookupObject(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        string needle = args.ToLowerInvariant();
        var feature = context.Session.Services.GetRequiredService<GameObjectLootFeature>();
        IEnumerable<string> lines = feature.Content.Templates
            .Where(t => LookupText.Fits(t.Name, needle))
            .OrderBy(t => t.Entry)
            .Select(t => $"{t.Entry} - |cffffffff|Hgameobject_entry:{t.Entry}|h[{t.Name}]|h|r ");
        return Send(context, lines, GmStrings.NoGameObjectsFound);
    }

    private static bool LookupTele(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            return false;   // vmangos answers LANG_COMMAND_TELE_PARAMETER then the syntax; ours is the syntax text
        }

        string needle = args.ToLowerInvariant();
        TeleportFeature feature = context.Session.Services.GetRequiredService<TeleportFeature>();
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
        List<string> shown = LookupText.Limit(lines, context.Commands.Gm.LookupMaxResults, out bool omitted);
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
