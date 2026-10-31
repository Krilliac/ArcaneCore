using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots.Qa;
using ArcaneCore.World.Tests.Honor;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Qa;

/// <summary>
/// The direct report path (<see cref="IPlayerbotQaSink"/>), the spell ids a finding keeps across a restart, and the spell publish caps: a first deploy
/// with many spell findings opens at most <see cref="PlayerbotQaGitHubOptions.MaxSpellIssuesPerPass"/> issues a pass and
/// <see cref="PlayerbotQaGitHubOptions.MaxSpellIssuesPerDay"/> a day, and a gap only the start-up coverage lists is never published on its own.
/// </summary>
public sealed class PlayerbotQaSpellCoreTests : IDisposable
{
    private readonly string _directory = QaData.NewTempDirectory();

    public void Dispose() => QaData.DeleteQuietly(_directory);

    internal static QaSignal Gap(uint spell, int effect = 0, string code = QaSpellGaps.Aura, Guid? bot = null, bool coverage = false, byte botClass = 8)
        => new()
        {
            Kind = QaSignalKind.SpellUnsupported, AtUtc = QaData.T0, BotKey = coverage ? null : bot ?? QaData.Bot1, BotClass = coverage ? (byte)0 : botClass,
            Spell = spell, SpellName = "Spell " + spell, SpellRank = "Rank 1", EffectIndex = effect, EffectName = "ApplyAura", EffectValue = 5, Aura = 12,
            Reason = code, Detail = "ModStun", FromCoverage = coverage, Message = "a bot cast spell " + spell,
        };

    // ----- the sink --------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ADisabledSink_QueuesNothing_AndAnEnabledOneQueuesEverySignal()
    {
        var bus = new PlayerbotQaSignalBus(64);
        var off = new PlayerbotQaSink(bus, Options.Create(new PlayerbotQaOptions { Enabled = false }));
        Assert.False(off.Enabled);
        off.Report(Gap(1));
        Assert.Equal(0, bus.Received);

        var on = new PlayerbotQaSink(bus, Options.Create(new PlayerbotQaOptions()));
        Assert.True(on.Enabled);
        on.Report(Gap(1));
        on.Report(Gap(2));
        Assert.Equal(2, bus.Received);
        Assert.Equal(2, on.Reported);
        Assert.False(NoPlayerbotQaSink.Instance.Enabled);
    }

    [Fact]
    public void ReportFor_ARealPlayerOrACreature_ReportsNothing()
    {
        var bus = new PlayerbotQaSignalBus(64);
        var sink = new PlayerbotQaSink(bus);
        Player human = RealPlayer();

        Assert.Null(PlayerbotQaBots.Identify(human));
        Assert.Null(PlayerbotQaBots.Identify(null));
        Assert.False(sink.ReportFor(human, Gap(1)));
        Assert.Equal(0, bus.Received);
    }

    [Fact]
    public void TheBotKey_IsStablePerCharacter_AndDiffersBetweenCharacters()
    {
        Guid a = PlayerbotQaBots.KeyOf(ObjectGuid.Player(7));
        Assert.Equal(a, PlayerbotQaBots.KeyOf(ObjectGuid.Player(7)));
        Assert.NotEqual(a, PlayerbotQaBots.KeyOf(ObjectGuid.Player(8)));
    }

    // ----- the finding -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void ACoverageSignal_MarksTheFinding_WithoutAnOccurrence_AndABotCastCountsOnTheSameFinding()
    {
        var aggregator = new PlayerbotQaAggregator(100, 5);

        string? fromCoverage = aggregator.Ingest(Gap(133, coverage: true), new FakeFacts(), QaData.T0);
        QaFindingView listed = aggregator.Get(fromCoverage!)!;
        Assert.Equal(0, listed.Count);
        Assert.Equal(0, listed.DistinctBots);
        Assert.True(listed.Coverage);
        Assert.Empty(listed.Samples);

        string? fromBot = aggregator.Ingest(Gap(133), new FakeFacts(), QaData.T0);
        Assert.Equal(fromCoverage, fromBot); // one finding: the same spell, effect and gap, whoever found it
        QaFindingView met = aggregator.Get(fromBot!)!;
        Assert.Equal(1, met.Count);
        Assert.Equal(1, met.DistinctBots);
        Assert.True(met.Coverage);
        Assert.Equal("Mage", met.ClassNames);
        Assert.Equal(QaCause.NotImplemented, met.Cause);
        Assert.Equal("spell 133 Spell 133 (Rank 1) effect 0 aura ModStun not implemented", met.Subject);
    }

