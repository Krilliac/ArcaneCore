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
/// The world thread's explicit stack (<see cref="WorldRuntime.WorldThreadStackBytes"/>) and the stack guard on a deep recursion, on the world thread
/// itself and in a child process (the throwaway fixture, which runs the same guarded 2 KB-a-frame recursion on a thread of a given stack size).
/// A real overflow ends the process and cannot be caught, so the child is where a deep fixture may run without risking the test host: it must
/// stop cleanly (exit 0, a depth printed), never crash.
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
    public async Task InAChildProcess_TheDeepGuardedRecursion_StopsCleanlyAtTheWorldStackSize()
    {
        (int exitCode, string output) = await RunFixtureAsync(WorldRuntime.WorldThreadStackBytes);

        Assert.Equal(0, exitCode);
        int depth = ParseDepth(output);
        Assert.True(depth >= 5000, $"the child reached only {depth} frames: {output}");
    }

    [Fact]
    public async Task InAChildProcess_ASmallStack_StopsCleanlyAndMuchSooner()
    {
        (int smallExit, string smallOutput) = await RunFixtureAsync(512 * 1024);
        (int bigExit, string bigOutput) = await RunFixtureAsync(WorldRuntime.WorldThreadStackBytes);

        Assert.Equal(0, smallExit);
        Assert.Equal(0, bigExit);
        Assert.True(ParseDepth(bigOutput) > ParseDepth(smallOutput) * 10, $"small: {smallOutput}; big: {bigOutput}");
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

    private static async Task<(int ExitCode, string Output)> RunFixtureAsync(int stackBytes)
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
