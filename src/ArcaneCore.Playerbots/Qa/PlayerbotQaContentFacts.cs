using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>One spawn of a creature that ends a quest (turns it in): the destination of a completed quest's trip.</summary>
internal readonly record struct QaEnderFact(uint Entry, string Name, uint Map, float X, float Y, float Z);

/// <summary>The creature template facts the classifier uses.</summary>
internal readonly record struct QaTemplateFacts(uint Rank, byte MinLevel, byte MaxLevel, string ScriptName, bool Guard = false);

/// <summary>
/// What the loaded content says about the ids a finding mentions (read-only, any thread). <see cref="Available"/> is false while the content is
/// not loaded (the creature content is installed from the world thread after start-up): the classifier then makes no missing-data claim, because an
/// empty store would call every entry missing.
/// </summary>
internal interface IQaContentFacts
{
    bool Available { get; }

    string? QuestTitle(uint quest);

    int? QuestLevel(uint quest);

    /// <summary>The creature entries the quest's kill/speak objectives name (the positive ReqCreatureOrGOId values).</summary>
    IReadOnlyList<uint> QuestObjectiveCreatures(uint quest);

    /// <summary>The creature entries that end the quest (its <c>creature_involvedrelation</c> rows), distinct; empty when none or unknown.</summary>
    IReadOnlyList<uint> QuestEnderEntries(uint quest);

    /// <summary>Every spawn, on any map, of <paramref name="entry"/> when it ends the quest (a <c>creature_involvedrelation</c> row); empty when it does not or the content is unknown.</summary>
    IReadOnlyList<QaEnderFact> QuestEnders(uint quest, uint entry);

    int SpawnCount(uint map, uint entry);

    int SpawnCountAnyMap(uint entry);

    /// <summary>The x/y of the spawns of <paramref name="entry"/> on <paramref name="map"/> (at most 64); empty when none or the content is unknown.</summary>
    IReadOnlyList<(float X, float Y)> SpawnPositions(uint map, uint entry);

    QaTemplateFacts? Template(uint entry);

    /// <summary>
    /// True when <paramref name="entry"/> has a <c>creature_movement_template</c> path <paramref name="pathId"/>, or when
    /// <paramref name="pathId"/> is 0 and the default <c>ResolveWaypointPath(0, entry)</c> has points. A shared <c>waypoint_path</c>
    /// id is not this entry's path.
    /// </summary>
    bool HasWaypointPath(uint entry, uint pathId);

    /// <summary>
    /// Whether the ScriptName of the creature <paramref name="entry"/> is implemented for <paramref name="map"/>, or cannot be judged without a
    /// live map: any layer of the script catalogue (named AI, entry script, <c>DungeonBossAis</c>, <c>InstanceScriptedAis</c>) answers yes.
    /// A template without a ScriptName is true.
    /// </summary>
    bool HasScript(uint entry, uint map);
}

/// <summary>No content: nothing is claimed.</summary>
internal sealed class NoQaContentFacts : IQaContentFacts
{
    public static NoQaContentFacts Instance { get; } = new();

    public bool Available => false;

    public string? QuestTitle(uint quest) => null;

    public int? QuestLevel(uint quest) => null;

    public IReadOnlyList<uint> QuestObjectiveCreatures(uint quest) => [];

    public IReadOnlyList<uint> QuestEnderEntries(uint quest) => [];

    public IReadOnlyList<QaEnderFact> QuestEnders(uint quest, uint entry) => [];

    public int SpawnCount(uint map, uint entry) => 0;

    public int SpawnCountAnyMap(uint entry) => 0;

    public IReadOnlyList<(float X, float Y)> SpawnPositions(uint map, uint entry) => [];

    public QaTemplateFacts? Template(uint entry) => null;

    public bool HasWaypointPath(uint entry, uint pathId) => false;

    public bool HasScript(uint entry, uint map) => false;
}

/// <summary>
/// The production facts: <see cref="CreatureWorldFeature.Content"/> (an immutable, volatile-swapped store that other threads may read),
/// <see cref="ArcaneCore.Game.Npc.QuestNpcServices.Quests"/> (a volatile read of the immutable quest store) and the creature AI factory's registrations (read-only
/// dictionaries after start-up). The features are looked up on every call, so content installed or reloaded later is seen. Thread-safe.
/// </summary>
internal sealed class PlayerbotQaContentFacts(IServiceProvider services) : IQaContentFacts
{
    private CreatureWorldFeature? Creatures => services.GetService<CreatureWorldFeature>();

