using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Quests;
using ArcaneCore.Kernel.Quests;
using Xunit;
using static ArcaneCore.Game.Tests.Npc.QuestFlowKit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// vmangos Player::CanTakeQuest (Player.cpp:12565-12577) through <see cref="QuestNpcServices.CanTakeQuest(Entities.Player, uint)"/>:
/// the relation checks a bot's quest-candidate scan runs per quest every think (exclusive groups, signed previous quests, chains and
/// breadcrumbs), and that the check and <see cref="QuestNpcServices.IsRewardable"/> allocate nothing on that path (the perf-tick lane's
/// allocation trace, 2026-10-19: closures and a visited set per call, about 0.5 KB per check).
/// </summary>
public sealed class QuestTakeCheckTests
{
    private const uint A = 930001;
    private const uint B = 930002;
    private const uint C = 930003;

    private static QuestTemplate Breadcrumb(uint id, uint target) => new()
    {
        Entry = id, Method = 2, MinLevel = 1, QuestLevel = 1, Title = $"Quest {id}", BreadcrumbForQuestId = target,
        Details = "Go there.", Objectives = "Talk.",
    };

    [Fact]
    public void AFollowUpOfANegativeExclusiveGroup_NeedsEveryMemberRewarded()
    {
        // SatisfyQuestPreviousQuest (13345-13435): a previous quest in a negative exclusive group needs the whole group rewarded
        QuestTemplate[] quests = [Task(A, exclusiveGroup: -5), Task(B, exclusiveGroup: -5), Task(C, previous: (int)A)];
        using (var one = new QuestFlowKit(quests, [Row(A, QuestStatus.Complete, rewarded: true)]))
        {
            Assert.False(one.Services.CanTakeQuest(one.Player, C));
        }

        using var both = new QuestFlowKit(quests, [Row(A, QuestStatus.Complete, rewarded: true), Row(B, QuestStatus.Complete, rewarded: true)]);
        Assert.True(both.Services.CanTakeQuest(both.Player, C));
    }

    [Fact]
    public void ANegativePreviousQuest_MustBeCurrent()
    {
        QuestTemplate[] quests = [Task(A), Task(C, previous: -(int)A)];
        using (var current = new QuestFlowKit(quests, [Row(A, QuestStatus.Incomplete)]))
        {
            Assert.True(current.Services.CanTakeQuest(current.Player, C));
        }

        using var rewarded = new QuestFlowKit(quests, [Row(A, QuestStatus.Complete, rewarded: true)]);
        Assert.False(rewarded.Services.CanTakeQuest(rewarded.Player, C));
    }

    [Fact]
    public void APositiveExclusiveGroup_RefusesOnceASiblingIsStarted()
    {
        // SatisfyQuestExclusiveGroup (13560-13592): a sibling in progress or done (a rewarded quest keeps status Complete) refuses.
        QuestTemplate[] quests = [Task(A, exclusiveGroup: 7), Task(B, exclusiveGroup: 7)];
        using (var busy = new QuestFlowKit(quests, [Row(B, QuestStatus.Incomplete)]))
        {
            Assert.False(busy.Services.CanTakeQuest(busy.Player, A));
        }

        using (var done = new QuestFlowKit(quests, [Row(B, QuestStatus.Complete, rewarded: true)]))
        {
            Assert.False(done.Services.CanTakeQuest(done.Player, A));
        }

        using var fresh = new QuestFlowKit(quests);
        Assert.True(fresh.Services.CanTakeQuest(fresh.Player, A));
    }

    [Fact]
    public void AChainedQuest_IsRefusedWhileItsPredecessorIsCurrent()
    {
        // SatisfyQuestPrevChain (13616-13644)
        QuestTemplate[] quests = [Task(A, nextInChain: B), Task(B)];
        using (var current = new QuestFlowKit(quests, [Row(A, QuestStatus.Incomplete)]))
        {
            Assert.False(current.Services.CanTakeQuest(current.Player, B));
        }

        using var rewarded = new QuestFlowKit(quests, [Row(A, QuestStatus.Complete, rewarded: true)]);
        Assert.True(rewarded.Services.CanTakeQuest(rewarded.Player, B));
    }

