using Xunit;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>The golden-file harness itself, against a scratch directory (synthetic pages, no production page).</summary>
public sealed class DocsHarnessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcane-docs-" + Guid.NewGuid().ToString("N"));

    public DocsHarnessTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void MissingGolden_Fails_WithTheRegenerateCommand()
    {
        string? failure = DocsGolden.Compare(_root, "docs/reference/x.md", "a\n", update: false);
        Assert.NotNull(failure);
        Assert.Contains(DocsGolden.RegenerateCommand, failure, StringComparison.Ordinal);
        Assert.Contains("does not exist", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleGolden_Fails_NamingTheFirstDifferentLine()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllText(Path.Combine(_root, "docs", "x.md"), "one\ntwo\nthree\n");
        string? failure = DocsGolden.Compare(_root, "docs/x.md", "one\nTWO\nthree\n", update: false);
        Assert.NotNull(failure);
        Assert.Contains("line 2", failure, StringComparison.Ordinal);
        Assert.Contains("committed: two", failure, StringComparison.Ordinal);
        Assert.Contains("generated: TWO", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a\r\nb\r\n")]
    [InlineData("a\nb\n")]
    public void LineEndings_DoNotMatter(string committed)
    {
        File.WriteAllText(Path.Combine(_root, "p.md"), committed);
        Assert.Null(DocsGolden.Compare(_root, "p.md", "a\nb\n", update: false));
        Assert.Null(DocsGolden.Compare(_root, "p.md", "a\r\nb\r\n", update: false));
    }

    [Fact]
    public void Update_WritesTheRender_AsLf_AndThenCompareSucceeds()
    {
        Assert.Null(DocsGolden.Compare(_root, "new/dir/p.md", "x\r\ny\r\n", update: true));
        byte[] bytes = File.ReadAllBytes(Path.Combine(_root, "new", "dir", "p.md"));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Null(DocsGolden.Compare(_root, "new/dir/p.md", "x\ny\n", update: false));
    }

    [Fact]
    public void RepoRoot_FindsTheSolution_OrThrows()
    {
        Assert.True(File.Exists(Path.Combine(RepoRoot.Find(), "ArcaneCore.slnx")));
    }
}
