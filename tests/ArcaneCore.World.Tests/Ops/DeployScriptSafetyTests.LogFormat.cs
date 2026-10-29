using Xunit;

namespace ArcaneCore.World.Tests.Ops;

public sealed partial class DeployScriptSafetyTests
{
    /// <summary>
    /// deploy-live.py reads the daemon log back as text lines (" WARN ", "Started managed playerbot"). A daemon writes ONE log file, in the
    /// format Logging:ArcaneCore:Format names, so in visible-console mode the script must pin Format=Text, switch the file on, give it the
    /// per-daemon path, and neither assign nor inherit the deprecated Json:Path alias (a stale one would disagree with File:Path).
    /// </summary>
    [Fact]
    public void DeployLive_VisibleConsoles_PinsTheTextFormat_AndOnlyTheFilePath()
    {
        string[] lines = File.ReadAllLines(DeployLive);
        int block = Array.FindIndex(lines, l => l.Trim() == "if args.visible_consoles:");
        Assert.True(block >= 0, "deploy-live.py no longer has an 'if args.visible_consoles:' block");

        string[] body = [.. lines.Skip(block + 1).TakeWhile(l => l.Trim().Length == 0 || l.StartsWith(new string(' ', lines[block].Length - lines[block].TrimStart().Length + 1), StringComparison.Ordinal))
            .Select(l => l.Trim()).Where(l => !l.StartsWith('#'))];

        string[] expected =
        [
            "env[\"Logging__ArcaneCore__Format\"] = \"Text\"",
            "env[\"Logging__ArcaneCore__File__Enabled\"] = \"true\"",
            "env[\"Logging__ArcaneCore__File__Path\"] = str(log_path(name))",
        ];
        int previous = -1;
        foreach (string line in expected)
        {
            int at = Array.IndexOf(body, line);
            Assert.True(at > previous, $"deploy-live.py visible-console block lacks (in this order): {line}");
            previous = at;
        }

        // One file, one path key: the deprecated Json:Path alias is never assigned, and a value inherited from the caller is dropped.
        Assert.DoesNotContain(body, l => l.StartsWith("env[\"Logging__ArcaneCore__Json__Path\"] =", StringComparison.Ordinal));
        Assert.Contains(body, l => l.StartsWith("env.pop(\"Logging__ArcaneCore__Json__Path\"", StringComparison.Ordinal));

        // Json:Enabled stays the profile's business; the Text pin makes the legacy switch irrelevant and nothing should force a second file on.
        Assert.DoesNotContain(body, l => l.Contains("Json__Enabled", StringComparison.Ordinal));
    }

    /// <summary>
    /// server-status.ps1 -Supervise restarts a service with its own log file. The restart sets the format explicitly (the file's extension and
    /// content must agree), never assigns a Json:Path (a null restore is not an assignment of a value), and puts Format back in its finally.
    /// </summary>
    [Fact]
    public void ServerStatus_SupervisedRestart_SetsFormat_NeverAssignsJsonPath_AndRestoresFormat()
    {
        string[] lines = File.ReadAllLines(Path.Combine(Root, "tools", "ops", "diag", "server-status.ps1"));
        int fn = Array.FindIndex(lines, l => l.StartsWith("function Restart-SupervisedService", StringComparison.Ordinal));
        Assert.True(fn >= 0, "server-status.ps1 no longer defines Restart-SupervisedService");
        int end = Array.FindIndex(lines, fn + 1, l => l.StartsWith("function ", StringComparison.Ordinal));
        string[] body = [.. lines.Skip(fn).Take((end < 0 ? lines.Length : end) - fn).Select(l => l.Trim()).Where(l => !l.StartsWith('#'))];

        int tryAt = Array.FindIndex(body, l => l.StartsWith("try {", StringComparison.Ordinal) || l == "try");
        int finallyAt = Array.FindIndex(body, l => l.Contains("} finally {", StringComparison.Ordinal) || l == "finally {");
        Assert.True(tryAt >= 0 && finallyAt > tryAt, "Restart-SupervisedService lost its try/finally environment restore");

        string[] inTry = body[tryAt..finallyAt];
        string[] inFinally = body[finallyAt..];
        Assert.Contains(inTry, l => l.StartsWith("$env:Logging__ArcaneCore__Format =", StringComparison.Ordinal));
        Assert.Contains(inTry, l => l.StartsWith("$env:Logging__ArcaneCore__File__Path =", StringComparison.Ordinal));
        Assert.Contains(body, l => l.StartsWith("$envPrevFormat = $env:Logging__ArcaneCore__Format", StringComparison.Ordinal));
        Assert.Contains(inFinally, l => l.StartsWith("$env:Logging__ArcaneCore__Format = $envPrevFormat", StringComparison.Ordinal));

        // Json__Path may only be cleared or restored to the caller's previous value, never assigned the service's log path.
        Assert.DoesNotContain(body, l => l.StartsWith("$env:Logging__ArcaneCore__Json__Path =", StringComparison.Ordinal)
            && !l.Contains("$null", StringComparison.Ordinal) && !l.Contains("$envPrevJsonPath", StringComparison.Ordinal) && !l.EndsWith("= ''", StringComparison.Ordinal) && !l.Contains("Remove-Item", StringComparison.Ordinal));
        Assert.DoesNotContain(body, l => l.Contains("$logFile", StringComparison.Ordinal) && l.Contains("Json__Path", StringComparison.Ordinal));
    }
}
