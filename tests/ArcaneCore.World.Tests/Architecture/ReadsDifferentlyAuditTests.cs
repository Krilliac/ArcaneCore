using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Architecture;

/// <summary>
/// The "reads differently than it does" audit (docs/ops/tooling.md, tools/ops/quality). <see cref="ReadsDifferentlyScanner"/> lists
/// four constructs in src/: RB001 <c>is not A or B</c> / <c>is not A or not B</c>, RB002 a float to integer cast in packet code, RB003 <c>??</c> mixed with
/// another operator without parentheses, RB004 <c>async void</c> that is not an event handler. Every hit that exists today is in
/// ReadsDifferentlyBaseline.txt with a one-line reason; a new hit fails <see cref="EveryHit_IsInTheReviewedBaseline"/> until it is
/// fixed or added there with its reason, and a baseline line whose construct is gone fails it too (delete the line).
/// To print the current hits as baseline lines, set ARCANECORE_READS_DIFFERENTLY_DUMP to an output file path and run that test.
/// </summary>
public sealed class ReadsDifferentlyAuditTests(ITestOutputHelper output)
{
    private const string BaselinePath = "tests/ArcaneCore.World.Tests/Architecture/ReadsDifferentlyBaseline.txt";
    private const string AssemblyName = "ArcaneCore.World.Tests";

