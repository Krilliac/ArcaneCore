using ArcaneCore.Data.Content.Names;
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
