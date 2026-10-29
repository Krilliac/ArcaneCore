using System.Text.Json.Nodes;
using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Paladin;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static ArcaneCore.World.Tests.Playerbots.Qa.QaSpellKit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// The static spell coverage: which spells bots can cast (rotation names restricted to the class family, class trainer spells) hit a gap, the gaps as
/// coverage findings that a bot's cast later counts on, the report section, the command, and that a gap no bot met is never published.
/// </summary>
public sealed class PlayerbotQaSpellCoverageTests : IDisposable
{
    // The ids sit above the Spell.dbc range: DeliberatelyInertSpells and QaSpellConsumers silence real ids (5001 and 5002 are listed).
    private const uint Fireball = 133, FrostNova = 122, NpcFireball = 9053, Teacher = 995001, Taught = 995002, Profession = 995003, PassiveTaught = 995004,
        TeachesPassive = 995005;

    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    private static AuraType Unsupported => AuraSupport.Entries.First(e => e.Level == AuraSupportLevel.Unsupported).Type;

    private static SpellInfo[] Spells() =>
    [
        Spell(Fireball, Effect(SpellEffectName.SchoolDamage, 14, SpellImplicitTarget.UnitEnemy)) with { Name = "Fireball", SpellFamilyName = 3 },
        Spell(FrostNova, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, Unsupported)) with { Name = "Frost Nova", SpellFamilyName = 3, Rank = "Rank 1" },
        Spell(NpcFireball, Effect(NoHandler, 1)) with { Name = "Fireball", SpellFamilyName = 0 }, // a creature's spell of the same name: not a bot's
        Spell(Teacher, new SpellEffectInfo { Effect = SpellEffectName.LearnSpell, TriggerSpell = Taught }) with { Name = "Teach" },
        Spell(Taught, Effect(SpellEffectName.Dummy, 0)) with { Name = "Taunting Shout", SpellFamilyName = 4 },
        Spell(Profession, Effect(NoHandler, 1)) with { Name = "Smelting", SpellFamilyName = 0 },
        Spell(PassiveTaught, Effect(NoHandler, 1)) with { Name = "Passive", SpellFamilyName = 4, Attributes = SpellAttributes.Passive },
        Spell(TeachesPassive, new SpellEffectInfo { Effect = SpellEffectName.LearnSpell, TriggerSpell = PassiveTaught }) with { Name = "Teach passive" },
    ];

    private static TrainerSpell[] Rows() =>
        [
            new() { Entry = 1, Spell = Fireball }, new() { Entry = 1, Spell = FrostNova }, new() { Entry = 2, Spell = Teacher },
            new() { Entry = 3, Spell = Profession }, new() { Entry = 2, Spell = TeachesPassive },
        ];

    private static SpellSystem System()
    {
        var system = new SpellSystem(new SpellStore(Spells(), [], []), () => 0);
        new ScriptEffectModule().Register(system);
        return system;
    }

    [Fact]
    public void BlessingOfLightDummyAuras_AreReadByTheHealingBonusRule()
    {
        SpellInfo blessing = Spell(19977,
            Effect(SpellEffectName.ApplyAura, 210, aura: AuraType.Dummy),
            Effect(SpellEffectName.ApplyAura, 60, aura: AuraType.Dummy)) with
        {
            SpellFamilyName = PaladinSpells.Family,
            SpellFamilyFlags = 1UL << BlessingOfLightRules.BlessingsBit,
            SpellVisual = BlessingOfLightRules.BlessingOfLightVisual,
        };
        var system = new SpellSystem(new SpellStore([blessing], [], []), () => 0);

        Assert.Empty(new PlayerbotQaSpellGapAnalyzer(system).Of(blessing));
    }

    [Fact]
    public void TheCoverage_CountsTheSpellsBotsCanCast_AndListsEveryGap()
    {
        QaSpellCoverageReport report = PlayerbotQaSpellCoverage.Build(System(),
            [(Class.Mage, ["Fireball", "Frost Nova"]), (Class.Warrior, ["Charge"])], Rows(), [], QaData.T0);

        Assert.Equal((3, 2, 2, 1, 5), (report.Castable, report.RotationCastable, report.SpellsWithGaps, report.RotationSpellsWithGaps, report.TrainerRows));
        Assert.Equal(
            [
                new QaCoverageGap(FrostNova, "Frost Nova", "Rank 1", 0, "ApplyAura", 0, (uint)Unsupported, QaSpellGaps.Aura, Unsupported.ToString(), 1u << (int)Class.Mage, true),
                new QaCoverageGap(Taught, "Taunting Shout", "Rank 2", 0, "Dummy", 0, 0, QaSpellGaps.Dummy, "Dummy", 1u << (int)Class.Warrior, false),
            ],
            report.Gaps);
        Assert.Equal(
            "Spell coverage: 1 of 3 spells bots can cast are fully handled (1 of 2 rotation and escape spells); 2 gaps in 2 spells (1 rotation spells)",
            report.Describe());
        Assert.Equal([new QaCoverageOffender(QaSpellGaps.Aura, Unsupported.ToString(), 1, 1), new QaCoverageOffender(QaSpellGaps.Dummy, "Dummy", 1, 0)],
            report.TopOffenders(5));
    }

    [Fact]
    public void ARotationNameOutsideTheBook_IsNotCastable()
    {
        // No trainer teaches Frost Nova here and no start or talent spell is it: a bot cannot have it, whatever its rotation names.
        QaSpellCoverageReport report = PlayerbotQaSpellCoverage.Build(System(), [(Class.Mage, ["Fireball", "Frost Nova"])],
            [new TrainerSpell { Entry = 1, Spell = Fireball }], [], QaData.T0);

        Assert.Equal((1, 1, 0), (report.Castable, report.RotationCastable, report.SpellsWithGaps));
    }

    [Fact]
    public async Task AnAdvisoryGap_ADummyAuraNothingKnownReads_IsNeverPublished_EvenWhenABotCastIt()
    {
        var options = new PlayerbotQaOptions { GitHub = { Enabled = true, Repository = QaData.Repo, MinOccurrences = 1, MinDistinctBots = 1 } };
        var aggregator = new PlayerbotQaAggregator(100, 5);
        aggregator.Ingest(PlayerbotQaSpellCoreTests.Gap(20375, code: QaSpellGaps.NoConsumer) with { Detail = "Dummy" }, new FakeFacts(), QaData.T0);
        aggregator.Ingest(PlayerbotQaSpellCoreTests.Gap(20376, code: QaSpellGaps.Aura), new FakeFacts(), QaData.T0);
        var publisher = new PlayerbotQaPublisher(options, _directory, aggregator, new PlayerbotQaRedactor(), new ListLogger(), _ => null, null,
            new ManualTime(QaData.T0), "b");

        await publisher.RunAsync(aggregator.Snapshot(), CancellationToken.None);

        string[] written = [.. Directory.GetFiles(Path.Combine(_directory, "pending-issues"), "*.md")];
        string only = Assert.Single(written);
        Assert.Contains("spell 20376", File.ReadAllText(only), StringComparison.Ordinal);
    }

    [Fact]
    public void ACoverageGap_IsTheSameFindingABotsCastLaterCounts()
    {
        QaSpellCoverageReport report = PlayerbotQaSpellCoverage.Build(System(), [(Class.Mage, ["Frost Nova"])], [], [FrostNova], QaData.T0); // a talent-style book spell
        var aggregator = new PlayerbotQaAggregator(100, 5);
        string id = Assert.Single(PlayerbotQaSpellCoverage.Signals(report).Select(s => aggregator.Ingest(s, new FakeFacts(), QaData.T0)))!;
        Assert.Equal(0, aggregator.Get(id)!.Count);

        using var kit = new QaSpellKit();
        var signal = new QaSignal
        {
            Kind = QaSignalKind.SpellUnsupported, AtUtc = QaData.T0, BotKey = QaData.Bot1, BotClass = 8, Spell = FrostNova, SpellName = "Frost Nova", EffectIndex = 0,
            Reason = QaSpellGaps.Aura, Detail = Unsupported.ToString(),
        };

        Assert.Equal(id, aggregator.Ingest(signal, new FakeFacts(), QaData.T0));
        Assert.Equal(1, aggregator.Get(id)!.Count);
    }

    [Fact]
    public void ACoverageGap_NeverEvictsAFindingOfRealOccurrences_ButARealOneEvictsAGap()
    {
        var aggregator = new PlayerbotQaAggregator(10, 5);
        for (uint quest = 1; quest <= 10; quest++) aggregator.Ingest(QaData.Stall(QaData.Bot1, 1, 1, quest), new FakeFacts(), QaData.T0);

        Assert.Null(aggregator.Ingest(PlayerbotQaSpellCoreTests.Gap(1, coverage: true), new FakeFacts(), QaData.T0));
        Assert.Equal(10, aggregator.Snapshot().Count(f => f.Kind == QaSignalKind.Stall));

        var gaps = new PlayerbotQaAggregator(10, 5);
        for (uint spell = 1; spell <= 10; spell++) gaps.Ingest(PlayerbotQaSpellCoreTests.Gap(spell, coverage: true), new FakeFacts(), QaData.T0);
        Assert.NotNull(gaps.Ingest(QaData.Stall(QaData.Bot1, 1, 1), new FakeFacts(), QaData.T0));
        Assert.Equal(9, gaps.Snapshot().Count(f => f.Count == 0));
    }

    [Fact]
    public async Task TheFeature_ComputesTheCoverageOnAPass_ReportsIt_AndPublishesNoGapNoBotMet()
    {
        var options = new PlayerbotQaOptions
        {
            Directory = _directory, GitHub = { Enabled = true, Repository = QaData.Repo, MinOccurrences = 1, MinDistinctBots = 1 }, // a dry run
        };
        await using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IOptions<PlayerbotQaOptions>>(Options.Create(options))
            .AddSingleton(new PlayerbotQaSignalBus(64))
            .AddSingleton<ILogger<SpellFeature>>(NullLogger<SpellFeature>.Instance)
            .AddSingleton<SpellFeature>()
            .BuildServiceProvider();
        var feature = new PlayerbotQaFeature(services);
        feature.Attach(null!);
        Assert.NotNull(feature.SpellMonitor); // registered on the world's spell system
        Assert.False(feature.TryComputeSpellCoverage()); // the spell table is not loaded yet
        SpellSystem system = services.GetRequiredService<SpellFeature>().System;
        system.Store = new SpellStore(Spells(), [((byte)Race.Human, (byte)Class.Mage, FrostNova)], []); // a mage's start spell: in the book

        feature.RunPass(CancellationToken.None);
        await feature.Publishing.WaitAsync(TimeSpan.FromSeconds(20));

        QaSpellCoverageReport coverage = feature.SpellCoverage!;
        Assert.Contains(coverage.Gaps, g => g.Spell == FrostNova && g.Rotation); // the production mage rotation and escapes name Frost Nova
        QaFindingView gap = Assert.Single(feature.Aggregator!.Snapshot(), f => f.Spell == FrostNova);
        Assert.Equal((0L, true), (gap.Count, gap.Coverage));
        Assert.False(Directory.Exists(Path.Combine(_directory, "pending-issues"))); // listed, not published
        string markdown = File.ReadAllText(Path.Combine(_directory, "report.md"));
        Assert.Contains("## Spell coverage", markdown, StringComparison.Ordinal);
        Assert.Contains("| 122 | Frost Nova (Rank 1) | Mage | yes | #0 ApplyAura |", markdown, StringComparison.Ordinal);
        Assert.Contains("No findings.", markdown, StringComparison.Ordinal); // a gap no bot met is not a seen problem
        JsonNode json = JsonNode.Parse(File.ReadAllText(Path.Combine(_directory, "report.json")))!;
        Assert.Equal(coverage.Castable, (int)json["SpellCoverage"]!["Castable"]!);
        Assert.Equal(coverage.Gaps.Count, json["SpellCoverage"]!["Gaps"]!.AsArray().Count);
        Assert.NotEmpty(json["SpellCoverage"]!["Offenders"]!.AsArray());
        Assert.Equal(["Bot QA: no findings."], PlayerbotQaReport.ChatTop(feature.Aggregator.Snapshot(), 10, feature.Redactor));
        Assert.False(feature.TryComputeSpellCoverage()); // computed once (no trainer rows will appear in this host)
        await feature.StopAsync();
    }

    [Fact]
    public void QaSpells_ShowsTheCoverage_TheOffenders_AndTheSpellFindingsBotsMet()
    {
        QaSpellCoverageReport report = PlayerbotQaSpellCoverage.Build(System(), [(Class.Mage, ["Frost Nova"])], Rows(), [], QaData.T0);
        var aggregator = new PlayerbotQaAggregator(100, 5);
        foreach (QaSignal signal in PlayerbotQaSpellCoverage.Signals(report)) aggregator.Ingest(signal, new FakeFacts(), QaData.T0);
        aggregator.Ingest(PlayerbotQaSpellCoreTests.Gap(FrostNova, code: QaSpellGaps.Aura) with { Detail = Unsupported.ToString() }, new FakeFacts(), QaData.T0);

        IReadOnlyList<string> lines = PlayerbotQaReport.ChatSpells(aggregator.Snapshot(), report, 10, new PlayerbotQaRedactor());
        IReadOnlyList<string> early = PlayerbotQaReport.ChatSpells([], null, 10, new PlayerbotQaRedactor());

        Assert.Equal(report.Describe(), lines[0]);
        Assert.Contains($"  aura {Unsupported} not implemented: 1 spells (1 rotation)", lines);
        Assert.Contains("Spell findings bots met: top 1 of 1", lines);
        Assert.Contains(lines, l => l.StartsWith("#1 ", StringComparison.Ordinal) && l.EndsWith("[Mage]", StringComparison.Ordinal));
        Assert.StartsWith("Spell coverage: not computed yet", early[0], StringComparison.Ordinal);
        Assert.Equal("No spell finding met by a bot yet.", early[^1]);
    }
}
