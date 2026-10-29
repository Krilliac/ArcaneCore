using ArcaneCore.Game.Rules;
using Xunit;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>docs/reference/rules.md is generated from the rule vocabulary (design 5.2), so it lists exactly what <c>.rules vocab</c> prints.</summary>
public sealed class RuleReferenceTests
{
    [Fact]
    public void RulesPage_MatchesTheVocabulary() =>
        DocsGolden.Verify("docs/reference/rules.md", RuleReference.RenderMarkdown(RuleVocabulary.Default));

    [Fact]
    public void RulesPage_NamesEverySharedPrimitive()
    {
        string page = RuleReference.RenderMarkdown(RuleVocabulary.Default);
        Assert.All(RuleVocabulary.Default.Primitives, p => Assert.Contains($"`{p.Name}`", page, StringComparison.Ordinal));
    }
}
