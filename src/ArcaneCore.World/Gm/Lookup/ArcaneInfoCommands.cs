using System.Globalization;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Reload;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.GameObjects;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Reload;
using ArcaneCore.World.Skills;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Lookup;

/// <summary>
/// <c>.arcane</c>, ArcaneCore's own read-only introspection root (no reference core has it, so it carries no retail level and is gated by
/// <see cref="AccountSecurity.GameMaster"/>): <c>.arcane content</c> (how much of each content table is loaded, and the creature
/// definitions generation), <c>.arcane maps</c> (players, objects and in-transit objects of every running map) and <c>.arcane reloads</c>
/// (how each reloadable last ended). Nothing here changes server state; every figure is read on the world thread.
/// </summary>
public sealed class ArcaneInfoCommands : ICommandGroup
{
    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("arcane", AccountSecurity.GameMaster, "Syntax: .arcane $subcommand\nRead-only server internals: content, maps, reloads.", Children:
        [
            new ChatCommand("content", AccountSecurity.GameMaster, "Syntax: .arcane content\nShow how many rows of each content table are loaded.", Content),
            new ChatCommand("maps", AccountSecurity.GameMaster, "Syntax: .arcane maps\nShow the players, objects and in-transit objects of every running map.", Maps),
            new ChatCommand("reloads", AccountSecurity.GameMaster, "Syntax: .arcane reloads\nShow how each reloadable content table last ended, and the creature definitions generation.", Reloads),
        ]),
    ];

    private static string Line(string label, string value) => $"{label}: {value}";

    private static bool Content(CommandContext context, string args)
    {
        IServiceProvider services = context.Session.Services;
        CultureInfo c = CultureInfo.InvariantCulture;
        WorldMaps maps = WorldMaps.Of(context.World);
        context.Reply(Line("maps", string.Create(c, $"{maps.Registry.Count} maps, {maps.Areas.Count} areas, {maps.GameTeles.Count} tele locations, {maps.AreaTriggers.Count} area triggers")));
        context.Reply(Line("items", string.Create(c, $"{context.Player.Inventory.Templates.Count} templates")));
        if (services.GetService<CreatureWorldFeature>() is { } creatures)
        {
            context.Reply(Line("creatures", string.Create(c, $"{creatures.Content.TemplateCount} templates, {creatures.Content.SpawnCount} spawns, definitions generation {creatures.Content.DefinitionsVersion}")));
        }

        if (services.GetService<GameObjectLootFeature>() is { } objects)
        {
            context.Reply(Line("gameobjects", string.Create(c, $"{objects.Content.TemplateCount} templates, {objects.Content.SpawnCount} spawns")));
        }

        QuestNpcFeature? quests = services.GetService<QuestNpcFeature>();
        context.Reply(Line("quests", string.Create(c, $"{quests?.Services.Quests.Count ?? 0} templates")));
        context.Reply(Line("taxi nodes", string.Create(c, $"{quests?.Services.Npcs.Nodes.Count() ?? 0}")));
        context.Reply(Line("spells", string.Create(c, $"{services.GetService<SpellFeature>()?.System.Store.Count ?? 0} spells")));
        SkillsFeature? skills = services.GetService<SkillsFeature>();
        context.Reply(Line("skills", skills is { IsActive: true } ? string.Create(c, $"{skills.Catalog.LineCount} skill lines") : "not active"));
        return true;
    }

    /// <summary>
    /// The summary line is always sent; the per-instance lines go through <see cref="LookupContentText.Send"/> so that a server with
    /// hundreds of live dungeon or battleground instances answers at most <c>World:GmCommands:LookupMaxResults</c> lines plus the
    /// "omitted" line, like every other list, instead of one chat packet per instance.
    /// </summary>
    private static bool Maps(CommandContext context, string args)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        Map[] maps = [.. context.World.Maps.OrderBy(m => m.MapId).ThenBy(m => m.InstanceId)];
        context.Reply(string.Create(c, $"{maps.Length} map(s) running, {context.World.OnlinePlayerCount} player(s) online."));
        return maps.Length == 0 || LookupContentText.Send(context, maps.Select(map => MapLine(map, c)), string.Empty);
    }

    private static string MapLine(Map map, CultureInfo c)
    {
        string name = map.Template?.Name is { Length: > 0 } n ? n : "?";
        return string.Create(c, $"map {map.MapId} instance {map.InstanceId} [{name}]: {map.PlayerCount} player(s), {map.ObjectCount} object(s), {map.TransitCount} in transit");
    }

    private static bool Reloads(CommandContext context, string args)
    {
        if (context.Session.Services.GetService<CreatureWorldFeature>() is { } creatures)
        {
            context.Reply(string.Create(CultureInfo.InvariantCulture, $"creature definitions generation: {creatures.Content.DefinitionsVersion} (swaps since start)"));
        }

        if (context.Session.Services.GetService<ReloadFeature>() is not { Enabled: true } feature)
        {
            context.Reply("Live reload is disabled (HotReload:Commands is false).");
            return true;
        }

        foreach (string name in feature.Coordinator.Names)
        {
            context.Reply(feature.Coordinator.LastResults.TryGetValue(name, out ReloadResult? last)
                ? string.Create(CultureInfo.InvariantCulture, $"{name}: {last.Status} at {last.At:HH:mm:ss}Z")
                : $"{name}: not reloaded since start");
        }

        return true;
    }
}
