using ArcaneCore.Data.Content.Names;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Chat;
using ArcaneCore.World.Chat;
using Xunit;

namespace ArcaneCore.World.Tests.Chat;

public sealed class ChatWordFilterTests
{
    [Fact]
    public void CensorRulesMaskEachMatchCaseInsensitively()
    {
        var filter = new ChatWordFilter([new ChatWordFilterRule(1, "dang", ChatWordFilterScope.Chat, ChatWordFilterAction.Censor)]);
        Assert.Equal("oh **** it, ****!", filter.Apply("oh dang it, DANG!"));
        Assert.Equal("clean", filter.Apply("clean"));
    }

    [Theory]
    [InlineData(ChatWordFilterAction.Censor)]
    [InlineData(ChatWordFilterAction.Block)]
    public void ACleanMessage_IsNotRejected_WhenMatchingIsPreempted(ChatWordFilterAction action)
    {
        // A backreference rules out the linear-time engine, so this rule backtracks under the timeout; the exponential input and a 1 ms budget
        // make the timeout fire every time, with no reliance on machine load.
        var ledger = new FaultLedger();
        var filter = new ChatWordFilter(
            [new ChatWordFilterRule(1, @"^(a+)+\1$", ChatWordFilterScope.Chat, action)],
            TimeSpan.FromMilliseconds(1),
            ledger);
        string clean = new string('a', 40) + "!";

        Assert.Equal(clean, filter.Apply(clean));
        Assert.Equal(1, ledger.Snapshot().Where(s => s.Site == "chat.word-filter-timeout").Sum(s => s.Count));
    }

    [Fact]
    public void LinearPatternsStillMatchAfterABacktrackingRuleTimesOut()
    {
        var ledger = new FaultLedger();
        var filter = new ChatWordFilter(
            [
                new ChatWordFilterRule(1, @"(a+)+\1$", ChatWordFilterScope.Chat, ChatWordFilterAction.Block),
                new ChatWordFilterRule(2, "dang", ChatWordFilterScope.Chat, ChatWordFilterAction.Censor),
            ],
            TimeSpan.FromMilliseconds(1),
            ledger);

        Assert.Equal("oh ****" + new string('a', 40) + "!", filter.Apply("oh dang" + new string('a', 40) + "!"));
    }

    [Fact]
    public void BlockRuleDropsTheMessageAndInvalidPatternsAreReported()
    {
        var filter = new ChatWordFilter(
        [
            new ChatWordFilterRule(1, "(", ChatWordFilterScope.Chat, ChatWordFilterAction.Block),
            new ChatWordFilterRule(2, @"gold\s*for\s*sale", ChatWordFilterScope.Chat, ChatWordFilterAction.Block),
        ]);
        Assert.Equal([1u], filter.Rejected.ToArray());
        Assert.Null(filter.Apply("cheap GOLD for sale"));
        Assert.Equal("hello", filter.Apply("hello"));
    }

    [Fact]
    public void NameScopeRulesJoinTheNameCatalogOnlyForNames()
    {
        var filter = new ChatWordFilter(
        [
            new ChatWordFilterRule(1, "^badname", ChatWordFilterScope.Names, ChatWordFilterAction.Censor),
            new ChatWordFilterRule(2, "both", ChatWordFilterScope.Chat | ChatWordFilterScope.Names, ChatWordFilterAction.Censor),
        ]);
        Assert.Equal(1, filter.ChatRuleCount);
        Assert.Equal(2, filter.NamePatterns.Length);
        Assert.Equal("badname", filter.Apply("badname"));

        NameCatalog catalog = NameCatalog.Empty.WithFilterProfane(filter.NamePatterns);
        Assert.Equal(NameCatalogResult.Profane, catalog.Check("Badnamex"));
        Assert.Equal(NameCatalogResult.Allowed, catalog.Check("Arthas"));
        Assert.Equal(NameCatalogResult.Profane, catalog.WithReservedExact(new HashSet<string>()).Check("Bothy"));
    }
}
