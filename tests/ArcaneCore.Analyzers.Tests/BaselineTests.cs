namespace ArcaneCore.Analyzers.Tests;

/// <summary>The baseline: covered findings are silent, extras are reported, unused allowances and malformed lines are errors of their own.</summary>
public sealed class BaselineTests
{
    private const string TwoWaits = """
        class C
        {
            int A(Task<int> t) => t.Result + t.Result;
        }
        """;

    [Fact]
    public void WithoutABaseline_EveryFindingIsReported()
    {
        Assert.Equal(["ARC0006", "ARC0006"], AnalyzerHarness.Ids(AnalyzerHarness.Analyze(TwoWaits)));
    }

    [Fact]
    public void ACoveringEntry_SilencesTheFinding()
    {
        var diagnostics = AnalyzerHarness.Analyze(TwoWaits, "ARC0006|src/Test.cs|C.A|Task.Result|2");
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void AnOccurrenceBeyondTheCount_IsReported()
    {
        var diagnostics = AnalyzerHarness.Analyze(TwoWaits, "ARC0006|src/Test.cs|C.A|Task.Result|1");
        Assert.Equal(["ARC0006"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void AnEntryForADifferentMember_DoesNotCover()
    {
        var diagnostics = AnalyzerHarness.Analyze(TwoWaits, "ARC0006|src/Test.cs|C.Other|Task.Result|2");
        Assert.Equal(["ARC0006", "ARC0006", "ARC9001"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void AnEntryAllowingMoreThanExists_IsStale()
    {
        var diagnostics = AnalyzerHarness.Analyze(TwoWaits, "ARC0006|src/Test.cs|C.A|Task.Result|3");
        Assert.Equal(["ARC9001"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void AnEntryForAFileOfAnotherProject_IsNotJudged()
    {
        var diagnostics = AnalyzerHarness.Analyze("class C { }", "ARC0006|src/Elsewhere/Other.cs|X.Y|Task.Result|2");
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void AnAssemblyEntry_SilencesTheAllowUnsafeBlocksFinding()
    {
        var diagnostics = AnalyzerHarness.Analyze("class C { }", "ARC0003|assembly:TestAssembly|<assembly>|AllowUnsafeBlocks|1", allowUnsafe: true);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void AnAssemblyEntry_WithoutAllowUnsafeBlocks_IsStale()
    {
        var diagnostics = AnalyzerHarness.Analyze("class C { }", "ARC0003|assembly:TestAssembly|<assembly>|AllowUnsafeBlocks|1", allowUnsafe: false);
        Assert.Equal(["ARC9001"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void AnAssemblyEntryForAnotherAssembly_IsNotJudged_AndDoesNotCover()
    {
        const string other = "ARC0003|assembly:OtherAssembly|<assembly>|AllowUnsafeBlocks|1";
        Assert.Empty(AnalyzerHarness.Analyze("class C { }", other, allowUnsafe: false));
        Assert.Equal(["ARC0003"], AnalyzerHarness.Ids(AnalyzerHarness.Analyze("class C { }", other, allowUnsafe: true)));
    }

    [Fact]
    public void MalformedLines_AreReported_AndCommentsAreNot()
    {
        var diagnostics = AnalyzerHarness.Analyze("class C { }", "# a comment\n\nnot a baseline line\nARC0006|src/Test.cs|C.A|Task.Result|zero");
        Assert.Equal(["ARC9002", "ARC9002"], AnalyzerHarness.Ids(diagnostics));
    }

    [Fact]
    public void Paths_AreRelativeToTheRepositoryRoot_WithForwardSlashes()
    {
        var diagnostics = AnalyzerHarness.Analyze(
            "class C { int A(Task<int> t) => t.Result; }",
            "ARC0006|src/Deep/Dir/Test.cs|C.A|Task.Result|1",
            sourcePath: "/repo/src\\Deep\\Dir\\Test.cs");
        Assert.Empty(diagnostics);

        var elsewhere = AnalyzerHarness.Analyze(
            "class C { int A(Task<int> t) => t.Result; }",
            "ARC0006|src/Test.cs|C.A|Task.Result|1",
            sourcePath: "/repo/src/Deep/Dir/Test.cs");
        Assert.Equal(["ARC0006"], AnalyzerHarness.Ids(elsewhere));
    }

    [Fact]
    public void TheDiagnosticMessage_CarriesTheLineToPasteIntoTheBaseline()
    {
        var diagnostics = AnalyzerHarness.Analyze("class C { int A(Task<int> t) => t.Result; }", "# empty baseline");
        string message = Assert.Single(diagnostics).GetMessage();
        Assert.Contains("Baseline line: ARC0006|src/Test.cs|C.A|Task.Result|1", message, StringComparison.Ordinal);
    }
}
