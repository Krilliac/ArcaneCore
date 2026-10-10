using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace ArcaneCore.World.Tests.Ops;

/// <summary>Runs the real <c>ArcaneCore.World.dll</c> (the build copies it next to the test assembly) in a child process.</summary>
internal static class ConfProcess
{
    public sealed record Result(int ExitCode, string Output);

    public static async Task<Result> RunWorldAsync(params string[] args)
    {
        var info = new ProcessStartInfo(DotnetMuxer())
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ArcaneCore.World.dll"));
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        // Not under systemd, and no inherited configuration directory, whatever the test runner has set.
        info.Environment.Remove("NOTIFY_SOCKET");
        info.Environment.Remove("WATCHDOG_USEC");
        info.Environment.Remove("ARCANECORE_CONFIG");

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("the world process did not start");
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
            Assert.Fail("the world process did not exit within 90 s (it may have started as a live server):\n" + await stdout + await stderr);
        }

        return new Result(process.ExitCode, await stdout + await stderr);
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
