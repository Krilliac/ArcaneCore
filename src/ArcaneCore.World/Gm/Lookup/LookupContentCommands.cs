using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Skills;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Lookup;

/// <summary>Result texts of the lookup, list and guid commands (mangos_string ids from the MaNGOS Zero Language.h; the rest are ArcaneCore's).</summary>
public static class LookupContentText
{
    /// <summary>LANG_COMMAND_NOSKILLFOUND (444).</summary>
    public const string NoSkillsFound = "No skills found!";

    /// <summary>LANG_COMMAND_NOSPELLFOUND (445).</summary>
    public const string NoSpellsFound = "No spells found!";

    /// <summary>LANG_COMMAND_NOQUESTFOUND (446).</summary>
    public const string NoQuestsFound = "No quests found!";

    /// <summary>LANG_COMMAND_NOAREAFOUND (442).</summary>
    public const string NoAreasFound = "No area found!";

    /// <summary>LANG_COMMAND_NOTAXINODEFOUND (466).</summary>
    public const string NoTaxiNodesFound = "No taxinodes found!";

    /// <summary>ArcaneCore's: no mangos_string covers <c>.lookup map</c>.</summary>
    public const string NoMapsFound = "No maps found!";

    /// <summary>LANG_NO_SELECTION (200).</summary>
    public const string NoSelection = "No selection.";

    /// <summary>LANG_OBJECT_GUID (201): "Object GUID is: %s".</summary>
    public static string ObjectGuid(string guid) => $"Object GUID is: {guid}";

    /// <summary>ArcaneCore's header of <c>.list auras</c>.</summary>
    public static string AuraCount(int count) => string.Create(CultureInfo.InvariantCulture, $"Target has {count} aura(s):");

    /// <summary>LANG_SELECT_CHAR_OR_CREATURE (1): <c>.list auras</c> with a selection that is not a unit.</summary>
    public const string NoUnitSelected = "You should select a character or a creature.";

    /// <summary>Shared tail of the lookup commands: cap by <c>LookupMaxResults</c>, "none" text when empty.</summary>
    public static bool Send(CommandContext context, IEnumerable<string> lines, string none)
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

    /// <summary>A coordinate for a result line (two decimals, invariant).</summary>
    public static string Coord(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>"Name Rank", or just the name for an unranked spell.</summary>
    public static string SpellLabel(SpellInfo spell) => spell.Rank.Length == 0 ? spell.Name : spell.Name + " " + spell.Rank;
}

/// <summary>
/// <c>.lookup quest|skill|spell|area|map|taxinode $namepart</c>: the lookups the reference cores share (matrix: quest, skill and spell in
/// four cores, area and taxinode in three, map in two) over the content ArcaneCore loads: <c>quest_template.Title</c>, SkillLine.dbc names,
/// the <c>spell_template</c> name and rank, the area and map tables, TaxiNodes.dbc names. Same shape as <see cref="LookupCommands"/>:
/// case-insensitive substring, ordered by id, capped by <c>GmCommands:LookupMaxResults</c>. Data that is not loaded (no skill DBC, no
/// quest database) answers the "none found" line, like an empty match.
/// <c>.lookup itemset</c> is not provided: no item-set table is loaded; <c>.lookup player ...</c> is not provided: the world daemon keeps
/// no account, ip or email index of characters.
/// </summary>
public sealed class LookupContentExtension : ICommandExtension
{
    public string Path => "lookup";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new ChatCommand("quest", AccountSecurity.Moderator, "Syntax: .lookup quest $namepart\nLooks up a quest by title.", LookupQuest, RetailLevel: 2),
        new ChatCommand("skill", AccountSecurity.Moderator, "Syntax: .lookup skill $namepart\nLooks up a skill line by name.", LookupSkill, RetailLevel: 2),
        new ChatCommand("spell", AccountSecurity.Moderator, "Syntax: .lookup spell $namepart\nLooks up a spell by name (the rank is part of the link text).", LookupSpell, RetailLevel: 2),
        new ChatCommand("area", AccountSecurity.Moderator, "Syntax: .lookup area $namepart\nLooks up an area or zone by name.", LookupArea, RetailLevel: 2),
        new ChatCommand("map", AccountSecurity.Moderator, "Syntax: .lookup map $namepart\nLooks up a map by name.", LookupMap, RetailLevel: 2),
        new ChatCommand("taxinode", AccountSecurity.Moderator, "Syntax: .lookup taxinode $namepart\nLooks up a flight-path node by name.", LookupTaxiNode, RetailLevel: 2),
    ];

