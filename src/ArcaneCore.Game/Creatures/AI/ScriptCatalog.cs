using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>Which layer of the AI selection gives a creature its AI (the order <see cref="ScriptCatalog.Resolve"/> tries them in).</summary>
public enum ScriptKind
{
    /// <summary>A script AI that an instance script registered for this map and entry (<c>CreatureMapSystem.RegisterEntryAi</c>); needs a live map.</summary>
    MapEntryAi,

    /// <summary>A boss AI of <see cref="DungeonBossAis"/> (map and entry).</summary>
    DungeonBossAi,

    /// <summary>A script AI the map's instance script gives the entry when it runs (<see cref="InstanceScriptedAis"/>: a <c>RegisterEntryAi</c> or a raid boss selection).</summary>
    InstanceScriptAi,

    /// <summary>An open-world boss of <c>WorldBossScripts</c> (<c>RaidBossAI.Create</c>'s map-independent fallback).</summary>
    WorldBossAi,

    /// <summary>A named AI registered with <see cref="CreatureAiFactory.Register"/> under the template's ScriptName.</summary>
    ScriptName,

    /// <summary>A world-wide entry script (<see cref="CreatureAiFactory.RegisterEntryScript"/>).</summary>
    EntryScript,

    /// <summary>
    /// A ScriptName implemented only as a gossip or quest script (<see cref="CreatureAiFactory.RegisterNpcScript"/>): it owns the creature's
    /// gossip menu or quest hooks, and the creature runs the AI of its AIName as usual.
    /// </summary>
    GossipScript,

    /// <inheritdoc cref="GossipScript"/>
    QuestScript,

    /// <summary>EventAI: the AIName is EventAI/GuardEventAI, or (with the implicit option) <c>creature_ai_scripts</c> rows exist.</summary>
    EventAI,

    /// <summary>SmartAI (<c>smart_scripts</c> rows).</summary>
    SmartAI,

    /// <summary>A built-in AI named by the AIName (NullAI, ReactorAI, GuardAI ...).</summary>
    BuiltInAi,

    /// <summary>The AIName names no registered AI; the creature gets the default AI.</summary>
    UnknownAiName,

    /// <summary>No script and no AIName: GuardAI, CritterAI, ReactorAI or AggressorAI.</summary>
    Default,
}

/// <summary>
/// The answer of <see cref="ScriptCatalog.Resolve(CreatureTemplate, uint, CreatureMapSystem?)"/>. <see cref="ScriptNameResolved"/> is false only when
/// the template has a ScriptName that no layer implements for the map (the creature then runs the AI of <see cref="Kind"/> instead).
/// </summary>
public readonly record struct ScriptResolution(ScriptKind Kind, string Implementation, string ScriptName, bool ScriptNameResolved)
{
    /// <summary>The template names a ScriptName that nothing implements on the map.</summary>
    public bool ScriptNameMissing => !ScriptNameResolved;

    public override string ToString() => $"{Kind} {Implementation}"
        + (ScriptName.Length == 0 ? string.Empty
            : ScriptNameResolved ? $" (ScriptName {ScriptName})"
            : $" (ScriptName {ScriptName}: NO IMPLEMENTATION)");
}

/// <summary>
/// One runtime answer to "which AI does creature entry X get on map M". It mirrors the selection order of
/// <c>CreatureMapSystem.CreateAi</c>: the map's own script AI (live registrations, then <see cref="DungeonBossAis"/>, then <see cref="InstanceScriptedAis"/>, then <c>WorldBossScripts</c>), the template's ScriptName
/// (named AI, then entry script, then a gossip/quest-only script), then the AIName, then the defaults. What an instance script (or a raid boss selection) gives an entry at run
/// time is read from <see cref="InstanceScriptedAis"/>. Read-only itself, but the factory registries it reads are not immutable: a hot-loaded script swap (CommitSwap) replaces entries on the world thread, so call it from the world thread
/// (a live <see cref="CreatureMapSystem"/> only from its map's thread).
/// </summary>
public sealed class ScriptCatalog(CreatureAiFactory factory, CreatureContent content, bool implicitEventAi = false)
{
    public ScriptResolution Resolve(uint entry, uint mapId, CreatureMapSystem? liveMap = null)
        => content.FindTemplate(entry) is { } template
            ? Resolve(template, mapId, liveMap)
            : new ScriptResolution(ScriptKind.Default, "(no creature_template)", string.Empty, true);

