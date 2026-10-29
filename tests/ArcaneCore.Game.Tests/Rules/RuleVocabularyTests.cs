using ArcaneCore.Game.Rules;
using Xunit;

namespace ArcaneCore.Game.Tests.Rules;

/// <summary>The primitive registry (design 5.2): reflection discovery, duplicate ids fail at startup, modules add but never override.</summary>
public sealed class RuleVocabularyTests
{
    [Fact]
    public void Default_HoldsTheSharedPrimitives_AndNoHost()
    {
        RuleVocabulary vocabulary = RuleVocabulary.Default;

        // Host lanes add their primitives to Default; the shared (any-host) set is what this test pins.
        Assert.Equal(
            ["action 1 set_phase", "action 2 start_rule_list", "action 3 say", "action 4 cast", "target 1 self", "target 2 invoker"],
            vocabulary.Primitives.Where(p => p.Host == RuleHostKind.Any).Select(p => $"{p.Kind.ToString().ToLowerInvariant()} {p.Id} {p.Name}"));
        Assert.All(vocabulary.Primitives, p => Assert.True(p.IsBuiltIn));
        Assert.All(vocabulary.Primitives, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
    }

    [Fact]
    public void Discovery_ReadsTheSignature_AndAHostSeesItsOwnPlusTheSharedPrimitives()
    {
        RuleVocabulary vocabulary = RuleTestKit.Vocabulary;

        RulePrimitiveInfo cast = vocabulary.FindAction(RuleTestKit.Kind, RuleTestKit.Cast)!.Info;
        Assert.Equal([("spell_id", RuleParamKind.SpellId), ("cast_flags", RuleParamKind.NonNegative)], cast.Parameters.Select(p => (p.Name, p.Kind)));
        Assert.NotNull(vocabulary.FindEvent(RuleTestKit.Kind, RuleTestKit.Ping));
        Assert.NotNull(vocabulary.FindAction(RuleTestKit.Kind, RuleTestKit.Record));
        Assert.Null(vocabulary.FindAction(RuleHostKind.Any, RuleTestKit.Record));
        Assert.Null(vocabulary.FindEvent(RuleHostKind.Bot, RuleTestKit.Ping));
        Assert.Equal("test", vocabulary.FindHost("TEST")!.Name);
        Assert.Contains(vocabulary.For(RuleTestKit.Kind), p => p.Name == "test.record");
        Assert.Contains(vocabulary.For(RuleTestKit.Kind), p => p.Name == "say");
    }

    [Fact]
    public void TwoClassesWithOneId_FailAtStartup()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RuleVocabulary.Discover([.. RuleTestKit.HostTypes, typeof(DuplicatePingEvent)]));

        Assert.Contains("declared twice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostIdThatRepeatsASharedId_FailsAtStartup()
    {
        Type[] types = [.. RuleVocabulary.TypesOf(typeof(RuleVocabulary).Assembly), .. RuleTestKit.HostTypes, typeof(ShadowingAction)];

        var error = Assert.Throws<InvalidOperationException>(() => RuleVocabulary.Discover(types));

        Assert.Contains("collides", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostPrimitiveWithoutItsHost_FailsAtStartup()
    {
        var error = Assert.Throws<InvalidOperationException>(() => RuleVocabulary.Discover([typeof(OrphanHostEvent)]));

        Assert.Contains("has no RuleHostDefinition", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AModule_AddsNewIds_ButNeverOverridesABuiltInOne()
    {
        RuleVocabulary extended = RuleTestKit.Vocabulary.WithModule([typeof(ModuleEchoAction)]);

        RulePrimitiveInfo echo = extended.FindAction(RuleTestKit.Kind, 101)!.Info;
        Assert.False(echo.IsBuiltIn);
        Assert.Null(RuleTestKit.Vocabulary.FindAction(RuleTestKit.Kind, 101));
        Assert.Contains(RuleReference.VocabularyLines(extended, RuleTestKit.Kind), l => l.Contains("test.module_echo", StringComparison.Ordinal) && l.Contains("[module]", StringComparison.Ordinal));

        var error = Assert.Throws<InvalidOperationException>(() => RuleTestKit.Vocabulary.WithModule([typeof(OverridingSetPhaseAction)]));
        Assert.Contains("cannot override", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => extended.WithModule([typeof(ModuleEchoAction)]));
    }
}

/// <summary>The stable share hash (design 5.4) is a fixed function: these values were computed independently of the C# code.</summary>
public sealed class RuleShareTests
{
    [Theory]
    [InlineData(0, 1, 1UL, 43)]
    [InlineData(0, 1, 2UL, 92)]
    [InlineData(7, 1, 1UL, 41)]
    [InlineData(0, 2, 1UL, 56)]
    [InlineData(12345, 1_000_000, 0x1234UL, 77)]
    [InlineData(-1, 42, 0xF130000000001F40UL, 97)]
    public void Bucket_IsPinned(int seed, int ruleId, ulong guid, int expected)
        => Assert.Equal(expected, RuleShare.Bucket(seed, ruleId, guid));

    [Fact]
    public void Selects_TreatsTheEdgesAsAllAndNone_AndSelectsRoughlyTheShare()
    {
        Assert.True(RuleShare.Selects(100, 5, 9, 123));
        Assert.False(RuleShare.Selects(0, 5, 9, 123));
        int selected = Enumerable.Range(1, 10_000).Count(g => RuleShare.Selects(30, 0, 7, (ulong)g));
        Assert.InRange(selected, 2_700, 3_300);
    }
}
