using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Web;

/// <summary>A fact that needs the <c>node</c> executable on PATH; skipped, visibly, without one.</summary>
public sealed class NodeFactAttribute : FactAttribute
{
    /// <summary>Marks the fact skipped when node cannot be found or the Web sources are not beside the test assembly's repository.</summary>
    public NodeFactAttribute()
    {
        if (WebTestEnvironment.SkipReason() is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// A fact that needs node and a real terrain extraction (<c>ARCANECORE_TEST_TERRAIN_DIR</c>, with maps/, vmaps/ and
/// mmaps/ from the 1.12.1 client). The variable is read-only input: the test never writes below it.
/// </summary>
public sealed class AtlasWebRealTerrainFactAttribute : FactAttribute
{
    /// <summary>The environment variable naming the extraction root (the same one the Game tests use).</summary>
    public const string Variable = "ARCANECORE_TEST_TERRAIN_DIR";

    /// <summary>The environment variable the real-data JS test reads for the temp directory the fact writes.</summary>
    public const string RealDataVariable = "ARCANECORE_ATLAS_REAL";

    /// <summary>Marks the fact skipped, with the reason, when node or the terrain extraction is missing.</summary>
    public AtlasWebRealTerrainFactAttribute()
    {
        if (WebTestEnvironment.SkipReason() is { } reason)
        {
            Skip = reason;
        }
        else if (RealTerrainRoot() is null)
        {
            Skip = $"{Variable} is not set to a terrain data root (maps/, vmaps/, mmaps/ from the 1.12.1 client).";
        }
    }

    /// <summary>The extraction root, or null when the variable is unset or has no maps/ folder.</summary>
    public static string? RealTerrainRoot()
    {
        string? dir = Environment.GetEnvironmentVariable(Variable);
        return string.IsNullOrWhiteSpace(dir) || !Directory.Exists(Path.Combine(dir, "maps")) ? null : dir;
    }
}

/// <summary>What node's TAP reporter said about one run of <c>node --test</c>.</summary>
/// <param name="ExitCode">Process exit code (0 only when every test passed).</param>
/// <param name="Output">Standard output (the TAP stream).</param>
/// <param name="Error">Standard error.</param>
internal sealed record NodeRun(int ExitCode, string Output, string Error)
{
    /// <summary>The number printed after <c># pass</c>, or -1 when the summary line is missing.</summary>
    public int Pass => Summary("pass");

    /// <summary>The number printed after <c># fail</c>, or -1 when the summary line is missing.</summary>
    public int Fail => Summary("fail");

    /// <summary>The number printed after <c># skipped</c>, or -1 when the summary line is missing.</summary>
    public int Skipped => Summary("skipped");

    /// <summary>The number printed after <c># tests</c>, or -1 when the summary line is missing.</summary>
    public int Tests => Summary("tests");

    /// <summary>True when a test of this exact name finished <c>ok</c> and was not skipped, at any nesting depth.</summary>
    public bool Passed(string name)
        => Regex.IsMatch(Output, @"^\s*ok \d+ - " + Regex.Escape(name) + @"\s*$", RegexOptions.Multiline);

    /// <summary>The run's text for an assertion message: the summary and any failing test blocks.</summary>
    public string Digest()
    {
        string[] all = Output.Split('\n');
        IEnumerable<string> flagged = all
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.TrimStart().StartsWith("not ok", StringComparison.Ordinal)
                || (l.StartsWith("# ", StringComparison.Ordinal) && !l.StartsWith("# Subtest", StringComparison.Ordinal)));
        // Each failing test's YAML block (from its "not ok" line to the closing "..."), which holds the assertion message.
        var failures = new List<string>();
        for (int i = 0; i < all.Length; i++)
        {
            if (!all[i].TrimStart().StartsWith("not ok", StringComparison.Ordinal))
            {
                continue;
            }

            for (int j = i; j < all.Length && j < i + 40; j++)
            {
                failures.Add(all[j].TrimEnd('\r'));
                if (j > i && all[j].Trim() == "...")
                {
                    break;
                }
            }
        }

        string tail = string.Join('\n', all.TakeLast(60));
        return $"exit {ExitCode}; {string.Join(" | ", flagged)}\n--- failing tests ---\n{string.Join('\n', failures)}\n--- last output ---\n{tail}\n--- stderr ---\n{Error}";
    }

    private int Summary(string key)
    {
        Match m = Regex.Match(Output, @"^# " + key + @" (\d+)\s*$", RegexOptions.Multiline);
        return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -1;
    }
}

/// <summary>Locates the Web sources, the node executable and the JS test files, and runs node's test runner.</summary>
internal static class WebTestEnvironment
{
    /// <summary>The Web sources, <c>tools/ArcaneCore.Atlas/Web</c>.</summary>
    public static string WebSources => Find(Path.Combine("tools", "ArcaneCore.Atlas", "Web"), "index.html")
        ?? throw new DirectoryNotFoundException("tools/ArcaneCore.Atlas/Web was not found above the test assembly or the test source folder.");

    /// <summary>The JS tests, <c>tests/ArcaneCore.Atlas.Tests/Web</c>.</summary>
    public static string WebTests => Find(Path.Combine("tests", "ArcaneCore.Atlas.Tests", "Web"), "imports.test.mjs")
        ?? throw new DirectoryNotFoundException("tests/ArcaneCore.Atlas.Tests/Web was not found above the test assembly or the test source folder.");

    /// <summary>Why the node-driven tests cannot run, or null when they can.</summary>
    public static string? SkipReason()
    {
        if (FindNode() is null)
        {
            return "node is not on PATH; the JS tests (golden parity with the C# core and the pure-module tests) were not run.";
        }

        if (Find(Path.Combine("tests", "ArcaneCore.Atlas.Tests", "Web"), "imports.test.mjs") is null)
        {
            return "the JS test sources are not beside this build (tests/ArcaneCore.Atlas.Tests/Web).";
        }

        return null;
    }

    /// <summary>The first <c>node</c> executable found on PATH, or null.</summary>
    public static string? FindNode()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string name = OperatingSystem.IsWindows() ? "node.exe" : "node";
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A PATH entry with characters the OS rejects cannot hold node.
            }
        }