    public ScriptResolution Resolve(CreatureTemplate template, uint mapId, CreatureMapSystem? liveMap = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        string scriptName = template.ScriptName ?? string.Empty;
        uint entry = template.Entry;

        if (liveMap is not null && liveMap.HasEntryAi(entry))
        {
            string live = liveMap.Creatures.FirstOrDefault(c => c.Template.Entry == entry)?.AI?.GetType().Name ?? "(registered by an instance script)";
            return new ScriptResolution(ScriptKind.MapEntryAi, live, scriptName, true);
        }

        if (DungeonBossAis.Describe(mapId, entry) is { } boss)
        {
            return new ScriptResolution(ScriptKind.DungeonBossAi, boss, scriptName, true);
        }

        if (InstanceScriptedAis.Contains(mapId, entry))
        {
            return new ScriptResolution(ScriptKind.InstanceScriptAi, $"(instance script of map {mapId})", scriptName, true);
        }

        if (Array.IndexOf(Creatures.Scripts.WorldBosses.WorldBossScripts.Entries, entry) >= 0)
        {
            return new ScriptResolution(ScriptKind.WorldBossAi, "WorldBossScripts", scriptName, true);
        }

        if (scriptName.Length > 0 && factory.IsNamedAi(scriptName))
        {
            return new ScriptResolution(ScriptKind.ScriptName, scriptName, scriptName, true);
        }

        if (factory.EntryScriptName(entry) is { } entryScript)
        {
            return new ScriptResolution(ScriptKind.EntryScript, entryScript, scriptName, true);
        }

        if (scriptName.Length > 0 && factory.NpcScriptOf(scriptName) is { } npcScript)
        {
            return new ScriptResolution(npcScript.Kind, npcScript.Implementation, scriptName, true);
        }

        bool resolved = scriptName.Length == 0;
        (ScriptKind kind, string implementation) = Fallback(template);
        return new ScriptResolution(kind, implementation, scriptName, resolved);
    }

    private (ScriptKind Kind, string Implementation) Fallback(CreatureTemplate template)
    {
        string aiName = template.AIName ?? string.Empty;
        if (aiName.Length > 0)
        {
            if (!factory.IsNamedAi(aiName))
            {
                return (ScriptKind.UnknownAiName, aiName);
            }

            if (string.Equals(aiName, CreatureAiFactory.EventAIName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(aiName, CreatureAiFactory.GuardEventAIName, StringComparison.OrdinalIgnoreCase))
            {
                return (ScriptKind.EventAI, aiName);
            }

            return string.Equals(aiName, CreatureAiFactory.SmartAIName, StringComparison.OrdinalIgnoreCase)
                ? (ScriptKind.SmartAI, aiName)
                : (ScriptKind.BuiltInAi, aiName);
        }

        if ((template.Behaviour & CreatureBehaviourFlags.Guard) != 0)
        {
            return (ScriptKind.Default, "GuardAI");
        }

        if (implicitEventAi && content.Ai.GetEvents(template.Entry).Count > 0)
        {
            return (ScriptKind.EventAI, CreatureAiFactory.EventAIName);
        }

        if (template.CreatureType == CreatureAiFactory.CritterType)
        {
            return (ScriptKind.Default, "CritterAI");
        }

        // CreatureAiFactory.Create gives a spirit healer or guide without an AIName a NullCreatureAI before the civilian check. Only the
        // template's npcflags are known here; a spawn-level flag override is invisible to a template-only catalog.
        if ((template.NpcFlags & CreatureAiFactory.SpiritHealerNpcFlags) != 0)
        {
            return (ScriptKind.Default, nameof(NullCreatureAI));
        }

        return (ScriptKind.Default, template.Civilian ? "ReactorAI" : "AggressorAI");
    }

    /// <summary>
    /// The creatures spawned on <paramref name="mapId"/> (distinct entries, ordered by entry) whose template names a ScriptName or an AIName,
    /// each with what <see cref="Resolve(CreatureTemplate, uint, CreatureMapSystem?)"/> answers for it.
    /// </summary>
    public IReadOnlyList<ScriptCoverageRow> Coverage(uint mapId, CreatureMapSystem? liveMap = null)
    {
        var rows = new List<ScriptCoverageRow>();
        foreach (IGrouping<uint, CreatureSpawn> group in content.GetSpawns(mapId).GroupBy(s => s.Entry).OrderBy(g => g.Key))
        {
            if (content.FindTemplate(group.Key) is not { } template
                || (string.IsNullOrEmpty(template.ScriptName) && string.IsNullOrEmpty(template.AIName)))
            {
                continue;
            }

            rows.Add(new ScriptCoverageRow(mapId, template.Entry, template.Name, template.ScriptName ?? string.Empty,
                template.AIName ?? string.Empty, Resolve(template, mapId, liveMap), group.Count()));
        }

        return rows;
    }
}

/// <summary>One scripted-or-named creature entry of a map and how it resolves.</summary>
public sealed record ScriptCoverageRow(uint MapId, uint Entry, string Name, string ScriptName, string AIName, ScriptResolution Resolution, int Spawns);

/// <summary>Counts over <see cref="ScriptCoverageRow"/>s (entries, not spawns).</summary>
public readonly record struct ScriptCoverageSummary(
    int ScriptNames, int ScriptNamesResolved, int ScriptNamesMissing, int AiNames, int AiNamesUnknown)
{
    public static ScriptCoverageSummary Of(IEnumerable<ScriptCoverageRow> rows)
    {
        int names = 0, resolved = 0, missing = 0, ai = 0, unknown = 0;
        foreach (ScriptCoverageRow row in rows)
        {
            if (row.ScriptName.Length > 0)
            {
                names++;
                if (row.Resolution.ScriptNameResolved) resolved++;
                else missing++;
            }

            if (row.AIName.Length > 0)
            {
                ai++;
                if (row.Resolution.Kind == ScriptKind.UnknownAiName) unknown++;
            }
        }

        return new ScriptCoverageSummary(names, resolved, missing, ai, unknown);
    }
}
