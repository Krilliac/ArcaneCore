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
    [GeneratedRegex(@"Assert\s*\.\s*Empty\s*\(\s*await\s+[\w.?!]+\s*\.\s*Collect\w*\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex QuietEmptyAssert();

    [Fact]
    public void NoTestAssertsEmptyOnAQuietWindowCollect()
    {
        string root = Path.Combine(RepositorySource.RequireRoot(), "tests");
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            foreach (Match match in QuietEmptyAssert().Matches(text))
            {
                int line = 1 + text.AsSpan(0, match.Index).Count('\n');
                offenders.Add($"tests/{relative}:{line}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Replace the fixed quiet-window emptiness assertion with WorldTestClient.AssertNoPacketsAsync(opcodes) or CollectThroughBarrierAsync: "
            + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("Assert." + "Empty(await client.CollectAsync(Quiet));", true)]
    [InlineData("Assert." + "Empty(await client.CollectAsync());", true)]
    [InlineData("Assert." + "Empty( await a.b.CollectFromAsync(WorldOpcode.X, Quiet));", true)]
    [InlineData("Assert." + "Empty(await client.CollectThroughBarrierAsync());", true)]
    [InlineData("Assert." + "Empty(await store.GetMailsAsync(1));", false)]
    [InlineData("await client.AssertNoPacketsAsync(WorldOpcode.SmsgMessagechat);", false)]
    public void ThePatternMatchesTheForbiddenShapeOnly(string line, bool expected)
        => Assert.Equal(expected, QuietEmptyAssert().IsMatch(line));
}
