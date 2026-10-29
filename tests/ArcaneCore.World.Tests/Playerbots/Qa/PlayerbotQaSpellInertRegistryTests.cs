using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Spells;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.ClientData;
using ArcaneCore.World.Tests.Spells.ContractSweep;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Qa.QaSpellKit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// The two explicit lists the spell gap analyzer honours: <see cref="DeliberatelyInertSpells"/> (no reference core implements the spell) and
/// <see cref="QaSpellConsumers"/> (ArcaneCore consumes it somewhere the registries cannot see). Neither changes behaviour, so these tests pin the data:
/// well-formed entries, consumers that still exist in the source tree, the analyzer reading both, and (with the client DBCs) that no entry is stale.
/// </summary>
public sealed class PlayerbotQaSpellInertRegistryTests
{
    private const uint Unlisted = 999_999;

    private static SpellSystem System(params SpellInfo[] spells)
    {
        var system = new SpellSystem(new SpellStore(spells, [], []), () => 0);
        new ScriptEffectModule().Register(system);
        return system;
    }

    private static SpellInfo DummyAura(uint id) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy));

    private static SpellInfo DummyEffect(uint id) => Spell(id, Effect(SpellEffectName.Dummy, 0));

    [Fact]
    public void TheInertRegistry_HasAReasonAndACitationForEveryEntry()
    {
        Assert.NotEmpty(DeliberatelyInertSpells.All);
        foreach (InertSpell entry in DeliberatelyInertSpells.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Why), $"spell {entry.SpellId}: no reason");
            Assert.False(string.IsNullOrWhiteSpace(entry.Citation), $"spell {entry.SpellId}: no citation");
            Assert.True(DeliberatelyInertSpells.TryGet(entry.SpellId, out InertSpell? found) && ReferenceEquals(found, entry), $"spell {entry.SpellId}: not indexed");
            Assert.Equal(entry.Unhandled is not null, entry.Reason == InertSpellReason.UnusedEffect);
        }
    }

    [Fact]
    public void ASpell_IsEitherDeliberatelyInert_OrConsumed_NeverBoth()
    {
        uint[] both = [.. DeliberatelyInertSpells.All.Select(e => e.SpellId).Where(id => QaSpellConsumers.TryGet(id, out _))];
        Assert.Empty(both);
    }

    [Fact]
    public void EveryConsumer_NamesAFileThatStillContainsItsNeedle()
    {
        string src = Path.Combine(RepositorySource.RequireRoot(), "src");
        Assert.NotEmpty(QaSpellConsumers.All);
        foreach (QaSpellConsumer consumer in QaSpellConsumers.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(consumer.Why), $"spell {consumer.SpellId}: no description");
            string path = Path.Combine(src, consumer.File);
            Assert.True(File.Exists(path), $"spell {consumer.SpellId}: the consumer {consumer.File} is not in the tree");
            Assert.True(File.ReadAllText(path).Contains(consumer.Needle, StringComparison.Ordinal),
                $"spell {consumer.SpellId}: {consumer.File} no longer contains '{consumer.Needle}'");
        }
    }

    [Fact]
    public void EveryEvadeKeepConsumer_IsInTheCreatureEvadeSet()
    {
        uint[] missing = [.. QaSpellConsumers.All.Where(c => c.Kind == QaSpellConsumerKind.EvadeKeep && !SpellSystemCreatureCaster.KeptOnEvade.Contains(c.SpellId))
            .Select(c => c.SpellId)];
        Assert.Empty(missing);
        Assert.Contains(QaSpellConsumers.All, c => c.Kind == QaSpellConsumerKind.EvadeKeep);
    }

    [Fact]
    public void TheBaseline_HasNoUnhandledLineForAListedSpell()
    {
        string baseline = Path.Combine(RepositorySource.RequireRoot(), "tests", "ArcaneCore.World.Tests", "Spells", "ContractSweep", "spell-contract-baseline.txt");
        HashSet<uint> listed = [.. DeliberatelyInertSpells.All.Select(e => e.SpellId), .. QaSpellConsumers.All.Select(c => c.SpellId)];
        string[] stale = [.. File.ReadLines(baseline)
            .Where(l => l.StartsWith("a.unhandled", StringComparison.Ordinal))
            .Where(l => uint.TryParse(l.Split('\t')[1], out uint id) && listed.Contains(id))];
        Assert.Empty(stale);
    }

    [Fact]
    public void ADeliberatelyInertDummyAura_IsNotAGap_ButTheSameSpellUnderAnotherIdIs()
    {
        InertSpell entry = DeliberatelyInertSpells.All.First(e => e.Reason == InertSpellReason.Marker);
        SpellInfo listed = DummyAura(entry.SpellId), unlisted = DummyAura(Unlisted);
        var analyzer = new PlayerbotQaSpellGapAnalyzer(System(listed, unlisted));

        Assert.Empty(analyzer.Of(listed));
        Assert.Equal([QaSpellGaps.NoConsumer], analyzer.Of(unlisted).Select(g => g.Code));
        Assert.Equal([QaSpellGaps.NoConsumer], analyzer.Of(listed, honourExplicitLists: false).Select(g => g.Code));
    }

    [Fact]
    public void ADeliberatelyInertDummyEffect_IsNotAGap_ButTheSameSpellUnderAnotherIdIs()
    {
        InertSpell entry = DeliberatelyInertSpells.All.First(e => e.Reason == InertSpellReason.ScriptCarrier);
        SpellInfo listed = DummyEffect(entry.SpellId), unlisted = DummyEffect(Unlisted);
        var analyzer = new PlayerbotQaSpellGapAnalyzer(System(listed, unlisted));

        Assert.Empty(analyzer.Of(listed));
        Assert.Equal([QaSpellGaps.Dummy], analyzer.Of(unlisted).Select(g => g.Code));
    }

    [Fact]
    public void AnUnusedEffect_IsCoveredOnlyByAnEntryThatNamesIt()
    {
        InertSpell stealth = DeliberatelyInertSpells.All.First(e => e.Unhandled == nameof(SpellEffectName.Stealth));
        InertSpell evade = DeliberatelyInertSpells.All.First(e => e.Unhandled == nameof(SpellEffectName.Evade));
        SpellInfo own = Spell(stealth.SpellId, Effect(SpellEffectName.Stealth, 0)), other = Spell(evade.SpellId, Effect(SpellEffectName.Stealth, 0));
        var analyzer = new PlayerbotQaSpellGapAnalyzer(System(own, other));

        Assert.Empty(analyzer.Of(own));
        QaSpellGap gap = Assert.Single(analyzer.Of(other));
        Assert.Equal((QaSpellGaps.Effect, nameof(SpellEffectName.Stealth)), (gap.Code, gap.Detail));
    }

    [Fact]
    public void AConsumedDummyEffect_IsNotAGap_UnlessTheListsAreIgnored()
    {
        QaSpellConsumer consumer = QaSpellConsumers.All.First(c => c.Kind == QaSpellConsumerKind.SpellHitHandler);
        SpellInfo spell = DummyEffect(consumer.SpellId);
        var analyzer = new PlayerbotQaSpellGapAnalyzer(System(spell));

        Assert.Empty(analyzer.Of(spell));
        Assert.Equal([QaSpellGaps.Dummy], analyzer.Of(spell, honourExplicitLists: false).Select(g => g.Code));
    }

    [Fact]
    public void SilencedBecause_NamesTheReasonOrTheConsumer()
    {
        InertSpell entry = DeliberatelyInertSpells.All.First();
        QaSpellConsumer consumer = QaSpellConsumers.All.First();

        Assert.Contains(entry.Citation, PlayerbotQaSpellGapAnalyzer.SilencedBecause(entry.SpellId));
        Assert.Contains(consumer.File, PlayerbotQaSpellGapAnalyzer.SilencedBecause(consumer.SpellId));
        Assert.Null(PlayerbotQaSpellGapAnalyzer.SilencedBecause(Unlisted));
    }
}

