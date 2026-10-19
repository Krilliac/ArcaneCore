using System.Text.RegularExpressions;
using Xunit;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// Every <c>docs/...md</c> path (and <c>#anchor</c>) that code, scripts, project files and workflows cite resolves. When
/// this was added, 12 citations named five docs that had never existed in the repository. The checker is tested on a
/// scratch tree first so a broken checker cannot report a clean repository.
/// </summary>
public sealed partial class CodeDocReferenceTests : IDisposable
{
    /// <summary>Directories under the root that hold code (markdown is <see cref="DocsLinkTests"/>' job).</summary>
    private static readonly string[] CodeDirectories = ["src", "tests", "tools", "scripts", "apps", "sql", ".github"];

    private static readonly string[] SkippedDirectories = ["bin", "obj", "node_modules", ".git", ".vs", "artifacts", "TestResults"];

    private static readonly string[] TextExtensions =
    [
        ".cs", ".csproj", ".props", ".targets", ".slnf", ".slnx", ".json", ".dist", ".ps1", ".psm1", ".psd1", ".sh", ".py",
        ".yml", ".yaml", ".sql", ".txt", ".cpp", ".js", ".ts", ".html", ".css", ".dart",
    ];

    /// <summary>Doc paths that name another project's documentation, with whose it is.</summary>
    private static readonly Dictionary<string, string> ExternalDocs = new(StringComparer.Ordinal)
    {
        ["docs/gguf.md"] = "ggml's GGUF specification (in the ggml repository)",
    };

    /// <summary>
    /// The doc citations each file uses as synthetic inputs (scratch trees, changed-path fixtures), not pointers, as
    /// written (path, or path#anchor). Only these are skipped, so a real pointer in the same file is still checked.
    /// </summary>
    private static readonly Dictionary<string, string[]> SyntheticCitations = new(StringComparer.Ordinal)
    {
        ["tests/ArcaneCore.Kernel.Tests/Configuration/Conf/ConfFixtureCatalog.cs"] = ["docs/reference/configuration.md#fixture"],
        ["tests/ArcaneCore.Kernel.Tests/Configuration/Conf/ConfWriterTests.cs"] = ["docs/reference/configuration.md#fixture"],
        ["tests/ArcaneCore.World.Tests/Docs/DocsHarnessTests.cs"] = ["docs/reference/x.md", "docs/x.md"],
        ["tests/ArcaneCore.World.Tests/Ops/RedeployPlanTests.cs"] = ["docs/x.md", "docs/a.md"],
    };

    /// <summary>Files the checker does not read: this one, whose scratch trees cite made-up docs throughout.</summary>
    private static readonly string[] UnreadFiles = ["tests/ArcaneCore.World.Tests/Docs/CodeDocReferenceTests.cs"];

    private static readonly Dictionary<string, string[]> NoSyntheticCitations = new(StringComparer.Ordinal);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcane-codedocs-" + Guid.NewGuid().ToString("N"));

    public CodeDocReferenceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>A cited doc path: where it is cited, the path, and the anchor (null when none).</summary>
    internal sealed record DocCitation(string File, int Line, string Path, string? Anchor);