    [Fact]
    public void EveryRankOfASpell_SharesOneFinding()
    {
        var aggregator = new PlayerbotQaAggregator(100, 5);

        string? rank1 = aggregator.Ingest(Gap(7384) with { SpellName = "Overpower", SpellRank = "Rank 1" }, new FakeFacts(), QaData.T0);
        string? rank4 = aggregator.Ingest(Gap(11585) with { SpellName = "Overpower", SpellRank = "Rank 4" }, new FakeFacts(), QaData.T0);
        string? other = aggregator.Ingest(Gap(133) with { SpellName = "Fireball" }, new FakeFacts(), QaData.T0);

        Assert.Equal(rank1, rank4);
        Assert.NotEqual(rank1, other);
        Assert.Equal((2L, 11585u), (aggregator.Get(rank1!)!.Count, aggregator.Get(rank1!)!.Spell)); // the latest rank met is the one to reproduce
    }

    [Fact]
    public void TheSpellIds_SurviveARestart()
    {
        var aggregator = new PlayerbotQaAggregator(100, 5);
        string id = aggregator.Ingest(Gap(133), new FakeFacts(), QaData.T0)!;
        var restored = new PlayerbotQaAggregator(100, 5, aggregator.Salt);

        Assert.Equal(0, restored.Restore(aggregator.Export()));

        QaFindingView view = restored.Get(id)!;
        Assert.Equal((133u, "Spell 133", "Rank 1", 0, "ApplyAura", 5, 12u, QaSpellGaps.Aura, "ModStun", 1u << 8),
            (view.Spell, view.SpellName, view.SpellRank, view.EffectIndex, view.EffectName, view.EffectValue, view.Aura, view.Reason, view.ReasonDetail, view.ClassMask));
    }

    // ----- the caps --------------------------------------------------------------------------------------------------------------------------

    private (PlayerbotQaPublisher Publisher, FakeGitHub GitHub, PlayerbotQaAggregator Findings, ManualTime Time, PlayerbotQaOptions Options) Rig(
        Action<PlayerbotQaGitHubOptions>? configure = null)
    {
        var options = new PlayerbotQaOptions
        {
            GitHub = { Enabled = true, Publish = true, Repository = QaData.Repo, MinOccurrences = 3, MinDistinctBots = 2, MaxIssuesPerHour = 1000 },
        };
        configure?.Invoke(options.GitHub);
        var github = new FakeGitHub();
        var time = new ManualTime(QaData.T0);
        // The findings are stamped with the build the publisher runs, as on a server (both read the assembly version): a gap met by this
        // build may open an issue however long ago (GitHub:CreateWithinHours bounds only findings the running build has not met).
        var aggregator = new PlayerbotQaAggregator(1000, 5, build: "test-build");
        var publisher = new PlayerbotQaPublisher(options, _directory, aggregator, new PlayerbotQaRedactor(), new ListLogger(), _ => QaData.FakeToken,
            token => new PlayerbotQaGitHubClient(github.Http, QaData.Repo, token, options.GitHub, (_, _) => Task.CompletedTask, null, () => time.GetUtcNow()),
            time, "test-build", null, (_, _) => Task.CompletedTask);
        return (publisher, github, aggregator, time, options);
    }

    [Fact]
    public async Task AFirstDeployWithManySpellGaps_OpensAtMostThePerPassCap_ThenTheDailyCap()
    {
        var (publisher, github, findings, time, _) = Rig(git => { git.MaxSpellIssuesPerPass = 2; git.MaxSpellIssuesPerDay = 5; });
        for (uint spell = 1; spell <= 40; spell++) findings.Ingest(Gap(spell), new FakeFacts(), QaData.T0); // forty gaps a bot met once each

        await publisher.RunAsync(findings.Snapshot(), CancellationToken.None);
        Assert.Equal(2, github.Creates);

        for (int pass = 0; pass < 5; pass++)
        {
            time.Advance(TimeSpan.FromMinutes(5));
            await publisher.RunAsync(findings.Snapshot(), CancellationToken.None);
        }

        Assert.Equal(5, github.Creates); // the daily cap
        time.Advance(TimeSpan.FromHours(24));
        await publisher.RunAsync(findings.Snapshot(), CancellationToken.None);
        Assert.Equal(7, github.Creates); // a new day, one more pass worth
    }

    [Fact]
    public async Task TheSpellCaps_DoNotHoldBackOtherKinds()
    {
        var (publisher, github, findings, _, _) = Rig(git => git.MaxSpellIssuesPerPass = 0);
        for (uint spell = 1; spell <= 5; spell++) findings.Ingest(Gap(spell), new FakeFacts(), QaData.T0);
        for (int i = 0; i < 3; i++) findings.Ingest(QaData.Relay(7), new FakeFacts(), QaData.T0);

        await publisher.RunAsync(findings.Snapshot(), CancellationToken.None);

        Assert.Equal(1, github.Creates); // the relay finding; no spell issue at a zero cap
    }