        return null;
    }

    /// <summary>The JS test files in <see cref="WebTests"/> (<c>*.test.mjs</c>), by name, except those listed.</summary>
    public static IReadOnlyList<string> TestFiles(params string[] except)
        => Directory.GetFiles(WebTests, "*.test.mjs").Where(f => !except.Contains(Path.GetFileName(f), StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Runs <c>node --test --test-reporter=tap</c> on the files. The child inherits this process's environment plus
    /// <paramref name="environment"/> (a null value removes a variable). The whole process tree is killed on timeout.
    /// </summary>
    public static async Task<NodeRun> RunNodeTestsAsync(
        IReadOnlyList<string> files,
        IReadOnlyDictionary<string, string?> environment,
        TimeSpan timeout)
    {
        string node = FindNode() ?? throw new FileNotFoundException("node is not on PATH.");
        var info = new ProcessStartInfo(node)
        {
            WorkingDirectory = WebTests,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("--test");
        info.ArgumentList.Add("--test-reporter=tap");
        foreach (string file in files)
        {
            info.ArgumentList.Add(file);
        }

        foreach ((string key, string? value) in environment)
        {
            if (value is null)
            {
                info.Environment.Remove(key);
            }
            else
            {
                info.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = info };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"node --test did not finish within {timeout.TotalSeconds:F0} s. Output so far:\n{await stdout}\n{await stderr}");
        }

        return new NodeRun(process.ExitCode, await stdout, await stderr);
    }

    private static string? Find(string relative, string marker)
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory(), SourceDirectory() })
        {
            if (string.IsNullOrEmpty(start))
            {
                continue;
            }

            for (DirectoryInfo? dir = new(start); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(Path.Combine(candidate, marker)))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path) ?? string.Empty;
}