    private QuestStore? Quests => services.GetService<QuestNpcFeature>()?.Services.Quests;

    public bool Available => Creatures is { ContentInstalled: true } creatures && creatures.Content.TemplateCount > 0;

    public string? QuestTitle(uint quest) => quest == 0 ? null : Quests?.Get(quest)?.Title;

    public int? QuestLevel(uint quest) => quest == 0 ? null : Quests?.Get(quest)?.QuestLevel;

    public IReadOnlyList<uint> QuestObjectiveCreatures(uint quest)
    {
        if (quest == 0 || Quests?.Get(quest) is not { } q) return [];
        return [.. q.ReqCreatureOrGOId.Where(id => id > 0).Select(id => (uint)id).Distinct()];
    }

    public IReadOnlyList<uint> QuestEnderEntries(uint quest)
        => quest == 0 || Quests is not { } quests ? [] : [.. quests.CreatureEndersOf(quest).Distinct()];

    public IReadOnlyList<QaEnderFact> QuestEnders(uint quest, uint entry)
    {
        if (quest == 0 || Quests is not { } quests || Creatures?.Content is not { } content || !quests.CreatureEndersOf(quest).Contains(entry)) return [];
        string name = content.FindTemplate(entry)?.Name ?? string.Empty;
        var found = new List<QaEnderFact>();
        foreach (uint map in content.MapsWithSpawns)
        {
            foreach (CreatureSpawn spawn in content.GetSpawns(map, entry))
            {
                if (found.Count >= MaxEnderSpawns) return found;
                found.Add(new QaEnderFact(entry, name, map, spawn.X, spawn.Y, spawn.Z));
            }
        }

        return found;
    }

    /// <summary>A quest has a handful of ender spawns; the cap bounds a pathological row.</summary>
    private const int MaxEnderSpawns = 64;

    public int SpawnCount(uint map, uint entry) => Creatures?.Content.GetSpawns(map, entry).Count ?? 0;

    public int SpawnCountAnyMap(uint entry)
    {
        if (Creatures?.Content is not { } content) return 0;
        int total = 0;
        foreach (uint map in content.MapsWithSpawns) total += content.GetSpawns(map, entry).Count;
        return total;
    }

    public IReadOnlyList<(float X, float Y)> SpawnPositions(uint map, uint entry)
    {
        if (Creatures?.Content is not { } content) return [];
        var found = new List<(float X, float Y)>();
        foreach (CreatureSpawn spawn in content.GetSpawns(map, entry))
        {
            if (found.Count >= MaxEnderSpawns) break;
            found.Add((spawn.X, spawn.Y));
        }

        return found;
    }

    public QaTemplateFacts? Template(uint entry)
        => Creatures?.Content.FindTemplate(entry) is { } t
            ? new QaTemplateFacts(t.Rank, t.MinLevel, t.MaxLevel, t.ScriptName,
                (t.Behaviour & CreatureBehaviourFlags.Guard) != 0) : null;

    public bool HasWaypointPath(uint entry, uint pathId)
    {
        if (Creatures?.Content is not { } content) return false;
        // RelayDataMissing with an entry is the "path N missing for entry E" warning. That lookup is this entry's
        // creature_movement_template path (path 0 is ResolveWaypointPath(0, entry)), not a shared cmangos waypoint_path.
        // GetWaypointPath stores those under entry 0 with WaypointPathBit, and path ids 0 and 1 exist globally, so treating
        // one as this creature's path files "a path is loaded now" for an entry that has none (#329 entry 5697, #278 entry 7052).
        return content.GetEntryWaypoints(entry, pathId).Count > 0
            || (pathId == 0 && content.ResolveWaypointPath(0, entry).Points.Count > 0);
    }

    public bool HasScript(uint entry, uint map)
        => Creatures is { } creatures && QaScriptFacts.Has(creatures.AiServices.Factory, creatures.Content, entry, map);
}

/// <summary>The script question of the classifier, answered by the script catalogue (every layer, for the signal's map).</summary>
internal static class QaScriptFacts
{
    /// <summary>
    /// False only when the creature's template names a ScriptName that no layer implements for <paramref name="map"/>
    /// (<see cref="ScriptResolution.ScriptNameMissing"/>). The older <c>CreatureAiFactory.HasScript</c> saw the
    /// world-wide registries only and called every per-map script missing.
    /// </summary>
    internal static bool Has(CreatureAiFactory factory, CreatureContent content, uint entry, uint map)
        => !new ScriptCatalog(factory, content).Resolve(entry, map).ScriptNameMissing;
}
