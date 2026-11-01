using System.Globalization;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Instances.Scripts;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>Skipped (reported as Skipped, not Passed) unless ARCANECORE_TEST_WORLD_DB names an existing world database.</summary>
internal sealed class WorldFixtureFactAttribute : FactAttribute
{
    public WorldFixtureFactAttribute()
    {
        if (ScriptCoverageProbe.WorldDbPath() is null)
        {
            Skip = "ARCANECORE_TEST_WORLD_DB must name a world.db; the dungeon/raid script coverage did NOT run.";
        }
    }
}

/// <summary>
/// The script coverage of the dungeons and raids of the real world fixture (ARCANECORE_TEST_WORLD_DB, opened read-only and immutable, never
/// written). Every ScriptName a spawned creature of a dungeon (MapType 1) or raid (MapType 2) map carries must resolve through
/// <see cref="ScriptCatalog"/> (named AI, entry script, <c>DungeonBossAis</c>, <see cref="InstanceScriptedAis"/>), or be in <see cref="KnownMissing"/>.
/// A new gap fails the test; so does a closed one, until its row leaves the list. The instance-script layer is proven by running the instance.
/// </summary>
public sealed class ScriptCoverageTests(ITestOutputHelper output)
{
    /// <summary>
    /// (map, creature entry, ScriptName) rows no layer implements and whose instance script gives no script AI either (proven by running the
    /// instance: <see cref="EveryKnownMissingRow_GetsNoScriptAiFromItsInstanceScript"/>). The creature runs its AIName's AI (EventAI) or the default.
    /// Each is a content gap: a ScriptDev2 script that is not ported. Remove a row when its script lands.
    /// </summary>
    private static readonly (uint Map, uint Entry, string Script)[] KnownMissing =
    [
        (230, 16079, "npc_theldren_trigger"),
        // Doom'rel has his gossip (DoomrelGossip, an INpcGossipScript registered by DungeonEventFeature) but no AI; the catalogue answers for AIs.
        (230, 9039, "boss_doomrel"),
    ];

    [WorldFixtureFact]
    public void EveryDungeonAndRaidScriptName_ResolvesOrIsListedMissing()
    {
        string path = ScriptCoverageProbe.WorldDbPath()!;
        ScriptCoverageProbe.World world = ScriptCoverageProbe.Load(path);
        var catalog = new ScriptCatalog(new CreatureAiFactory(), world.Content);
        var all = new List<ScriptCoverageRow>();
        foreach (uint map in world.DungeonMaps.Concat(world.RaidMaps))
        {
            IReadOnlyList<ScriptCoverageRow> rows = catalog.Coverage(map);
            ScriptCoverageSummary s = ScriptCoverageSummary.Of(rows);
            all.AddRange(rows);
            if (s.ScriptNames > 0)
            {
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"map {map} ({(world.RaidMaps.Contains(map) ? "raid" : "dungeon")}): {s.ScriptNames} ScriptNames, {s.ScriptNamesResolved} implemented, {s.ScriptNamesMissing} missing"));
            }
        }

        ScriptCoverageSummary total = ScriptCoverageSummary.Of(all);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{world.DungeonMaps.Length} dungeon + {world.RaidMaps.Length} raid maps: {total.ScriptNames} ScriptNames, {total.ScriptNamesResolved} implemented, {total.ScriptNamesMissing} missing (known-missing list {KnownMissing.Length}); {total.AiNames} AINames"));
        Assert.True(total.ScriptNames > 0, "the fixture has no dungeon/raid ScriptNames: the coverage saw nothing");

        var actual = all.Where(r => r.ScriptName.Length > 0 && !r.Resolution.ScriptNameResolved).Select(r => (Map: r.MapId, Entry: r.Entry, Script: r.ScriptName)).ToHashSet();
        Assert.Empty(actual.Except(KnownMissing).OrderBy(t => t.Map).ThenBy(t => t.Entry)); // a new gap: implement it or list it
        Assert.Empty(KnownMissing.Except(actual).OrderBy(t => t.Map).ThenBy(t => t.Entry)); // a closed gap: remove it from the list
    }

    [Fact]
    public void EveryKnownMissingRow_GetsNoScriptAiFromItsInstanceScript()
    {
        foreach (IGrouping<uint, (uint Map, uint Entry, string Script)> map in KnownMissing.GroupBy(r => r.Map))
        {
            HashSet<uint> scripted = ScriptCoverageProbe.ScriptedEntries(map.Key, [.. map.Select(r => r.Entry)]);
            Assert.True(scripted.Count == 0, $"map {map.Key}: {string.Join(", ", scripted)} now get a script AI from the instance script: add to InstanceScriptedAis and remove from KnownMissing");
        }
    }

    [Fact]
    public void EveryInstanceScriptedAiPair_IsBehaviourallyScripted()
    {
        int pairs = 0;
        foreach (IGrouping<uint, (uint MapId, uint Entry)> map in InstanceScriptedAis.All.GroupBy(p => p.MapId))
        {
            uint[] entries = [.. map.Select(p => p.Entry)];
            HashSet<uint> scripted = ScriptCoverageProbe.ScriptedEntries(map.Key, entries);
            Assert.True(entries.All(scripted.Contains), $"map {map.Key}: {string.Join(", ", entries.Where(e => !scripted.Contains(e)))} get no script AI from the instance script");
            pairs += entries.Length;
        }

        output.WriteLine($"{pairs} (map, entry) pairs proven");
        Assert.True(pairs > 0);
    }
}
