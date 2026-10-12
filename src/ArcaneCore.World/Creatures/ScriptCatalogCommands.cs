using System.Globalization;
using System.Text;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Creatures;

/// <summary>
/// <c>.debug script &lt;entry&gt; [map]</c> and <c>.debug scriptcoverage [map]</c>: what <see cref="ScriptCatalog"/> answers for a creature entry
/// and how many of a map's (or every dungeon's) ScriptNames and AINames have an implementation. They have no vmangos equivalent. Both work
/// from the server console (with an explicit map); neither changes anything. Run on the world thread.
/// </summary>
public sealed class ScriptCatalogCommands : ICommandExtension
{
    /// <summary>The longest unresolved list one reply prints; the rest is counted.</summary>
    private const int MaxListed = 40;

    public string Path => "debug";

    public IReadOnlyList<ChatCommand> Children { get; } =
    [
        new("script", AccountSecurity.GameMaster,
            "Syntax: .debug script $entry [$map]\nSay which AI the creature entry gets on the map (default: your map): the layer (map entry AI, dungeon boss AI, ScriptName, entry script, EventAI, SmartAI, built-in or default), the implementing type, and whether its ScriptName is implemented. From the console the map is required.",
            Script, RetailLevel: 3),
        new("scriptcoverage", AccountSecurity.GameMaster,
            "Syntax: .debug scriptcoverage [$map]\nCount the creatures spawned on the map (default: every dungeon) whose template names a ScriptName or an AIName, and how many ScriptNames have no implementation; the unresolved ones are listed.",
            Coverage, RetailLevel: 3),
    ];

    private static bool Script(CommandContext context, string args)
    {
        string[] words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length is < 1 or > 2 || !uint.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint entry))
        {
            return false;
        }

        uint mapId;
        if (words.Length == 2)
        {
            if (!uint.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out mapId)) return false;
        }
        else if (context.IsConsole)
        {
            return false;
        }
        else
        {
            mapId = context.Player.MapId;
        }

        if (Feature(context) is not { ContentInstalled: true } creatures)
        {
            context.Reply("The creature content is not loaded.");
            return true;
        }

        if (creatures.Content.FindTemplate(entry) is not { } template)
        {
            context.Reply(F($"Creature entry {entry} has no creature_template row."));
            return true;
        }

        ScriptResolution resolution = Catalog(creatures).Resolve(template, mapId, LiveSystem(context, mapId));
        context.Reply(F($"{template.Name} (entry {entry}) on map {mapId}: {resolution}"));
        context.Reply(F($"  ScriptName '{template.ScriptName}', AIName '{template.AIName}', creature_ai_scripts rows: {creatures.Content.Ai.GetEvents(entry).Count}."));
        return true;
    }

    private static bool Coverage(CommandContext context, string args)
    {
        string[] words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        uint? only = null;
        if (words.Length > 1) return false;
        if (words.Length == 1)
        {
            if (!uint.TryParse(words[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed)) return false;
            only = parsed;
        }

        if (Feature(context) is not { ContentInstalled: true } creatures)
        {
            context.Reply("The creature content is not loaded.");
            return true;
        }

        ScriptCatalog catalog = Catalog(creatures);
        uint[] maps = only is { } one
            ? [one]
            : [.. WorldMaps.Of(context.World).Registry.All.Where(m => m.MapType == MapType.Instance).Select(m => m.Entry)];
        var all = new List<ScriptCoverageRow>();
        foreach (uint map in maps)
        {
            IReadOnlyList<ScriptCoverageRow> rows = catalog.Coverage(map, LiveSystem(context, map));
            all.AddRange(rows);
            ScriptCoverageSummary summary = ScriptCoverageSummary.Of(rows);
            if (only is not null || summary.ScriptNames + summary.AiNames > 0)
            {
                context.Reply(Line(map, summary));
            }
        }

        context.Reply(maps.Length > 1
            ? "Total over " + F($"{maps.Length} maps: ") + Describe(ScriptCoverageSummary.Of(all))
            : Describe(ScriptCoverageSummary.Of(all)));
        ScriptCoverageRow[] unresolved = [.. all.Where(r => r.ScriptName.Length > 0 && !r.Resolution.ScriptNameResolved)];
        foreach (ScriptCoverageRow row in unresolved.Take(MaxListed))
        {
            context.Reply(F($"  unresolved: map {row.MapId} entry {row.Entry} {row.Name}: {row.Resolution}"));
        }

        if (unresolved.Length > MaxListed)
        {
            context.Reply(F($"  ... and {unresolved.Length - MaxListed} more."));
        }

        return true;
    }

    private static string Line(uint map, ScriptCoverageSummary s) => F($"map {map}: ") + Describe(s);

    private static string Describe(ScriptCoverageSummary s)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{s.ScriptNames} ScriptNames: {s.ScriptNamesResolved} implemented, ")
            .Append(CultureInfo.InvariantCulture, $"{s.ScriptNamesMaybeInstanceRegistered} possibly registered by the instance script, ")
            .Append(CultureInfo.InvariantCulture, $"{s.ScriptNamesMissing} with no implementation; {s.AiNames} AINames ({s.AiNamesUnknown} unknown).");
        return text.ToString();
    }

    private static CreatureWorldFeature? Feature(CommandContext context) => context.Session.Services.GetService<CreatureWorldFeature>();

    private static ScriptCatalog Catalog(CreatureWorldFeature creatures) => new(creatures.AiServices.Factory, creatures.Content);

    /// <summary>The invoker's own map's creature system when it is <paramref name="mapId"/> (the live registrations of its instance script), else null.</summary>
    private static CreatureMapSystem? LiveSystem(CommandContext context, uint mapId)
        => !context.IsConsole && context.Player.Map is { } map && map.MapId == mapId ? map.FindUpdater<CreatureMapSystem>() : null;

    private static string F(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
