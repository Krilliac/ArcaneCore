using Xunit;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// Every relative link and heading anchor in every markdown file of the repository resolves. This is a ratchet: it was
/// clean when it was added and stays at zero. The checker is tested on a scratch tree first so a broken checker cannot report
/// a clean repository.
/// </summary>
public sealed class DocsLinkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcane-links-" + Guid.NewGuid().ToString("N"));

    public DocsLinkTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    internal static IReadOnlyList<string> FindBrokenLinks(string root)
    {
        var problems = new List<string>();
        var anchorCache = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (string file in MarkdownDocs.Files(root))
        {
            string text = File.ReadAllText(Path.Combine(root, file));
            foreach (MarkdownLink link in MarkdownDocs.Links(file, text))
            {
                string target = link.Target;
                if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int hash = target.IndexOf('#', StringComparison.Ordinal);
                string pathPart = hash >= 0 ? target[..hash] : target;
                string? fragment = hash >= 0 ? target[(hash + 1)..] : null;
                string resolvedRelative = pathPart.Length == 0
                    ? file
                    : Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(root, Path.GetDirectoryName(file)!.Replace('/', Path.DirectorySeparatorChar), Uri.UnescapeDataString(pathPart)))).Replace('\\', '/');
                string full = Path.Combine(root, resolvedRelative);
                if (!File.Exists(full) && !Directory.Exists(full))
                {
                    problems.Add($"{file}:{link.Line}: link target {target} does not exist");
                    continue;
                }

                if (!string.IsNullOrEmpty(fragment) && resolvedRelative.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                {
                    if (!anchorCache.TryGetValue(resolvedRelative, out IReadOnlySet<string>? anchors))
                    {
                        anchors = MarkdownDocs.Anchors(File.ReadAllText(full));
                        anchorCache[resolvedRelative] = anchors;
                    }

                    if (!anchors.Contains(Uri.UnescapeDataString(fragment).ToLowerInvariant()))
                    {
                        problems.Add($"{file}:{link.Line}: no heading #{fragment} in {resolvedRelative}");
                    }
                }
            }
        }

        return problems;
    }

    [Fact]
    public void Checker_FindsBrokenFilesAndAnchors_AndIgnoresCodeAndExternalLinks()
    {
        Write("a.md", "# Title\n\n## Second heading\n\n[ok](b.md) [ok2](b.md#target-one) [self](#second-heading) [dir](sub/)\n"
            + "[bad file](missing.md) [bad anchor](b.md#nope) [bad self](#nowhere)\n"
            + "`[in code](gone.md)` [web](https://example.com/x) \n```\n[fenced](gone2.md)\n```\n");
        Write("b.md", "## Target one\n\n## Target one\n\ntext\n");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Write("sub/c.md", "[up](../a.md#title) [bad up](../zzz.md)\n");

        IReadOnlyList<string> problems = FindBrokenLinks(_root);
        Assert.Equal(4, problems.Count);
        Assert.Contains(problems, p => p.Contains("a.md:6", StringComparison.Ordinal) && p.Contains("missing.md", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("#nope", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("#nowhere", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("sub/c.md:1", StringComparison.Ordinal) && p.Contains("zzz.md", StringComparison.Ordinal));
    }

    [Fact]
    public void Anchors_FollowTheGitHubRules()
    {
        IReadOnlySet<string> anchors = MarkdownDocs.Anchors("# `World:Chat` and more\n## A -- B (c)\n## Same\n## Same\n");
        Assert.Contains("worldchat-and-more", anchors);
        Assert.Contains("a----b-c", anchors);
        Assert.Contains("same", anchors);
        Assert.Contains("same-1", anchors);
    }

    [Fact]
    public void Repository_EveryRelativeLinkAndAnchorResolves()
    {
        string root = RepoRoot.Find();
        Assert.True(MarkdownDocs.Files(root).Count > 100, "the checker must see the repository's markdown");
        IReadOnlyList<string> problems = FindBrokenLinks(root);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void DocsIndex_LinksTheReferencePages_AndTheReadmeLinksTheIndex()
    {
        string root = RepoRoot.Find();
        string index = File.ReadAllText(Path.Combine(root, "docs", "README.md"));
        foreach (string page in Directory.EnumerateFiles(Path.Combine(root, "docs", "reference"), "*.md"))
        {
            Assert.Contains("reference/" + Path.GetFileName(page), index, StringComparison.Ordinal);
        }

        Assert.Contains("docs/README.md", File.ReadAllText(Path.Combine(root, "README.md")), StringComparison.Ordinal);
    }
}
