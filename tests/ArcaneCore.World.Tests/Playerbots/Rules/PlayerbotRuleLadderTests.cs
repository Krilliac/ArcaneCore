using ArcaneCore.Game.Rules;
using ArcaneCore.Game.Rules.Bot;
using ArcaneCore.Kernel.Rules;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Rules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Rules;

/// <summary>The brain's rule host (<see cref="PlayerbotRuleLadder"/>): which catalog it runs, the empty-table fallback and its warning, and a custom insert-only row.</summary>
public sealed class PlayerbotRuleLadderTests
{
    private const int CustomRuleId = RuleCatalog.CustomIdStart + 7;
    private const short CustomGoalId = RuleCatalog.CustomGoalIdStart + 7;

    private static RuleCatalog Build(RuleContent content) => RuleCatalog.Build(content, RuleVocabulary.Default, new RuleBuildOptions());

    private static ServiceProvider Services(CapturingLoggerFactory? logs = null)
    {
        var services = new ServiceCollection();
        if (logs is not null) services.AddSingleton<ILoggerFactory>(logs);
        return services.BuildServiceProvider();
    }

    private static RuleFeature Feature(ServiceProvider sp, RuleContent content)
    {
        var feature = new RuleFeature(sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<RuleFeature>.Instance);
        feature.Replace(Build(content));
        return feature;
    }

