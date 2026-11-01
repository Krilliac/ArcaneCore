using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Tests.Ops;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Diagnostics;

/// <summary>
/// The world thread's explicit stack (<see cref="WorldRuntime.WorldThreadStackBytes"/>) and the production stack guard on a deep recursion, on the world
/// thread itself and in a child process (the throwaway fixture, which binds the real <c>StackGuard</c> from ArcaneCore.Game.dll and runs a 2 KB-a-frame
/// recursion on a thread of a given stack size). A real overflow ends the process and cannot be caught, so the child is where it may run: guarded it must
/// stop cleanly (exit 0, a depth printed); with the guard suspended (the RED run) it must die with a stack overflow.
/// </summary>
public sealed class StackGuardRuntimeTests
{
    private const int FrameBytes = 2048;

    [Fact]
    public void TheWorldRuntime_ReportsTheConfiguredWorldThreadStack()
    {
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, NullSaveQueue.Instance, NullLogger<WorldRuntime>.Instance);

        Assert.Equal(WorldRuntime.WorldThreadStackBytes, world.ConfiguredStackBytes);
        Assert.Equal(16 * 1024 * 1024, world.ConfiguredStackBytes);
    }

    [Fact]
    public async Task TheStartedWorldThread_RunsAGuardedRecursion_FarDeeperThanASmallStackAllows()
    {
        using var world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, NullSaveQueue.Instance, NullLogger<WorldRuntime>.Instance);
        world.Start();
        try
        {
            var ledger = new FaultLedger();
            int worldDepth = await world.InvokeAsync(() => RecurseWarm(ledger)).WaitAsync(TimeSpan.FromSeconds(60));

            int smallDepth = 0;
            var small = new Thread(() => smallDepth = RecurseWarm(new FaultLedger()), 512 * 1024);
            small.Start();
            small.Join();

            // 16 MiB at 2 KB a frame is about 8000 frames, minus the runtime's margin; the 512 KiB thread gets about 190.
            Assert.True(worldDepth >= 5000, $"the world thread reached only {worldDepth} frames of {FrameBytes} bytes");
            Assert.True(worldDepth > smallDepth * 10, $"world {worldDepth} frames against {smallDepth} on a 512 KiB thread");
        }
        finally
        {
            world.Stop();
        }
    }

    [Fact]
    public async Task InAChildProcess_TheProductionGuard_StopsADeepRecursionCleanly_AtTheWorldStackSize()
    {
        (int exitCode, string output) = await RunFixtureAsync(WorldRuntime.WorldThreadStackBytes, unguarded: false);

        Assert.Equal(0, exitCode);
        Assert.True(ParseDepth(output) >= 5000, $"the child reached only {output}");
    }

    [Fact]
    public async Task InAChildProcess_TheProductionGuard_CutsASmallStackMuchSooner()
    {
        (int smallExit, string smallOutput) = await RunFixtureAsync(512 * 1024, unguarded: false);
        (int bigExit, string bigOutput) = await RunFixtureAsync(WorldRuntime.WorldThreadStackBytes, unguarded: false);

        Assert.Equal(0, smallExit);
        Assert.Equal(0, bigExit);
        Assert.True(ParseDepth(bigOutput) > ParseDepth(smallOutput) * 10, $"small: {smallOutput}; big: {bigOutput}");
    }

    [Fact]
    public async Task InAChildProcess_WithTheProductionGuardSuspended_TheSameRecursionOverflowsAndKillsTheChild()
    {
        // RED: the same recursion, the same production StackGuard, suspended: nothing stops it and the runtime ends the process.
        (int exitCode, string output) = await RunFixtureAsync(256 * 1024, unguarded: true);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Stack overflow", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("depth ", output, StringComparison.Ordinal);
    }

    private static int ParseDepth(string output)
    {
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("depth ", StringComparison.Ordinal))
            {
                return int.Parse(line["depth ".Length..], CultureInfo.InvariantCulture);
            }
        }

        throw new InvalidOperationException("the child printed no depth: " + output);
    }

    private static async Task<(int ExitCode, string Output)> RunFixtureAsync(int stackBytes, bool unguarded)
    {
        var info = new ProcessStartInfo(ConfProcess.DotnetMuxer())
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SupervisedChildFixture.dll"));
        info.ArgumentList.Add("--stack-depth");
        info.ArgumentList.Add(stackBytes.ToString(CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--game-dll");
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ArcaneCore.Game.dll"));
        if (unguarded)
        {
            info.ArgumentList.Add("--unguarded");
        }

        // No runtime crash dump for the deliberate overflow (the fixture installs no ArcaneCore crash handler, so there is no repo dump mode to turn off).
        info.Environment["DOTNET_DbgEnableMiniDump"] = "0";

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("the fixture did not start");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await stdout + await stderr);
    }

    /// <summary>Throws and catches once so the first fault record (which needs stack for the exception machinery) is not paid at the margin, then recurses.</summary>
    private static int RecurseWarm(FaultLedger ledger)
    {
        try
        {
            throw new InsufficientExecutionStackException("warm-up");
        }
        catch (InsufficientExecutionStackException ex)
        {
            new FaultLedger().Record("test.stack-guard-runtime-warmup", ex);
        }

        return Recurse(ledger, 0);
    }

    /// <summary>The production guard in a 2 KB-a-frame recursion: returns the depth at which <see cref="StackGuard.TryEnter"/> cut it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Recurse(FaultLedger ledger, int depth)
    {
        if (!StackGuard.TryEnter("test.stack-guard-runtime", ledger))
        {
            return depth;
        }

        Span<byte> pad = stackalloc byte[FrameBytes];
        pad[0] = 1;
        int result = Recurse(ledger, depth + 1);
        return result + pad[0] - 1; // keeps the frame alive: not a tail call
    }
}
