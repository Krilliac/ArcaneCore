using System.Diagnostics;
using System.Runtime.InteropServices;
using ArcaneCore.Kernel.Ops;
using Xunit;

namespace ArcaneCore.Realm.Tests.Watchdog;

/// <summary>
/// The exit code a supervisor sees is whatever the realm's <c>Main</c> returns, so this runs the real
/// <c>ArcaneCore.Realm</c> (the build copies it next to the test assembly) against a throw-away SQLite auth
/// database with <c>Ops:Watchdog:Memory:Action=Stop</c> at a threshold every heap exceeds, and reads the exit
/// status. docs/ops/watchdog.md: Stop "sets ExitCodes.Current = 1 and stops the host once (a supervisor that
/// restarts on 1 brings the realm back)". Regression: Program.cs returned 0 after RunAsync, so systemd
/// Restart=on-failure treated the stop as clean and the logon service stayed down.
/// </summary>
public sealed class RealmExitCodeTests
{
    [Fact]
    public async Task MemoryAction_Stop_ExitsTheRealmProcessWithCode1()
    {
        string dir = Path.Combine(Path.GetTempPath(), "arcane-realm-exit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var info = new ProcessStartInfo(DotnetMuxer())
            {
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ArcaneCore.Realm.dll"));
            info.ArgumentList.Add("--Database:Provider=Sqlite");
            info.ArgumentList.Add("--Database:ConnectionString=Data Source=" + Path.Combine(dir, "auth.db"));
            info.ArgumentList.Add("--Auth:BindAddress=127.0.0.1");
            info.ArgumentList.Add("--Auth:Port=0");
            info.ArgumentList.Add("--Ops:Watchdog:CheckIntervalMs=100");
            info.ArgumentList.Add("--Ops:Watchdog:Memory:SampleIntervalSeconds=1");
            info.ArgumentList.Add("--Ops:Watchdog:Memory:Action=Stop");
            info.ArgumentList.Add("--Ops:Watchdog:Memory:ActionHeapBytes=1");
            info.ArgumentList.Add("--Ops:Watchdog:Counters:DumpIntervalSeconds=0");
            // Not under systemd here, whatever the test runner inherited.
            info.Environment.Remove("NOTIFY_SOCKET");
            info.Environment.Remove("WATCHDOG_USEC");

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
                Assert.Fail("the realm did not exit within 90 s after the memory action fired:\n" + await stdout + await stderr);
            }

            string output = await stdout + await stderr;
            Assert.Contains("reached the action threshold", output);
            Assert.Contains("Stop", output);
            Assert.Contains("Application is shutting down", output);
            Assert.True(process.ExitCode == ExitCodes.Failure, $"the realm exited with {process.ExitCode}, not {ExitCodes.Failure}:\n{output}");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // A late SQLite handle on Windows: the temp directory is not the subject of the test.
            }
        }
    }

    /// <summary>The <c>dotnet</c> muxer that runs this test, or the one owning the runtime we run on (a testhost apphost does not reveal it in its path).</summary>
    private static string DotnetMuxer()
    {
        string? current = Environment.ProcessPath;
        if (current is not null && Path.GetFileNameWithoutExtension(current).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        // <root>/shared/Microsoft.NETCore.App/<version>/ -> <root>/dotnet
        string runtime = RuntimeEnvironment.GetRuntimeDirectory();
        string root = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        string muxer = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(muxer) ? muxer : "dotnet";
    }
}