    [Fact]
    public void WithNoLadderRowsLoaded_TheShippedLadderRuns_AndOneWarningSaysSo()
    {
        var logs = new CapturingLoggerFactory();
        using ServiceProvider sp = Services(logs);
        PlayerbotRuleLadder host = PlayerbotRuleLadder.Create(sp, Feature(sp, RuleContent.Empty));

        RuleCatalog first = host.Catalog();
        RuleCatalog second = host.Catalog();
        var bot = new PlayerbotLadderReplayTests.ReplayBot(5);
        host.CreateLadder().Choose(bot, first, 0);

        Assert.Same(first, second);
        Assert.Equal(PlayerbotLadderReplayTests.TodaysOrder, bot.Candidates.ToArray().Select(c => c.Rung).ToArray());
        Assert.Single(logs.Warnings);
        Assert.Contains("shipped ladder", logs.Warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoRuleFeatureAtAll_TheShippedLadderRuns()
    {
        using ServiceProvider sp = Services();
        PlayerbotRuleLadder host = PlayerbotRuleLadder.Create(sp, null);
        var bot = new PlayerbotLadderReplayTests.ReplayBot(5);

        host.CreateLadder().Choose(bot, host.Catalog(), 0);

        Assert.Equal(PlayerbotLadderReplayTests.TodaysOrder, bot.Candidates.ToArray().Select(c => c.Rung).ToArray());
    }

    [Fact]
    public void WithTheSeededRowsLoaded_TheLoadedCatalogRuns_WithoutAWarning()
    {
        var logs = new CapturingLoggerFactory();
        using ServiceProvider sp = Services(logs);
        RuleFeature feature = Feature(sp, PlayerbotLadderContent.Content);
        PlayerbotRuleLadder host = PlayerbotRuleLadder.Create(sp, feature);

        Assert.Same(feature.Catalog, host.Catalog());
        Assert.Empty(logs.Warnings);
    }

    [Fact]
    public void GoalRowsWithoutAnyLadderRule_FallBackToTheShippedLadder_WithTheWarning()
    {
        var logs = new CapturingLoggerFactory();
        using ServiceProvider sp = Services(logs);
        // The goal rows are there, the rule table held other rows so the ladder rules were never seeded.
        RuleFeature feature = Feature(sp, new RuleContent { Goals = PlayerbotLadderContent.Goals });
        PlayerbotRuleLadder host = PlayerbotRuleLadder.Create(sp, feature);
        var bot = new PlayerbotLadderReplayTests.ReplayBot(5);

        host.CreateLadder().Choose(bot, host.Catalog(), 0);

        Assert.Equal(PlayerbotLadderReplayTests.TodaysOrder, bot.Candidates.ToArray().Select(c => c.Rung).ToArray());
        Assert.Single(logs.Warnings);
    }

    [Fact]
    public void BrainsWithoutARuleFeature_ShareNoEvaluator_AndConcurrentPassesNeverDisturbEachOther()
    {
        using ServiceProvider sp = Services();
        using ServiceProvider other = Services();
        PlayerbotRuleLadder host = PlayerbotRuleLadder.For(sp);
        Assert.Same(host, PlayerbotRuleLadder.For(sp));
        Assert.NotSame(host, PlayerbotRuleLadder.For(other));

        int bad = 0;
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, worker =>
        {
            // Each brain asks the shared host for its own ladder, as PlayerbotBrain does.
            BotGoalLadder ladder = PlayerbotRuleLadder.For(sp).CreateLadder();
            var bot = new PlayerbotLadderReplayTests.ReplayBot((ulong)worker + 1);
            for (int i = 0; i < 3000; i++)
            {
                ladder.Choose(bot, host.Catalog(), i);
                if (bot.CandidateCount != 16) Interlocked.Increment(ref bad);
            }
        });

        Assert.Equal(0, bad);
        Assert.Equal(0, host.Counters.Faults);
        Assert.Equal(0, host.Counters.Reentrant);
    }

    [Fact]
    public void TheCatalogChoiceAndAPass_AllocateNothingOncePrimed()
    {
        using ServiceProvider sp = Services();
        PlayerbotRuleLadder host = PlayerbotRuleLadder.For(sp);
        BotGoalLadder ladder = host.CreateLadder();
        var bot = new PlayerbotLadderReplayTests.ReplayBot(3);
        for (int i = 0; i < 50; i++) ladder.Choose(bot, host.Catalog(), i);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {

            ladder.Choose(bot, host.Catalog(), i);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ACustomInsertOnlyRow_ForA30PercentShareOfAClass_ReachesThatShare_OfTheLoadedCatalog()
    {
        RuleContent content = PlayerbotLadderContent.Content with
        {
            Rules = [.. PlayerbotLadderContent.Rules, new RuleRecord
            {
                Id = CustomRuleId, Host = (byte)RuleHostKind.Bot, EventType = BotRuleIds.GoalEvaluate, FilterId = 1, ChancePct = 100,
                Flags = (int)RuleFlags.Repeatable, Comment = "30% of warriors go to the trainer first",
            }],
            Actions = [.. PlayerbotLadderContent.Actions, new RuleActionRecord
            {
                OwnerId = CustomRuleId, Seq = 0, ActionType = BotRuleIds.ProposeGoal, Param1 = CustomGoalId, Param2 = 5000, TargetType = BotRuleIds.SelfTarget,
            }],
            Filters = [new RuleSubjectFilterRecord { Id = 1, ClassMask = 1, SharePct = 30, ShareSeed = 4242 }],
            Goals = [.. PlayerbotLadderContent.Goals, new PlayerbotGoalRecord
            {
                GoalId = CustomGoalId, Name = "custom: trainer first", BasePriority = 0, Driver = (short)PlayerbotLadderRung.Trainers,
            }],
        };
        using ServiceProvider sp = Services();
        RuleFeature feature = Feature(sp, content);
        Assert.Empty(feature.Catalog.Rejected);
        PlayerbotRuleLadder host = PlayerbotRuleLadder.Create(sp, feature);

        int warriorsFirst = 0;
        int warriors = 0;
        for (ulong guid = 1; guid <= 2000; guid++)
        {
            var bot = new PlayerbotLadderReplayTests.ReplayBot(guid);
            host.CreateLadder().Choose(bot, host.Catalog(), 0);
            bool first = bot.Candidates[0].Rung == PlayerbotLadderRung.Trainers;
            if (bot.Facts.Class == 1)
            {
                warriors++;
                if (first) warriorsFirst++;
            }
            else
            {
                Assert.False(first);
            }
        }

        double share = 100.0 * warriorsFirst / warriors;
        Assert.InRange(share, 22, 38);
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<string> Warnings { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new Capture(this);

        public void Dispose()
        {
        }

        private sealed class Capture(CapturingLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning) owner.Warnings.Add(formatter(state, exception));
            }
        }
    }
}
