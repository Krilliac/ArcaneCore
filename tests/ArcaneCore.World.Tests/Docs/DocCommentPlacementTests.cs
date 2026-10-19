using Xunit;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// The compiler gives a doc comment to whatever member follows it. A member inserted between a comment and the member it documents
/// takes the comment over, and when the inserted member brings its own comment the two stack into one with two summaries, the first
/// about another member (the build sees nothing wrong unless a <c>paramref</c> in it names a parameter the new owner lacks).
/// </summary>
public sealed class DocCommentPlacementTests
{
    [Fact]
    public void NoDocComment_HasTwoSummaries()
    {
        string root = RepoRoot.Find();
        int files = 0;
        var stacked = new List<string>();
        foreach (string top in (string[])["src", "tests"])
        {
            foreach (string path in Directory.EnumerateFiles(Path.Combine(root, top), "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.StartsWith("tests/ThirdParty/", StringComparison.Ordinal)
                    || relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
                    continue;
                files++;
                stacked.AddRange(StackedSummaryLines(File.ReadAllLines(path)).Select(line => $"{relative}:{line}"));
            }
        }

        Assert.True(files > 1000, $"scanned only {files} source files under src/ and tests/");
        Assert.True(stacked.Count == 0, "a doc comment with a second <summary> (the first one documents another member): " + string.Join(", ", stacked));
    }

    [Fact]
    public void TheScan_FindsAStackedSummary_AndPassesSeparateOnes()
    {
        string[] stacked =
        [
            "    /// <summary>Runs <paramref name=\"action\"/> later.</summary>",
            "    /// <summary>Pending actions.</summary>",
            "    public int PendingCount => 0;",
        ];
        string[] withBlank =
        [
            "    /// <summary>Runs <paramref name=\"action\"/> later.</summary>",
            "",
            "    /// <summary>Pending actions.</summary>",
            "    public int PendingCount => 0;",
        ];
        string[] withComment =
        [
            "    /// <summary>Runs <paramref name=\"action\"/> later.</summary>",
            "    // A note between the two comments.",
            "    /// <summary>Pending actions.</summary>",
            "    public int PendingCount => 0;",
        ];
        string[] withAttribute =
        [
            "    /// <summary>Runs <paramref name=\"action\"/> later.</summary>",
            "    [Fact]",
            "    /// <summary>Pending actions.</summary>",
            "    public void Test() { }",
        ];
        string[] separate =
        [
            "    /// <summary>Pending actions.</summary>",
            "    public int PendingCount => 0;",
            "",
            "    /// <summary>",
            "    /// Runs <paramref name=\"action\"/> later.",
            "    /// </summary>",
            "    public void Schedule(Action action) { }",
        ];

        Assert.Equal(2, Assert.Single(StackedSummaryLines(stacked)));
        Assert.Equal(3, Assert.Single(StackedSummaryLines(withBlank)));
        Assert.Equal(3, Assert.Single(StackedSummaryLines(withComment)));
        Assert.Equal(3, Assert.Single(StackedSummaryLines(withAttribute)));
        Assert.Empty(StackedSummaryLines(separate));
    }

    /// <summary>
    /// The 1-based line of each <c>&lt;summary&gt;</c> after the first before a member declaration (blank, <c>//</c> and <c>[...]</c>
    /// attribute lines do not end the run).
    /// </summary>
    private static IEnumerable<int> StackedSummaryLines(string[] lines)
    {
        int summaries = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("///", StringComparison.Ordinal))
            {
                if (trimmed.Contains("<summary>", StringComparison.Ordinal) && ++summaries > 1)
                    yield return i + 1;
                continue;
            }

            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('['))
                continue;

            summaries = 0;
        }
    }
}