    [Fact]
    public void EveryHit_IsInTheReviewedBaseline()
    {
        string root = RepositorySource.RequireRoot();
        var scanner = new ReadsDifferentlyScanner(root, AssemblyName);
        scanner.Load();
        List<ReadsDifferentlyHit> hits = scanner.Scan();
        output.WriteLine($"files {scanner.FilesScanned}, packet-scope files {scanner.PacketScopeFiles}, integer casts classified {scanner.CastsClassified}, unresolved {scanner.Unresolved} (casts {scanner.UnresolvedCasts}, async lambdas {scanner.UnresolvedLambdas} of {scanner.AsyncLambdas}), syntax errors {scanner.SyntaxErrors.Count}, hits {hits.Count}");

        string? dump = Environment.GetEnvironmentVariable("ARCANECORE_READS_DIFFERENTLY_DUMP");
        if (!string.IsNullOrEmpty(dump))
        {
            File.WriteAllLines(dump, hits.Select(h => $"{h.BaseKey} | <reason>  (line {h.Line})"));
        }

        // C# the pinned Roslyn cannot read leaves holes in the tree and silently hides constructs: fail loudly instead.
        Assert.True(scanner.SyntaxErrors.Count == 0,
            $"{scanner.SyntaxErrors.Count} syntax error(s) in src/ (syntax newer than the Roslyn package of this test project?):\n  " + string.Join("\n  ", scanner.SyntaxErrors.Take(20)));

        // A scan that read nothing, or could not tell what the casts are, proves nothing.
        Assert.True(scanner.FilesScanned > 1000, $"only {scanner.FilesScanned} source files were found under src/");
        Assert.True(scanner.PacketScopeFiles >= 20, $"only {scanner.PacketScopeFiles} files are in the packet scope");
        Assert.True(scanner.CastsClassified >= 100, $"only {scanner.CastsClassified} integer casts in the packet scope were classified");
        Assert.True(scanner.UnresolvedCasts * 4 <= scanner.CastsClassified + scanner.UnresolvedCasts,
            $"{scanner.UnresolvedCasts} of {scanner.CastsClassified + scanner.UnresolvedCasts} integer casts in the packet scope could not be typed (references missing?)");
        Assert.True(scanner.AsyncLambdas >= 10, $"only {scanner.AsyncLambdas} async lambdas were looked at");
        Assert.True(scanner.UnresolvedLambdas * 4 <= scanner.AsyncLambdas,
            $"{scanner.UnresolvedLambdas} of {scanner.AsyncLambdas} async lambdas had no resolved delegate type (references missing?); first sites:\n  " + string.Join("\n  ", scanner.UnresolvedLambdaSites.Take(15)));

        Dictionary<string, int> baseline = ParseBaseline(File.ReadAllLines(Path.Combine(root, BaselinePath)));
        Dictionary<string, int> found = hits.GroupBy(h => h.BaseKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        string[] fresh = [.. found.Where(f => f.Value > baseline.GetValueOrDefault(f.Key)).Select(f => $"{f.Key} | <reason>" + (f.Value > 1 ? $"   (x{f.Value - baseline.GetValueOrDefault(f.Key)} more)" : string.Empty))];
        string[] stale = [.. baseline.Where(b => b.Value > found.GetValueOrDefault(b.Key)).Select(b => b.Key)];
        Assert.True(fresh.Length == 0 && stale.Length == 0,
            $"{fresh.Length} construct(s) that read differently than they do are not in {BaselinePath} (fix the code, or add a line with the reason):\n  "
            + string.Join("\n  ", fresh)
            + $"\n{stale.Length} baseline line(s) whose construct is gone (delete them):\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void Baseline_EveryLineHasAKnownRuleAndAReason()
    {
        string[] lines = File.ReadAllLines(Path.Combine(RepositorySource.RequireRoot(), BaselinePath));
        Dictionary<string, int> entries = ParseBaseline(lines);
        Assert.NotEmpty(entries);
        foreach (string key in entries.Keys)
        {
            Assert.Matches(@"^RB00[1-4] \| src/", key);
        }
    }

    [Fact]
    public void Scanner_ReportsEachConstruct_AndSparesTheirClearForms()
    {
        string root = Path.Combine(Path.GetTempPath(), "arcane-reads-" + Guid.NewGuid().ToString("N"));
        string dir = Path.Combine(root, "src", "ArcaneCore.Fake", "Net");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Sample.cs"), """
                using System;
                using System.Threading.Tasks;
                namespace Fake.Net;
                public sealed class Sample
                {
                    public bool NotOr(object o) => o is not string or int;
                    public bool NotAnd(object o) => o is not string and not int;
                    public bool NotNull(object? o) => o is not null and not "";
                    public bool Grouped(object o) => o is not (string or int);
                    public int CoalesceCompare(int? a, int b, int c) => (a ?? b < c) ? 1 : 0;
                    public int CoalesceSum(int? a, int b, int c) => a ?? b + c;
                    public int CoalesceClear(int? a, int b, int c) => (a ?? b) + c;
                    public string AsLeft(object? o) => o as string ?? "x";
                    public string? AsBoth(object? o, object? p) => o as string ?? p as string;
                    public string? AsRight(string? o, object? p) => o ?? p as string;
                    public byte Truncates(float f) => (byte)(f * 2.5f);
                    public byte Rounds(float f) => (byte)MathF.Round(f * 2.5f);
                    public int FromInt(long l) => (int)l;
                    public async void FireAndForget() { await Task.Yield(); }
                    public async void OnTick(object sender, EventArgs e) { await Task.Yield(); }
                    public async Task Proper() { await Task.Yield(); }
                    public void Run() { Action a = async () => await Task.Yield(); Func<Task> f = async () => await Task.Yield(); a(); _ = f; }
                    public bool NotOrNot(object o) => o is not string or not int;
                    public bool CoalesceIs(bool? a, object? b) => a ?? b is null;
                    public bool CoalesceNotNull(bool? a, object? b) => a ?? b is not null;
                    public bool CoalesceBrace(bool? a, object? b) => a ?? b is { };
                    public bool CoalesceParen(object? a, object? b) => (a ?? b) is null;
                    public void Unknown() { Mystery m = async () => await Task.Yield(); _ = m; }
                }
                """);
            var scanner = new ReadsDifferentlyScanner(root, AssemblyName);
            scanner.Load();
            string[] found = [.. scanner.Scan().Select(h => $"{h.Rule} {h.Member}")];
            Assert.Equal(
            [
                "RB001 Sample.NotOr",
                "RB003 Sample.CoalesceCompare",
                "RB003 Sample.CoalesceSum",
                "RB003 Sample.AsRight",
                "RB002 Sample.Truncates",
                "RB004 Sample.FireAndForget",
                "RB004 Sample.Run",
                "RB001 Sample.NotOrNot",
                "RB003 Sample.CoalesceIs",
                "RB003 Sample.CoalesceNotNull",
                "RB003 Sample.CoalesceBrace",
            ],
            found);
            Assert.Empty(scanner.SyntaxErrors);
            Assert.Equal(1, scanner.UnresolvedLambdas); // 'Mystery' does not exist: the lambda's delegate type is unknown, which must show
            Assert.Equal(3, scanner.AsyncLambdas);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Scanner_ListsSyntaxErrors_SoUnreadableCodeFailsLoudly()
    {
        string root = Path.Combine(Path.GetTempPath(), "arcane-reads-" + Guid.NewGuid().ToString("N"));
        string dir = Path.Combine(root, "src", "ArcaneCore.Fake");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Broken.cs"), "namespace Fake; public sealed class Broken { public int M( => ; }");
            var scanner = new ReadsDifferentlyScanner(root, AssemblyName);
            scanner.Load();
            Assert.NotEmpty(scanner.SyntaxErrors);
            Assert.Contains("src/ArcaneCore.Fake/Broken.cs(1)", scanner.SyntaxErrors[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>"RULE | file | member | snippet | reason" lines → key (the first four fields) with how often it is listed. '#' lines and blanks are skipped.</summary>
    private static Dictionary<string, int> ParseBaseline(string[] lines)
    {
        var entries = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int reasonAt = line.LastIndexOf(" | ", StringComparison.Ordinal);
            Assert.True(reasonAt > 0, $"{BaselinePath}:{i + 1}: expected 'RULE | file | member | snippet | reason'");
            string reason = line[(reasonAt + 3)..].Trim();
            string key = line[..reasonAt];
            Assert.True(reason.Length > 0 && reason != "<reason>", $"{BaselinePath}:{i + 1}: the line has no reason");
            Assert.True(key.Split(" | ").Length >= 4, $"{BaselinePath}:{i + 1}: expected 'RULE | file | member | snippet | reason'");
            entries[key] = entries.GetValueOrDefault(key) + 1;
        }

        return entries;
    }
}