    [Fact]
    public async Task AGapOnlyTheCoverageLists_IsNeverPublished_NorDryRun()
    {
        var (publisher, github, findings, _, options) = Rig();
        for (uint spell = 1; spell <= 30; spell++) findings.Ingest(Gap(spell, coverage: true), new FakeFacts(), QaData.T0);

        await publisher.RunAsync(findings.Snapshot(), CancellationToken.None);
        options.GitHub.Publish = false;
        await publisher.RunAsync(findings.Snapshot(), CancellationToken.None);

        Assert.Equal(0, github.Creates);
        Assert.Equal(0, github.Gets);
        Assert.False(Directory.Exists(Path.Combine(_directory, "pending-issues")));
    }

    [Fact]
    public async Task ASpellGapABotMetOnce_IsPublished_WithoutTheBotThresholds()
    {
        var (publisher, github, findings, _, _) = Rig();
        findings.Ingest(Gap(133), new FakeFacts(), QaData.T0);

        await publisher.RunAsync(findings.Snapshot(), CancellationToken.None);

        Assert.Equal(1, github.Creates);
    }

    [Fact]
    public void TheDailySpellCap_SurvivesARestart()
    {
        var ledger = new QaLedger();
        ledger.RecordSpellCreate(QaData.T0);
        ledger.RecordSpellCreate(QaData.T0);
        var restarted = new QaLedger();

        restarted.Restore(ledger.Export());

        Assert.False(restarted.CanCreateSpell(QaData.T0.AddHours(23), 2));
        Assert.True(restarted.CanCreateSpell(QaData.T0.AddHours(24), 2));
        restarted.Restore(new QaLedgerState([], [])); // a file written before the spell caps
        Assert.True(restarted.CanCreateSpell(QaData.T0, 1));
    }

    [Fact]
    public async Task KindLabels_AddTheKindsLabel_OnlyWhenAskedFor()
    {
        var (publisher, github, findings, _, options) = Rig();
        findings.Ingest(Gap(133), new FakeFacts(), QaData.T0);
        QaFindingView view = findings.Snapshot()[0];

        Assert.Equal(["bot-qa"], publisher.LabelsFor(view));
        options.GitHub.KindLabels = true;
        Assert.Equal(["bot-qa", "bot-qa:spell-unsupported"], publisher.LabelsFor(view));

        await publisher.RunAsync(findings.Snapshot(), CancellationToken.None);
        Assert.Contains("bot-qa:spell-unsupported", github.Http.Requests.Single(r => r.Method == HttpMethod.Post).Body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSpellCapOptions_AreValidated()
    {
        Assert.Contains("GitHub:MaxSpellIssuesPerPass must be 0..100", new PlayerbotQaGitHubOptions { MaxSpellIssuesPerPass = 101 }.Validate());
        Assert.Contains("GitHub:MaxSpellIssuesPerDay must be 0..1000", new PlayerbotQaGitHubOptions { MaxSpellIssuesPerDay = -1 }.Validate());
        Assert.Empty(new PlayerbotQaGitHubOptions().Validate());
    }

    /// <summary>The static gap check names an implicit target the spell system cannot select (59) and stays silent for one it can (48: caster back).</summary>
    [Fact]
    public void TheGapAnalyzer_ListsAnUnselectableImplicitTarget_AndNoneForCasterBack()
    {
        SpellInfo gap59 = QaSpellKit.Spell(990059, QaSpellKit.Effect(SpellEffectName.Dummy, 0, (SpellImplicitTarget)59));
        SpellInfo back48 = QaSpellKit.Spell(990048, QaSpellKit.Effect(SpellEffectName.Dummy, 0, (SpellImplicitTarget)48));
        var system = new SpellSystem(new SpellStore([gap59, back48], [], []), () => 0);
        new ScriptEffectModule().Register(system);
        var analyzer = new PlayerbotQaSpellGapAnalyzer(system);

        QaSpellGap listed = Assert.Single(analyzer.Of(gap59), g => g.Code == QaSpellGaps.Target);
        Assert.Equal("59 (59)", listed.Detail);
        Assert.DoesNotContain(analyzer.Of(back48), g => g.Code == QaSpellGaps.Target);
    }

    private static Player RealPlayer()
    {
        var character = new CharacterRecord { Id = 1, AccountId = 1, Name = "Human", Race = 1, Class = 8, Gender = 0, Level = 60, MapId = 0, ZoneId = 12, X = 0, Y = 0, Z = 83.5f };
        var appearance = new PlayerAppearance(49, 1, PowerType.Mana, 60, 0, 60, 1000, 0, 400);
        return new Player(character, appearance, new NullPlayerSession());
    }
}