    /// <summary>Repo-relative, '/'-separated paths of the code files the checker reads.</summary>
    internal static IReadOnlyList<string> CodeFiles(string root)
    {
        // The root's own files cite docs too (Directory.Build.props, docker-compose.yml, .codacy.yml); below the root
        // only CodeDirectories are walked.
        var files = new List<string>();
        AddCodeFiles(root, root, files);
        foreach (string dir in CodeDirectories)
        {
            string full = Path.Combine(root, dir);
            if (Directory.Exists(full))
            {
                Collect(root, full, files);
            }
        }

        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static void Collect(string root, string dir, List<string> files)
    {
        AddCodeFiles(root, dir, files);
        foreach (string sub in Directory.EnumerateDirectories(dir))
        {
            if (!SkippedDirectories.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
            {
                Collect(root, sub, files);
            }
        }
    }

    /// <summary>Adds the code files directly in <paramref name="dir"/> (not its subdirectories).</summary>
    private static void AddCodeFiles(string root, string dir, List<string> files)
    {
        foreach (string file in Directory.EnumerateFiles(dir))
        {
            string name = Path.GetFileName(file);
            if (TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) || name.Equals("Dockerfile", StringComparison.Ordinal))
            {
                files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
        }
    }

    internal static IEnumerable<DocCitation> Citations(string file, string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            foreach (Match m in DocPathRegex().Matches(lines[i]))
            {
                yield return new DocCitation(file, i + 1, m.Groups["path"].Value, m.Groups["anchor"].Success ? m.Groups["anchor"].Value : null);
            }
        }
    }

    /// <summary>The docs that exist, compared with their exact case (CI's file system is case-sensitive; Windows' is not).</summary>
    private static HashSet<string> ExistingDocs(string root)
    {
        string docs = Path.Combine(root, "docs");
        return Directory.Exists(docs)
            ? new HashSet<string>(Directory.EnumerateFiles(docs, "*.md", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')), StringComparer.Ordinal)
            : [];
    }

    internal static IReadOnlyList<string> FindBrokenCitations(
        string root, IReadOnlyDictionary<string, string> external, IReadOnlyDictionary<string, string[]> synthetic, IReadOnlyCollection<string> unreadFiles)
        => Check(root, CodeFiles(root).Where(f => !unreadFiles.Contains(f)), external, synthetic);

    /// <summary>A citation as written: its path, then <c>#anchor</c> when it has one.</summary>
    private static string AsWritten(DocCitation citation) => citation.Anchor is null ? citation.Path : citation.Path + "#" + citation.Anchor;

    /// <summary>
    /// The unresolved citations in <paramref name="files"/>, each as "file:line: problem", leaving out the
    /// <paramref name="synthetic"/> citations each file lists.
    /// </summary>
    private static List<string> Check(
        string root, IEnumerable<string> files, IReadOnlyDictionary<string, string> external, IReadOnlyDictionary<string, string[]> synthetic)
    {
        var problems = new List<string>();
        var anchorCache = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        HashSet<string> existing = ExistingDocs(root);
        foreach (string file in files)
        {
            string[] inputs = synthetic.GetValueOrDefault(file) ?? [];
            foreach (DocCitation citation in Citations(file, File.ReadAllText(Path.Combine(root, file))))
            {
                if (external.ContainsKey(citation.Path) || inputs.Contains(AsWritten(citation), StringComparer.Ordinal))
                {
                    continue;
                }

                if (!existing.Contains(citation.Path))
                {
                    problems.Add($"{file}:{citation.Line}: {citation.Path} does not exist");
                    continue;
                }

                if (citation.Anchor is not null)
                {
                    if (!anchorCache.TryGetValue(citation.Path, out IReadOnlySet<string>? anchors))
                    {
                        anchors = MarkdownDocs.Anchors(File.ReadAllText(Path.Combine(root, citation.Path.Replace('/', Path.DirectorySeparatorChar))));
                        anchorCache[citation.Path] = anchors;
                    }

                    if (!anchors.Contains(citation.Anchor.ToLowerInvariant()))
                    {
                        problems.Add($"{file}:{citation.Line}: no heading #{citation.Anchor} in {citation.Path}");
                    }
                }
            }
        }

        return problems;
    }

    [Fact]
    public void Checker_FindsMissingDocsAndAnchors_AndSkipsExternalAndSyntheticPaths()
    {
        Write("docs/areas/real.md", "# Real\n\n## Limits\n");
        Write("src/A/A.cs", "/// See docs/areas/real.md and docs/areas/real.md#limits.\n// docs/areas/gone.md\nvar x = \"docs/areas/real.md#nope\";\n");
        Write("tools/run.ps1", "# Usage: docs/ops/missing.md\n");
        Write("src/A/obj/Gen.cs", "// docs/never-read.md\n");
        Write("src/A/B.cs", "// https://example.com/docs/web.md ../docs/areas/real.md ggml's docs/spec.md\n");
        Write("tests/T/Fixture.cs", "\"docs/x.md\"\n\"docs/areas/real.md#fixture\"\n/// docs/ops/stale.md\n");
        Write("tests/T/Scratch.cs", "// docs/never.md\n");
        Write("src/A/notes.md", "docs/markdown-is-not-code.md\n");
        Write("src/A/Case.cs", "// docs/Areas/real.md\n");
        Write("Directory.Build.props", "<!-- docs/guide/gone.md -->\n");

        IReadOnlyList<string> problems = FindBrokenCitations(
            _root,
            new Dictionary<string, string> { ["docs/spec.md"] = "test" },
            new Dictionary<string, string[]> { ["tests/T/Fixture.cs"] = ["docs/x.md", "docs/areas/real.md#fixture"] },
            ["tests/T/Scratch.cs"]);

        Assert.Equal(6, problems.Count);
        Assert.Contains("src/A/A.cs:2: docs/areas/gone.md does not exist", problems);
        Assert.Contains("src/A/A.cs:3: no heading #nope in docs/areas/real.md", problems);
        Assert.Contains("src/A/Case.cs:1: docs/Areas/real.md does not exist", problems);
        Assert.Contains("tools/run.ps1:1: docs/ops/missing.md does not exist", problems);
        Assert.Contains("tests/T/Fixture.cs:3: docs/ops/stale.md does not exist", problems);
        Assert.Contains("Directory.Build.props:1: docs/guide/gone.md does not exist", problems);
    }

    [Fact]
    public void Repository_EveryDocCitedFromCodeExists()
    {
        string root = RepoRoot.Find();
        IReadOnlyList<string> files = CodeFiles(root);
        Assert.True(files.Count > 1000, "the checker must see the repository's code");
        int citations = files.Sum(f => Citations(f, File.ReadAllText(Path.Combine(root, f))).Count());
        Assert.True(citations > 500, $"the checker must see the code's doc citations (found {citations})");

        IReadOnlyList<string> problems = FindBrokenCitations(root, ExternalDocs, SyntheticCitations, UnreadFiles);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Exemptions_StayNeeded()
    {
        string root = RepoRoot.Find();
        IReadOnlyList<string> readFiles = [.. CodeFiles(root).Where(f => !UnreadFiles.Contains(f))];
        foreach ((string path, string owner) in ExternalDocs)
        {
            Assert.False(File.Exists(Path.Combine(root, path)), $"{path} exists here now, so it is no longer only {owner}");
            Assert.True(
                readFiles.Any(f => Citations(f, File.ReadAllText(Path.Combine(root, f))).Any(c => c.Path == path)),
                $"no code cites {path} any more (this file's own table does not count); drop its {nameof(ExternalDocs)} entry");
        }

        foreach ((string file, string[] inputs) in SyntheticCitations)
        {
            Assert.True(File.Exists(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar))), $"{file} is exempt but no longer exists");
            foreach (string input in inputs)
            {
                var others = new Dictionary<string, string[]>(StringComparer.Ordinal) { [file] = [.. inputs.Where(i => i != input)] };
                Assert.True(
                    Check(root, [file], ExternalDocs, others).Count > 0,
                    $"{file} no longer cites the synthetic {input}, or it resolves now; drop it from {nameof(SyntheticCitations)}");
            }
        }

        foreach (string file in UnreadFiles)
        {
            Assert.True(File.Exists(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar))), $"{file} is unread but no longer exists");
            Assert.True(
                Check(root, [file], ExternalDocs, NoSyntheticCitations).Count > 0,
                $"{file} cites no synthetic doc path or anchor any more; drop it from {nameof(UnreadFiles)}");
        }
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_./\\-])(?<path>docs/[A-Za-z0-9_./-]*[A-Za-z0-9_-]\.md)(?:#(?<anchor>[A-Za-z0-9_-]+))?")]
    private static partial Regex DocPathRegex();
}
