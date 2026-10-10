using System.Diagnostics;
using System.Runtime.InteropServices;
using ArcaneCore.Kernel.Ops;
using Xunit;

namespace ArcaneCore.Realm.Tests.Ops;

/// <summary>
/// The Realm daemon's command line, end to end: <c>ArcaneCore.Realm.dll --help</c> must print the usage and exit 64 without building the
/// host (a bare <c>Host.CreateApplicationBuilder(args)</c> drops <c>--help</c> silently and the realm would start serving logons).
/// The sentinel is a file-logger path that only exists once the host has been built; the control test proves it can fire, so its absence
/// in the help test means something.
/// </summary>
public sealed class ConfRealmCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "arcane-confrealm-" + Guid.NewGuid().ToString("N")[..8]);

    public ConfRealmCliTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A late SQLite or logger handle on Windows: the temp directory is not the subject of the test.
        }
    }

    private string Sentinel => Path.Combine(_dir, "sentinel.log");

    /// <summary>Options that make the realm build its host and, if it runs, stop itself at once (Ops:Watchdog memory action, as RealmExitCodeTests).</summary>
    private string[] HostArgs =>
    [
        "--Database:Provider=Sqlite",
        "--Database:ConnectionString=Data Source=" + Path.Combine(_dir, "auth.db"),
        "--Auth:BindAddress=127.0.0.1",
        "--Auth:Port=0",
        "--Logging:ArcaneCore:File:Enabled=true",
        "--Logging:ArcaneCore:File:Path=" + Sentinel,
        "--Ops:Watchdog:CheckIntervalMs=100",
        "--Ops:Watchdog:Memory:SampleIntervalSeconds=1",
        "--Ops:Watchdog:Memory:Action=Stop",
        "--Ops:Watchdog:Memory:ActionHeapBytes=1",
        "--Ops:Watchdog:Counters:DumpIntervalSeconds=0",
    ];

    [Fact]
    public async Task Realm_Help_PrintsUsageAndExits64_WithoutBuildingTheHost()
    {
        (int exit, string output) = await RunRealmAsync([.. HostArgs, "--help"]);

        Assert.Equal(ExitCodes.Usage, exit);
        Assert.Contains("Usage:", output, StringComparison.Ordinal);
        Assert.Contains("check-config", output, StringComparison.Ordinal);
        Assert.Contains("--config", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Sentinel), "the host was built: " + Sentinel + " exists\n" + output);
    }

    [Fact]
    public async Task Realm_SameArgumentsWithoutHelp_BuildTheHost_SentinelControl()
    {
        (int exit, string output) = await RunRealmAsync(HostArgs);

        Assert.True(exit == ExitCodes.Failure, $"the realm exited with {exit}, not {ExitCodes.Failure} (the watchdog stop):\n{output}");
        Assert.True(File.Exists(Sentinel), "the realm ran but never created the sentinel log file, so the --help test proves nothing:\n" + output);
    }

    [Fact]
    public async Task Realm_CheckConfig_WarnsOnAMistypedRealmConfKey_WithTheSuggestion()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "realm.conf"), "# realm\nAuth.Prot = 3724\n");

        (int exit, string output) = await RunRealmAsync(["check-config", "--contentRoot", _dir, .. HostArgs]);

        Assert.Equal(ExitCodes.Success, exit); // a warning, not an error
        Assert.Contains("realm.conf:2", output, StringComparison.Ordinal);
        Assert.Contains("Auth.Prot", output, StringComparison.Ordinal);
        Assert.Contains("Auth.Port", output, StringComparison.Ordinal); // the closest key
        Assert.False(File.Exists(Sentinel), "check-config built the host\n" + output);

        await File.WriteAllTextAsync(Path.Combine(_dir, "realm.conf"), "# realm\nAuth.Port = 3724\n");
        (int cleanExit, string clean) = await RunRealmAsync(["check-config", "--contentRoot", _dir, .. HostArgs]);
        Assert.Equal(ExitCodes.Success, cleanExit);
        Assert.DoesNotContain("realm.conf:", clean, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Realm_StartupStrict_IsAcceptedOnTheCommandLine_AndInRealmConf()
    {
        (int exit, string output) = await RunRealmAsync(["check-config", "--Startup:Strict=true", .. HostArgs]);
        Assert.True(exit == ExitCodes.Success, $"--Startup:Strict=true: exit {exit}:\n{output}");
        Assert.DoesNotContain("unknown option", output, StringComparison.OrdinalIgnoreCase);

        await File.WriteAllTextAsync(Path.Combine(_dir, "realm.conf"), "Startup.Strict = true\n");
        (int confExit, string confOutput) = await RunRealmAsync(["check-config", "--contentRoot", _dir, .. HostArgs]);
        Assert.True(confExit == ExitCodes.Success, $"Startup.Strict = true in realm.conf: exit {confExit}:\n{confOutput}");
        Assert.DoesNotContain("realm.conf:", confOutput, StringComparison.Ordinal);

        // The control: under the same strict file a mistyped key is refused (exit 78), so the pass above is the catalog and not a disabled check.
        await File.WriteAllTextAsync(Path.Combine(_dir, "realm.conf"), "Startup.Strict = true\nAuth.Prot = 3724\n");
        (int strictExit, string strictOutput) = await RunRealmAsync(["check-config", "--contentRoot", _dir, .. HostArgs]);
        Assert.True(strictExit == ExitCodes.InvalidConfiguration, $"exit {strictExit}:\n{strictOutput}");
    }

    private static async Task<(int ExitCode, string Output)> RunRealmAsync(string[] args)
    {
        var info = new ProcessStartInfo(DotnetMuxer())
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ArcaneCore.Realm.dll"));
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment.Remove("NOTIFY_SOCKET");
        info.Environment.Remove("WATCHDOG_USEC");
        info.Environment.Remove("ARCANECORE_CONFIG");

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("the realm process did not start");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("the realm did not exit within 90 s:\n" + await stdout + await stderr);
        }

        return (process.ExitCode, await stdout + await stderr);
    }

    private static string DotnetMuxer()
    {
        string? current = Environment.ProcessPath;
        if (current is not null && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        string runtime = RuntimeEnvironment.GetRuntimeDirectory();
        string root = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        string muxer = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(muxer) ? muxer : "dotnet";
    }
}