    private static bool LookupQuest(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        string needle = args.ToLowerInvariant();
        QuestStore quests = context.Session.Services.GetService<QuestNpcFeature>()?.Services.Quests ?? QuestStore.Empty;
        IEnumerable<string> lines = quests.All
            .Where(q => LookupText.Fits(q.Template.Title, needle))
            .OrderBy(q => q.Id)
            .Select(q => $"{q.Id} - |cffffffff|Hquest:{q.Id}:{q.Template.QuestLevel}|h[{q.Template.Title}]|h|r");
        return LookupContentText.Send(context, lines, LookupContentText.NoQuestsFound);
    }

    private static bool LookupSkill(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        string needle = args.ToLowerInvariant();
        SkillCatalog catalog = context.Session.Services.GetService<SkillsFeature>()?.Catalog ?? SkillCatalog.Empty;
        IEnumerable<string> lines = catalog.Lines
            .Where(l => LookupText.Fits(l.Name, needle))
            .OrderBy(l => l.Id)
            .Select(l => $"{l.Id} - |cffffffff|Hskill:{l.Id}|h[{l.Name}]|h|r");
        return LookupContentText.Send(context, lines, LookupContentText.NoSkillsFound);
    }

    private static bool LookupSpell(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        string needle = args.ToLowerInvariant();
        SpellStore store = context.Session.Services.GetService<SpellFeature>()?.System.Store ?? SpellStore.Empty;
        IEnumerable<string> lines = store.All
            .Where(s => LookupText.Fits(s.Name, needle))
            .OrderBy(s => s.Id)
            .Select(s => $"{s.Id} - |cffffffff|Hspell:{s.Id}|h[{LookupContentText.SpellLabel(s)}]|h|r");
        return LookupContentText.Send(context, lines, LookupContentText.NoSpellsFound);
    }

    private static bool LookupArea(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        string needle = args.ToLowerInvariant();
        IEnumerable<string> lines = WorldMaps.Of(context.World).Areas.All
            .Where(a => LookupText.Fits(a.Name, needle))
            .Select(a => string.Create(CultureInfo.InvariantCulture,
                $"{a.Entry} - |cffffffff|Harea:{a.Entry}|h[{a.Name}]|h|r (map {a.MapId}, {(a.IsZone ? "zone" : "in zone " + a.ZoneId)})"));
        return LookupContentText.Send(context, lines, LookupContentText.NoAreasFound);
    }

    private static bool LookupMap(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        string needle = args.ToLowerInvariant();
        IEnumerable<string> lines = WorldMaps.Of(context.World).Registry.All
            .Where(m => LookupText.Fits(m.Name, needle))
            .Select(m => string.Create(CultureInfo.InvariantCulture, $"{m.Entry} - |cffffffff|Hmap:{m.Entry}|h[{m.Name}]|h|r ({m.MapType})"));
        return LookupContentText.Send(context, lines, LookupContentText.NoMapsFound);
    }

    private static bool LookupTaxiNode(CommandContext context, string args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        string needle = args.ToLowerInvariant();
        NpcStore npcs = context.Session.Services.GetService<QuestNpcFeature>()?.Services.Npcs ?? NpcStore.Empty;
        IEnumerable<string> lines = npcs.Nodes
            .Where(n => LookupText.Fits(n.Name, needle))
            .Select(n => $"{n.Id} - |cffffffff|Htaxinode:{n.Id}|h[{n.Name}]|h|r");
        return LookupContentText.Send(context, lines, LookupContentText.NoTaxiNodesFound);
    }
}

/// <summary>
/// <c>.guid</c> (MaNGOS Zero, AzerothCore cs_misc.cpp:1250, TrinityCore; level 2 in all three): the GUID of the selected object.
/// </summary>
public sealed class GuidCommand : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("guid", AccountSecurity.Moderator, "Syntax: .guid\nShow the GUID of the selected object.", Guid, RetailLevel: 2),
    ];

    private static bool Guid(CommandContext context, string args)
    {
        ObjectGuid selection = context.Player.Selection;
        if (selection.IsEmpty)
        {
            context.Reply(LookupContentText.NoSelection);
            return true;
        }

        string detail = selection.HasEntry ? $"entry {selection.Entry}, counter {selection.Counter}" : $"low {selection.Low}";
        context.Reply(LookupContentText.ObjectGuid($"{selection} ({detail})"));
        return true;
    }
}