    [Fact]
    public void ABreadcrumbChain_FollowsItsTarget_AndATargetWaitsForADependentBreadcrumbInProgress()
    {
        // SatisfyQuestBreadcrumbQuest (13437-13453): A leads to B leads to C; each is takeable only while the quest it leads to is.
        // SatisfyQuestDependentBreadcrumbQuests (13455-13471): C is refused while a breadcrumb to it is in progress.
        QuestTemplate[] open = [Breadcrumb(A, B), Breadcrumb(B, C), Task(C)];
        using (var fresh = new QuestFlowKit(open))
        {
            Assert.True(fresh.Services.CanTakeQuest(fresh.Player, A));
            Assert.True(fresh.Services.CanTakeQuest(fresh.Player, B));
            Assert.True(fresh.Services.CanTakeQuest(fresh.Player, C));
        }

        QuestTemplate[] gated = [Breadcrumb(A, B), Breadcrumb(B, C), Task(C, minLevel: 60)];
        using (var low = new QuestFlowKit(gated))
        {
            Assert.False(low.Services.CanTakeQuest(low.Player, A));
            Assert.False(low.Services.CanTakeQuest(low.Player, B));
        }

        using var onBreadcrumb = new QuestFlowKit(open, [Row(A, QuestStatus.Incomplete)]);
        Assert.False(onBreadcrumb.Services.CanTakeQuest(onBreadcrumb.Player, C));
    }

    [Fact]
    public void CanTakeQuest_OnEveryRelationCheck_AllocatesNothing()
    {
        // B follows the rewarded A, sits in a positive exclusive group with C (not started), and is the target of the breadcrumb D. A
        // breadcrumb's own check (D) recurses into its target with a set of the quests on the path, so only the target is measured.
        const uint D = 930004;
        QuestTemplate[] quests = [Task(A, nextInChain: B), Task(B, previous: (int)A, exclusiveGroup: 9), Task(C, exclusiveGroup: 9), Breadcrumb(D, B)];
        using var kit = new QuestFlowKit(quests, [Row(A, QuestStatus.Complete, rewarded: true)]);
        QuestNpcServices services = kit.Services;
        Entities.Player player = kit.Player;
        Assert.True(services.CanTakeQuest(player, B));
        Assert.True(services.CanTakeQuest(player, D));

        AllocationProbe.AssertEveryCleanWindowAtMost(() =>
        {
            for (int i = 0; i < 128; i++)
            {
                services.CanTakeQuest(player, B);
            }
        }, 0, "CanTakeQuest x128");
    }

    [Fact]
    public void CanTakeQuest_ThroughANegativeExclusiveGroup_AllocatesNothing()
    {
        QuestTemplate[] quests = [Task(A, exclusiveGroup: -5), Task(B, exclusiveGroup: -5), Task(C, previous: (int)A)];
        using var kit = new QuestFlowKit(quests, [Row(A, QuestStatus.Complete, rewarded: true), Row(B, QuestStatus.Complete, rewarded: true)]);
        QuestNpcServices services = kit.Services;
        Entities.Player player = kit.Player;
        Assert.True(services.CanTakeQuest(player, C));

        AllocationProbe.AssertEveryCleanWindowAtMost(() =>
        {
            for (int i = 0; i < 128; i++)
            {
                services.CanTakeQuest(player, C);
            }
        }, 0, "CanTakeQuest (negative group) x128");
    }

    [Fact]
    public void IsRewardable_AllocatesNothing()
    {
        using var kit = new QuestFlowKit([Task(A), Task(B, special: (byte)QuestSpecialFlags.ExplorationOrEvent)]);
        QuestNpcServices services = kit.Services;
        Assert.True(services.IsRewardable(A));
        Assert.False(services.IsRewardable(B)); // an event quest no adapter covers stays unsupported

        AllocationProbe.AssertEveryCleanWindowAtMost(() =>
        {
            for (int i = 0; i < 128; i++)
            {
                services.IsRewardable(A);
            }
        }, 0, "IsRewardable x128");
    }
}
