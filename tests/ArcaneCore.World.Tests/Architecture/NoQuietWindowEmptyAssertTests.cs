using System.Text.RegularExpressions;
using ArcaneCore.TestSupport;
using Xunit;

namespace ArcaneCore.World.Tests.Architecture;

/// <summary>
/// Forbids asserting emptiness of a fixed quiet-window collect: <c>Assert.Empty</c> over <c>await client.CollectAsync(quiet)</c> (or
/// any other <c>Collect*</c> read of a <c>WorldTestClient</c>). Such an assertion passes or fails on how long the server takes, not on
/// what it sends: on a loaded box a packet from enter-world (SMSG_CLEAR_FAR_SIGHT_IMMEDIATE, an update, an area packet) lands in the
/// window and fails it, and a reply slower than the window passes it vacuously. Use
/// <see cref="WorldTestClient.AssertNoPacketsAsync"/> (a world-queue barrier, then a filter on the opcodes under test) or
/// <see cref="WorldTestClient.CollectThroughBarrierAsync"/>.
/// </summary>
public sealed partial class NoQuietWindowEmptyAssertTests
{
    // The receiver is optional (a helper inside WorldTestClient calls CollectAsync() bare). Matches Assert.Empty(await x.Collect...(..)).
    [GeneratedRegex(@"Assert\s*\.\s*Empty\s*\(\s*await\s+(?:[\w.?!]+\s*\.\s*)?Collect\w*\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex QuietEmptyAssert();

    // The two-step form: var p = await x.Collect...(..); (up to six statements later) Assert.Empty(p).
    [GeneratedRegex(@"\bvar\s+(\w+)\s*=\s*await\s+(?:[\w.?!]+\s*\.\s*)?Collect\w*\s*\([^;]*;(?:(?!\bvar\s+\1\b)[^;]*;){0,6}?\s*Assert\s*\.\s*Empty\s*\(\s*\1\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex QuietEmptyAssertTwoStep();

    // Locate possible two-step assertions with a simple, bounded-cost expression first.
    // Running the backreference-heavy two-step regex over entire large test files can
    // exhaust its generated backtracking stack in CI.
    [GeneratedRegex(@"\bvar\s+\w+\s*=\s*await\s+(?:[\w.?!]+\s*\.\s*)?Collect\w*\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex CollectAssignment();

    // The fixed-window silence helper (it passed or failed on timing) was removed; neither a call nor a redefinition may return.
    [GeneratedRegex(@"\bAssertSilentAsync\b", RegexOptions.CultureInvariant)]
    private static partial Regex FixedWindowSilenceHelper();

    [Fact]
    public void NoTestAssertsEmptyOnAQuietWindowCollect()
    {
        string root = Path.Combine(RepositorySource.RequireRoot(), "tests");
        var offenders = new List<string>();
        var scanned = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            scanned.Add(relative);
            string text = File.ReadAllText(file);
            // This file names the removed helper in its own pattern and theory data.
            bool self = relative.EndsWith("Architecture/NoQuietWindowEmptyAssertTests.cs", StringComparison.Ordinal);
            Regex[] patterns = self ? [QuietEmptyAssert()] : [QuietEmptyAssert(), FixedWindowSilenceHelper()];
            foreach (Regex pattern in patterns)
            {
                foreach (Match match in pattern.Matches(text))
                {
                    int line = 1 + text.AsSpan(0, match.Index).Count('\n');
                    offenders.Add($"tests/{relative}:{line}");
                }
            }

            // Evaluate the expensive backreference pattern only on a bounded region
            // following each possible assignment, not on a multi-megabyte source file.
            foreach (Match start in CollectAssignment().Matches(text))
            {
                const int MaxTwoStepWindow = 8192;
                string window = text.Substring(start.Index, Math.Min(MaxTwoStepWindow, text.Length - start.Index));
                Match match = QuietEmptyAssertTwoStep().Match(window);
                if (match.Success && match.Index == 0)
                {
                    int line = 1 + text.AsSpan(0, start.Index).Count('\n');
                    offenders.Add($"tests/{relative}:{line}");
                }
            }
        }

        // A wrong root or enumeration would scan nothing and pass vacuously: the scan must have reached known files.
        Assert.True(scanned.Count > 100, $"the scan visited only {scanned.Count} files under {root}");
        Assert.Contains("ArcaneCore.World.Tests/WorldTestClient.cs", scanned);
        Assert.Contains("ArcaneCore.World.Tests/Teleport/TeleportRequirementWorldTests.cs", scanned);

        Assert.True(offenders.Count == 0,
            "Replace the fixed quiet-window emptiness assertion with WorldTestClient.AssertNoPacketsAsync(opcodes) or CollectThroughBarrierAsync: "
            + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("Assert." + "Empty(await CollectAsync());", true)]
    [InlineData("Assert." + "Empty(await client.CollectAsync(Quiet));", true)]
    [InlineData("Assert." + "Empty(await client.CollectAsync());", true)]
    [InlineData("Assert." + "Empty( await a.b.CollectFromAsync(WorldOpcode.X, Quiet));", true)]
    [InlineData("Assert." + "Empty(await client.CollectThroughBarrierAsync());", true)]
    [InlineData("Assert." + "Empty(await store.GetMailsAsync(1));", false)]
    [InlineData("await client.AssertNoPacketsAsync(WorldOpcode.SmsgMessagechat);", false)]
    public void ThePatternMatchesTheForbiddenShapeOnly(string line, bool expected)
        => Assert.Equal(expected, QuietEmptyAssert().IsMatch(line));

    [Theory]
    [InlineData("var p = await c.CollectAsync(Quiet);\n        Assert." + "Empty(p);", true)]
    [InlineData("var p = await CollectAsync();\n        Assert." + "Empty(p);", true)]
    [InlineData("var p = await c.CollectAsync(Quiet);\n        var q = 1;\n        Assert." + "Empty(p);", true)]
    [InlineData("var p = await c.CollectAsync(Quiet);\n        Assert." + "Equal(1, p.Count);\n        Assert." + "Empty(q);", false)]
    [InlineData("var p = await store.GetMailsAsync(1);\n        Assert." + "Empty(p);", false)]
    public void TheTwoStepPatternMatchesTheForbiddenShapeOnly(string text, bool expected)
        => Assert.Equal(expected, QuietEmptyAssertTwoStep().IsMatch(text));

    [Theory]
    [InlineData("await client.AssertSilent" + "Async(window);", true)]
    [InlineData("await client.AssertNoPacketsAsync(WorldOpcode.X);", false)]
    public void TheRemovedSilenceHelperIsRecognised(string line, bool expected)
        => Assert.Equal(expected, FixedWindowSilenceHelper().IsMatch(line));
}