/// <summary>
/// <c>.list creature|object #entry [#max]</c> (MaNGOS Zero, AzerothCore and TrinityCore <c>list creature|object</c>; level 3 in MaNGOS Zero
/// and TrinityCore, 1 in AzerothCore): the database spawns of one template with their position, ordered by spawn id, at most
/// <c>#max</c> (default 10) lines. <c>.list auras</c> (level 3 in MaNGOS Zero and TrinityCore): the live auras of the selected unit or
/// yourself. The other <c>list</c> sub-commands of the cores are not provided (see docs/integration/gm-lookup-lane.md).
/// </summary>
public sealed class ListCommands : ICommandGroup
{
    private const int DefaultMax = 10;

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("list", AccountSecurity.GameMaster, "Syntax: .list $subcommand", Children:
        [
            new ChatCommand("creature", AccountSecurity.GameMaster, "Syntax: .list creature #creature_id [#max_count]\nOutput the database spawns of the creature template (default 10).", ListCreature, RetailLevel: 3),
            new ChatCommand("object", AccountSecurity.GameMaster, "Syntax: .list object #object_id [#max_count]\nOutput the database spawns of the gameobject template (default 10).", ListObject, RetailLevel: 3),
            new ChatCommand("auras", AccountSecurity.GameMaster, "Syntax: .list auras\nList the auras of the selected unit, or yours.", ListAuras, RetailLevel: 3),
        ], RetailLevel: 3),
    ];

    /// <summary>"#id [#max]"; a max below 1 is refused like a bad number.</summary>
    private static bool TryArgs(string args, out uint entry, out int max)
    {
        entry = 0;
        max = DefaultMax;
        string[] words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length is 1 or 2
            && uint.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out entry)
            && (words.Length == 1 || (int.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out max) && max > 0));
    }

    private static bool ListCreature(CommandContext context, string args)
    {
        if (!TryArgs(args, out uint entry, out int max))
        {
            return false;
        }

        var feature = context.Session.Services.GetRequiredService<CreatureWorldFeature>();
        string name = feature.Content.FindTemplate(entry)?.Name ?? string.Empty;
        IEnumerable<string> lines = feature.Content.MapsWithSpawns
            .SelectMany(feature.Content.GetSpawns)
            .Where(s => s.Entry == entry)
            .OrderBy(s => s.Guid)
            .Take(max)
            .Select(s => $"{s.Guid} - |cffffffff|Hcreature:{s.Guid}|h[{name} X:{LookupContentText.Coord(s.X)} Y:{LookupContentText.Coord(s.Y)} Z:{LookupContentText.Coord(s.Z)} MapId:{s.MapId}]|h|r");
        return LookupContentText.Send(context, lines, GmStrings.NoCreaturesFound);
    }

    private static bool ListObject(CommandContext context, string args)
    {
        if (!TryArgs(args, out uint entry, out int max))
        {
            return false;
        }

        var feature = context.Session.Services.GetRequiredService<GameObjectLootFeature>();
        string name = feature.Content.Templates.FirstOrDefault(t => t.Entry == entry)?.Name ?? string.Empty;
        IEnumerable<string> lines = feature.Content.Spawns
            .Where(s => s.Entry == entry)
            .OrderBy(s => s.Guid)
            .Take(max)
            .Select(s => $"{s.Guid} - |cffffffff|Hgameobject:{s.Guid}|h[{name} X:{LookupContentText.Coord(s.X)} Y:{LookupContentText.Coord(s.Y)} Z:{LookupContentText.Coord(s.Z)} MapId:{s.MapId}]|h|r");
        return LookupContentText.Send(context, lines, GmStrings.NoGameObjectsFound);
    }

    private static bool ListAuras(CommandContext context, string args)
    {
        ObjectGuid selection = context.Player.Selection;
        Unit? target = selection.IsEmpty ? context.Player : context.Player.Map?.FindObject(selection) as Unit;
        if (target is null || context.Session.Services.GetService<SpellFeature>() is not { } spells)
        {
            context.Reply(LookupContentText.NoUnitSelected);
            return true;
        }

        List<SpellAuraHolder> auras = [.. spells.System.GetAuras(target).Where(h => !h.IsRemoved).OrderBy(h => h.Spell.Id)];
        context.Reply(LookupContentText.AuraCount(auras.Count));
        return auras.Count == 0 || LookupContentText.Send(
            context,
            auras.Select(h => $"{h.Spell.Id} - |cffffffff|Hspell:{h.Spell.Id}|h[{LookupContentText.SpellLabel(h.Spell)}]|h|r x{h.StackAmount}"),
            string.Empty);
    }
}
