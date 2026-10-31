using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Playerbots.Qa;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// The classifier's "this creature's ScriptName has no implementation" claim is filed publicly, so it must see per-map scripts
/// (<c>DungeonBossAis</c>, the instance scripts' <c>RegisterEntryAi</c>), which the world-wide <c>CreatureAiFactory.HasScript</c> never did.
/// </summary>
public sealed class PlayerbotQaScriptFactsTests
{
    private const uint JandiceBarov = 10503;
    private const string JandiceScript = "boss_jandice_barov";
    private const uint Scholomance = 289;

    /// <summary>The real script question (what <c>PlayerbotQaContentFacts</c> asks) over scripted template/spawn facts.</summary>
    private sealed class CatalogFacts(CreatureAiFactory factory, CreatureContent content) : FakeFacts
    {
        public override bool HasScript(uint entry, uint map) => QaScriptFacts.Has(factory, content, entry, map);
    }

    private static CatalogFacts Facts(uint entry, string scriptName, uint map)
    {
        var template = new CreatureTemplate { Entry = entry, Name = "Boss", ScriptName = scriptName };
        var facts = new CatalogFacts(new CreatureAiFactory(), new CreatureContent([template], [], [], [], []));
        facts.Objectives[1656] = [entry];
        facts.Templates[entry] = new QaTemplateFacts(1, 60, 60, scriptName);
        facts.Spawns[(map, entry)] = 1;
        return facts;
    }

    private static QaSignal Stall(uint entry, uint map) => QaData.Stall(QaData.Bot1, -5608.8f, -439.4f, 1656, entry, map);

    [Fact]
    public void APerMapBossAi_IsNotCalledAMissingScript()
    {
        // The world-wide registries do not hold the Scholomance boss AI: the legacy question answers "missing" for a script that exists.
        Assert.False(new CreatureAiFactory().HasScript(JandiceBarov, JandiceScript));

        CatalogFacts facts = Facts(JandiceBarov, JandiceScript, Scholomance);

        Assert.True(facts.HasScript(JandiceBarov, Scholomance));
        Assert.NotEqual(QaCause.MissingData, PlayerbotQaClassifier.Classify(Stall(JandiceBarov, Scholomance), facts).Cause);
    }

    [Fact]
    public void ABossAiOfAnotherMap_DoesNotCoverThisMap()
    {
        // (289, 10503) is scripted; the same entry on a map without an instance script has nothing: still a missing-data claim.
        CatalogFacts facts = Facts(JandiceBarov, JandiceScript, 0);

        Assert.False(facts.HasScript(JandiceBarov, 0));
        QaClassification c = PlayerbotQaClassifier.Classify(Stall(JandiceBarov, 0), facts);
        Assert.Equal(QaCause.MissingData, c.Cause);
        Assert.Contains(JandiceScript, c.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstanceScriptedEntry_IsNotCalledMissing_ButAnUnlistedOneOnTheSameMapIs()
    {
        // 646 is given its AI by the Deadmines instance script (InstanceScriptedAis); 99_999_001 is not.
        CatalogFacts scripted = Facts(646, "boss_mr_smite", 36);
        Assert.True(scripted.HasScript(646, 36));
        Assert.NotEqual(QaCause.MissingData, PlayerbotQaClassifier.Classify(Stall(646, 36), scripted).Cause);

        CatalogFacts unknown = Facts(99_999_001, "boss_nobody", 36);
        Assert.False(unknown.HasScript(99_999_001, 36));
        Assert.Equal(QaCause.MissingData, PlayerbotQaClassifier.Classify(Stall(99_999_001, 36), unknown).Cause);
    }

    [Fact]
    public void AKnownMissingEntry_IsStillReportedMissing()
    {
        CatalogFacts facts = Facts(16079, "npc_theldren_trigger", 230); // Blackrock Depths: no reference script for the Theldren trigger
        Assert.False(facts.HasScript(16079, 230));
        Assert.Equal(QaCause.MissingData, PlayerbotQaClassifier.Classify(Stall(16079, 230), facts).Cause);
    }
}
