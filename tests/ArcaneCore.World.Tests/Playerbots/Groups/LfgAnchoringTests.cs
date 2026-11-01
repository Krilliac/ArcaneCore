using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Combat;
using ArcaneCore.World.Playerbots.Groups;
using ArcaneCore.World.Playerbots.Party;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The anchoring rules of instance plans: <see cref="PlayerbotGroupContent.Match(IReadOnlyList{PlayerbotGroupCandidate}, int, PlayerbotGroupOptions, bool, bool)"/>
/// with <c>needAnchoring</c> (the predicate of the coordinator's <c>PlanAnchored</c>: a waiting bot that anchors the goal), the wrap-safe waiting
/// order (<see cref="PlayerbotGroupContent.SinceOrder"/>) and the per-pass cap of <c>PlayerbotPartyAI</c>'s LFG dungeon lookups.
/// </summary>
public sealed class LfgAnchoringTests
{
    private static readonly PlayerbotGroupOptions Options = new();

    // A waiting bot anchors (and qualifies); a recruit is in band (qualifies) but never anchors.
    private static PlayerbotGroupCandidate Bot(string name, uint since, bool anchoring, bool qualifying = true)
        => new(Guid.NewGuid(), (ulong)name.GetHashCode(StringComparison.Ordinal) & 0xFFFF, name, Class.Mage, 30, 1, 0, Vector3.Zero, PlayerbotRole.MeleeDps, 0, since,
            Qualifying: qualifying, Anchoring: anchoring);

    private static string[] Names(PlayerbotGroupPlan plan) => [.. plan.Members.Select(m => m.Bot.Name).OrderBy(n => n, StringComparer.Ordinal)];

    [Fact]
    public void APlanFixableByAnchoring_IsAccepted_WhereTheQualifyingRuleAloneFormsAnUnanchoredOne()
    {
        // Three recruits waited longest and are in band; the anchoring waiter is the newest. The qualifying rule is already met by a recruit, so
        // Fill picks the three recruits - a plan PlanAnchored then rejects, though swapping the anchor in fixes it.
        PlayerbotGroupCandidate[] candidates =
        [
            Bot("Recrua", 1, anchoring: false), Bot("Recrub", 2, anchoring: false), Bot("Recruc", 3, anchoring: false), Bot("Anchor", 100, anchoring: true),
        ];

        PlayerbotGroupPlan unanchored = PlayerbotGroupContent.Match(candidates, 3, Options, (0, 0), needQualifying: true)!;
        Assert.DoesNotContain(unanchored.Members, m => m.Bot.Anchoring);

        PlayerbotGroupPlan anchored = PlayerbotGroupContent.Match(candidates, 3, Options, (0, 0), needQualifying: true, needAnchoring: true)!;
        Assert.Equal(3, anchored.Members.Count);
        Assert.Contains(anchored.Members, m => m.Bot.Name == "Anchor");
    }

    [Fact]
    public void WithoutAnAnchoringCandidate_NoPlanIsFormed_EvenWhenEveryoneQualifies()
    {
        PlayerbotGroupCandidate[] candidates = [Bot("Recrua", 1, anchoring: false), Bot("Recrub", 2, anchoring: false), Bot("Recruc", 3, anchoring: false)];

        Assert.NotNull(PlayerbotGroupContent.Match(candidates, 3, Options, (0, 0), needQualifying: true));
        Assert.Null(PlayerbotGroupContent.Match(candidates, 3, Options, (0, 0), needQualifying: true, needAnchoring: true));
    }

    [Fact]
    public void WithoutNeedAnchoring_TheAnchoringFlagChangesNothing()
    {
        PlayerbotGroupCandidate[] candidates = [Bot("Recrua", 1, anchoring: false), Bot("Recrub", 2, anchoring: false), Bot("Anchor", 100, anchoring: true)];

        Assert.Equal(["Anchor", "Recrua", "Recrub"], Names(PlayerbotGroupContent.Match(candidates, 3, Options, (0, 0), needQualifying: false)!));
    }

    [Fact]
    public void SinceOrdering_StaysCorrectAcrossAnIntWrap()
    {
        const uint BeforeWrap = 0xFFFF_FFF0u;
        // The tick counter wrapped between the first two and the third: the plain uint order would put them last.
        Assert.True(PlayerbotGroupContent.SinceOrder.Compare(BeforeWrap, 5u) < 0);
        Assert.True(PlayerbotGroupContent.SinceOrder.Compare(5u, BeforeWrap) > 0);
        Assert.Equal(0, PlayerbotGroupContent.SinceOrder.Compare(7u, 7u));

        PlayerbotGroupCandidate[] candidates = [Bot("Newest", 10, anchoring: true), Bot("Oldest", BeforeWrap, anchoring: true), Bot("Middle", 5, anchoring: true)];
        Assert.Equal("Oldest", PlayerbotGroupContent.QualifyingAnchor(candidates)!.Name);
        Assert.Equal(["Middle", "Oldest"], Names(PlayerbotGroupContent.Match(candidates, 2, Options, (0, 0), needQualifying: true, needAnchoring: true)!));

        uint[] ordered = [.. new uint[] { 10, BeforeWrap, 5 }.OrderBy(v => v, PlayerbotGroupContent.SinceOrder)];
        Assert.Equal([BeforeWrap, 5u, 10u], ordered);
    }

    [Fact]
    public void ThePerPassLookupCap_IsEnforced_AndRenewsWithTheNextWindow()
    {
        PlayerbotPartyAI.LfgLookupBudget budget = PlayerbotPartyAI.LfgLookupBudget.Of(new object());
        for (int i = 0; i < PlayerbotPartyAI.MaxLfgLookupsPerPass; i++) Assert.True(budget.TryTake(5_000 + i), $"lookup {i}");
        Assert.False(budget.TryTake(5_500));
        Assert.False(budget.TryTake(5_000 + PlayerbotPartyAI.LfgLookupPassMs - 1));

        Assert.True(budget.TryTake(5_000 + PlayerbotPartyAI.LfgLookupPassMs)); // the next window
        for (int i = 1; i < PlayerbotPartyAI.MaxLfgLookupsPerPass; i++) Assert.True(budget.TryTake(6_100));
        Assert.False(budget.TryTake(6_100));

        Assert.True(budget.TryTake(100)); // a clock that went back opens a fresh window
    }

    [Fact]
    public void TheBudget_IsPerWorld()
    {
        object a = new(), b = new();
        Assert.Same(PlayerbotPartyAI.LfgLookupBudget.Of(a), PlayerbotPartyAI.LfgLookupBudget.Of(a));
        Assert.NotSame(PlayerbotPartyAI.LfgLookupBudget.Of(a), PlayerbotPartyAI.LfgLookupBudget.Of(b));
    }
}