/// <summary>
/// With the client DBCs: every listed spell exists in Spell.dbc, and each entry still silences a gap in the composed world (an entry the analyzer
/// would not have reported anyway means a handler or reader now exists: drop the entry), while the lists leave no gap at all on those spells.
/// </summary>
[Collection(SpellContractSweepCollection.Name)]
public sealed class PlayerbotQaSpellInertRegistryDbcTests
{
    [RealClientDbcFact]
    public void EveryListedSpell_ExistsInSpellDbc_AndTheListSilencesAGapOnIt()
    {
        SpellSweepCatalog catalog = SpellSweepCatalog.Shared;
        using var rig = new SpellSweepRig(new SpellStore([.. catalog.Spells, SweepHelperSpells.SelfDummySpell()], [], []), RealClientDbcFactAttribute.DbcDirectory);
        var analyzer = new PlayerbotQaSpellGapAnalyzer(rig.System);
        uint[] ids = [.. DeliberatelyInertSpells.All.Select(e => e.SpellId), .. QaSpellConsumers.All.Select(c => c.SpellId)];

        string[] problems = [.. ids.Select(id =>
        {
            SpellInfo? spell = catalog.Store.Get(id);
            if (spell is null) return $"{id}: not in Spell.dbc";
            if (analyzer.Of(spell, honourExplicitLists: false).Length == 0) return $"{id}: silences nothing (something else already handles or reads it)";
            return analyzer.Of(spell).Length != 0 ? $"{id}: a gap remains: {string.Join(", ", analyzer.Of(spell).Select(g => g.Code + ":" + g.Detail))}" : null;
        }).OfType<string>()];

        Assert.Empty(problems);
    }
}
