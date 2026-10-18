using System.Text.RegularExpressions;
using Xunit;

namespace ArcaneCore.World.Tests.Docs;

/// <summary>
/// Every <c>docs/...md</c> path (and <c>#anchor</c>) that code, scripts, project files and workflows cite resolves. When
/// this was added, 16 citations named five docs that had never existed in the repository. The checker is tested on a
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

    /// <summary>Files whose doc paths are synthetic inputs (scratch trees, changed-path fixtures), not pointers.</summary>
    private static readonly string[] SyntheticInputFiles =
    [
        "tests/ArcaneCore.Kernel.Tests/Configuration/Conf/ConfFixtureCatalog.cs",
        "tests/ArcaneCore.Kernel.Tests/Configuration/Conf/ConfWriterTests.cs",
        "tests/ArcaneCore.World.Tests/Docs/DocsHarnessTests.cs",
        "tests/ArcaneCore.World.Tests/Docs/CodeDocReferenceTests.cs",
        "tests/ArcaneCore.World.Tests/Ops/RedeployPlanTests.cs",
    ];

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
        var files = new List<string>();
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
        foreach (string file in Directory.EnumerateFiles(dir))
        {
            string name = Path.GetFileName(file);
            if (TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase) || name.Equals("Dockerfile", StringComparison.Ordinal))
            {
                files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
        }

        foreach (string sub in Directory.EnumerateDirectories(dir))
        {
            if (!SkippedDirectories.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
            {
                Collect(root, sub, files);
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

    internal static IReadOnlyList<string> FindBrokenCitations(string root, IReadOnlyDictionary<string, string> external, IReadOnlyCollection<string> syntheticFiles)
        => Check(root, CodeFiles(root).Where(f => !syntheticFiles.Contains(f)), external);

    /// <summary>The unresolved citations in <paramref name="files"/>, each as "file:line: problem".</summary>
    private static List<string> Check(string root, IEnumerable<string> files, IReadOnlyDictionary<string, string> external)
    {
        var problems = new List<string>();
        var anchorCache = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        HashSet<string> existing = ExistingDocs(root);
        foreach (string file in files)
        {
            foreach (DocCitation citation in Citations(file, File.ReadAllText(Path.Combine(root, file))))
            {
                if (external.ContainsKey(citation.Path))
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
        Write("tests/T/Fixture.cs", "\"docs/x.md\"\n");
        Write("src/A/notes.md", "docs/markdown-is-not-code.md\n");
        Write("src/A/Case.cs", "// docs/Areas/real.md\n");

        IReadOnlyList<string> problems = FindBrokenCitations(_root, new Dictionary<string, string> { ["docs/spec.md"] = "test" }, ["tests/T/Fixture.cs"]);

        Assert.Equal(4, problems.Count);
        Assert.Contains("src/A/A.cs:2: docs/areas/gone.md does not exist", problems);
        Assert.Contains("src/A/A.cs:3: no heading #nope in docs/areas/real.md", problems);
        Assert.Contains("src/A/Case.cs:1: docs/Areas/real.md does not exist", problems);
        Assert.Contains("tools/run.ps1:1: docs/ops/missing.md does not exist", problems);
    }

    [Fact]
    public void Repository_EveryDocCitedFromCodeExists()
    {
        string root = RepoRoot.Find();
        IReadOnlyList<string> files = CodeFiles(root);
        Assert.True(files.Count > 1000, "the checker must see the repository's code");
        int citations = files.Sum(f => Citations(f, File.ReadAllText(Path.Combine(root, f))).Count());
        Assert.True(citations > 500, $"the checker must see the code's doc citations (found {citations})");

        IReadOnlyList<string> problems = FindBrokenCitations(root, ExternalDocs, SyntheticInputFiles);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Exemptions_StayNeeded()
    {
        string root = RepoRoot.Find();
        foreach ((string path, string owner) in ExternalDocs)
        {
            Assert.False(File.Exists(Path.Combine(root, path)), $"{path} exists here now, so it is no longer only {owner}");
            Assert.Contains(CodeFiles(root), f => Citations(f, File.ReadAllText(Path.Combine(root, f))).Any(c => c.Path == path));
        }

        foreach (string file in SyntheticInputFiles)
        {
            Assert.True(File.Exists(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar))), $"{file} is exempt but no longer exists");
            Assert.True(Check(root, [file], ExternalDocs).Count > 0, $"{file} cites no synthetic doc path or anchor any more; drop its exemption");
        }
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_./\\-])(?<path>docs/[A-Za-z0-9_./-]*[A-Za-z0-9_-]\.md)(?:#(?<anchor>[A-Za-z0-9_-]+))?")]
    private static partial Regex DocPathRegex();
}
