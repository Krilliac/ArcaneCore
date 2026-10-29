using ArcaneCore.Game.Rules;
using ArcaneCore.Game.Rules.Bot;
using ArcaneCore.Kernel.Rules;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Rules;

/// <summary>
/// The bot goal ladder as it was hard-coded in <c>PlayerbotBrain</c>, recorded on a fixed seed BEFORE the ladder became rules (spec P1,
/// "Replay test"). The constants below are that recording, taken from the hard-coded order (the third commit of this lane); the
/// rule-driven ladder must reproduce them, so any change to the shipped rules or the ranking shows up here.
/// </summary>
public sealed class PlayerbotLadderReplayTests
{
    internal const string RecordedDigest = "DCB6EB348C404A277D82A846ABE3E4D80C4D438C4C591B9B9807152A0F39CFBC";
    internal const string RecordedHistogram = "1=310,2=316,3=280,4=298,5=285,6=309,7=288,8=305,9=271,10=302,11=251,12=254,13=274,14=223,15=266,16=231,fall=9937";

    /// <summary>The order the brain called the rungs in before the rules existed.</summary>
    internal static readonly PlayerbotLadderRung[] TodaysOrder =
    [
        PlayerbotLadderRung.HelpErrand, PlayerbotLadderRung.Equipment, PlayerbotLadderRung.ClassUpkeep, PlayerbotLadderRung.ReturnHome,
        PlayerbotLadderRung.UrgentTown, PlayerbotLadderRung.Policy, PlayerbotLadderRung.Quests, PlayerbotLadderRung.Town,
        PlayerbotLadderRung.SicknessStandDown, PlayerbotLadderRung.Crafting, PlayerbotLadderRung.Economy, PlayerbotLadderRung.Bank,
        PlayerbotLadderRung.Opportunities, PlayerbotLadderRung.Trainers, PlayerbotLadderRung.Riding, PlayerbotLadderRung.QuestDestinations,
    ];

    internal sealed class ReplayBot(ulong guid) : BotRuleSubject
    {
        public override ulong Guid { get; } = guid;

        public override bool IsInCombat => false;

        public override RuleSubjectFacts Facts { get; } = new((byte)(1 + guid % 9), 1, 30, 2, 0, 0, 0, RuleSubjectFlags.Player | RuleSubjectFlags.Bot);

        public override long Money => 0;

        public override int FreeBagSlots => 10;
    }

    private static RuleCatalog ShippedCatalog()
        => RuleCatalog.Build(PlayerbotLadderContent.Content, RuleVocabulary.Default, new RuleBuildOptions());

    private static LadderReplay.Result ReplayThroughTheRules()
    {
        RuleCatalog catalog = ShippedCatalog();
        Assert.Empty(catalog.Rejected);
        var ladder = new BotGoalLadder(new RuleEvaluator(new Random(1)));
        var bots = new Dictionary<int, ReplayBot>();
        return LadderReplay.Run((bot, think) =>
        {
            if (!bots.TryGetValue(bot, out ReplayBot? subject)) bots[bot] = subject = new ReplayBot((ulong)bot + 1);
            ladder.Choose(subject, catalog, think * 1000L);
            return [.. subject.Candidates.ToArray().Select(c => c.Rung)];
        });
    }

    [Fact]
    public void TheShippedRules_RankTheRungsInTodaysOrder()
    {
        var ladder = new BotGoalLadder(new RuleEvaluator(new Random(1)));
        var bot = new ReplayBot(1);

        ladder.Choose(bot, ShippedCatalog(), 0);

        Assert.Equal(TodaysOrder, bot.Candidates.ToArray().Select(c => c.Rung).ToArray());
    }

    [Fact]
    public void TheRuleDrivenLadder_ReplaysToTheRecordedChoices()
    {
        LadderReplay.Result result = ReplayThroughTheRules();

        Assert.True(result.Histogram == RecordedHistogram && result.Digest == RecordedDigest,
            $"histogram={result.Histogram} digest={result.Digest}");
    }
}
