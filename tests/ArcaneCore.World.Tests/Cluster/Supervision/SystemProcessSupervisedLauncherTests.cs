using System.Collections.Concurrent;
using System.Diagnostics;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.World.Cluster.Supervision;
using ArcaneCore.World.Tests.Ops;
using Xunit;

namespace ArcaneCore.World.Tests.Cluster.Supervision;

/// <summary>
/// The real launcher against a throwaway fixture child (tests/fixtures/SupervisedChildFixture) that ignores stdin and exits only when
/// killed or its bounded lifetime (60 s) ends. It is never an ArcaneCore daemon, so nothing here can start a world, a realm or a database. These pin who owns a child
/// and for how long: a pid leaves the owned set only when the OS reports the exit, and a kill always reaches a child that is still running.
/// No test ever kills a process by pid (a pid can be reused by an unrelated process): a fixture left behind by a failing test ends by itself
/// when its lifetime runs out, and the one test that makes the kill fail gives its child a 5 s lifetime instead.
/// </summary>
public sealed class SystemProcessSupervisedLauncherTests : IDisposable
{
    private readonly string _tempDir;

    public SystemProcessSupervisedLauncherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ArcaneCore.LauncherTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // A late handle on Windows: the temp directory is not the subject of the test.
        }
    }

    private static SupervisedProcessConfig FixtureConfig(string name, string extraArgs = "", int drainTimeoutMs = 500) => new()
    {
        Name = name,
        Role = SupervisedRoles.Worker,
        ExecutablePath = ConfProcess.DotnetMuxer(),
        Arguments = $"\"{Path.Combine(AppContext.BaseDirectory, "SupervisedChildFixture.dll")}\" {extraArgs}".Trim(),
        Console = SupervisedConsoleMode.None,
        DrainTimeoutMs = drainTimeoutMs,
        RequiredMemoryMb = 1,
    };

    /// <summary>Polls up to 5 s: Windows keeps the process object until the last handle closes; Linux is gone once .NET reaps the child.</summary>
    private static async Task AssertProcessGoneAsync(int pid)
    {
        for (int i = 0; i < 50; i++)
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                if (p.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"pid {pid} is still alive");
    }

    private static void AssertProcessAlive(int pid)
    {
        using Process p = Process.GetProcessById(pid);
        Assert.False(p.HasExited);
    }

    private sealed class FixedProbe(long availableMb) : IClusterCapacityProbe
    {
        public long GetAvailableMemoryMb() => availableMb;
    }

    private static ClusterHostSupervisor NewSupervisor(SystemProcessSupervisedLauncher launcher, int drainTimeoutMs, out SupervisedOutputMultiplexer mux)
    {
        var options = new ClusterSupervisorOptions();
        options.Processes["child"] = FixtureConfig("child", drainTimeoutMs: drainTimeoutMs);
        mux = new SupervisedOutputMultiplexer(TextWriter.Null);
        return new ClusterHostSupervisor(options, launcher, mux, new FixedProbe(16_384));
    }

    [Fact]
    public async Task RequestDrain_WhenTheChildIgnoresStdin_TimesOut_KeepsThePid_AndKillRemovesIt()
    {
        var launcher = new SystemProcessSupervisedLauncher();
        using ISupervisedProcess p = launcher.Launch(FixtureConfig("ignores-stdin"));
        int pid = p.ProcessId;
        bool drained = await p.RequestDrainAsync(TimeSpan.FromMilliseconds(500));

        Assert.False(drained);
        Assert.Contains(pid, launcher.OwnedProcessIds);

        p.KillOwnedProcess();

        Assert.True(p.HasExited);
        Assert.DoesNotContain(pid, launcher.OwnedProcessIds);
        await AssertProcessGoneAsync(pid);
        p.Dispose();
        p.KillOwnedProcess();
    }

    [Fact]
    public async Task RequestDrain_WhenTheCallersTokenFires_Throws_KeepsThePid_AndKillRemovesIt()
    {
        var launcher = new SystemProcessSupervisedLauncher();
        using ISupervisedProcess p = launcher.Launch(FixtureConfig("cancelled-drain"));
        int pid = p.ProcessId;
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(200);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.RequestDrainAsync(TimeSpan.FromSeconds(30), cts.Token));

        Assert.Contains(pid, launcher.OwnedProcessIds);
        AssertProcessAlive(pid);

        p.KillOwnedProcess();

        await AssertProcessGoneAsync(pid);
        Assert.Empty(launcher.OwnedProcessIds);
    }

    [Fact]
    public async Task KillOwnedProcess_KillsTheWholeTree_AndIsIdempotent()
    {
        // Not a reproducer of the ownership bug: it pins the tree kill and idempotence.
        string pidFile = Path.Combine(_tempDir, "pids.txt");
        var launcher = new SystemProcessSupervisedLauncher();
        // The fixture writes both pid lines (closing the file each time) before it prints "ready", so "ready" is the latch: nothing reads the file while it is being written.
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using ISupervisedProcess p = launcher.Launch(FixtureConfig("tree", $"--spawn-child --pid-file \"{pidFile}\""), (_, line, isStdErr) =>
        {
            if (!isStdErr && line == "ready")
            {
                ready.TrySetResult();
            }
        });
        var pids = new List<int> { p.ProcessId };
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(120)); // a backstop for a fixture that never starts, not a pacing wait
        string[] lines = File.ReadAllLines(pidFile).Where(l => l.Length > 0).ToArray();

        Assert.Equal(2, lines.Length);
        pids.AddRange(lines.Select(l => int.Parse(l.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture)));

        p.KillOwnedProcess();
        p.KillOwnedProcess();
        p.Dispose();
        p.KillOwnedProcess();

        foreach (int pid in pids)
        {
            await AssertProcessGoneAsync(pid);
        }

        Assert.Empty(launcher.OwnedProcessIds);

    }

    [Fact]
    public async Task WaitForExit_ObservesAChildThatExitsOnItsOwn_AndRemovesThePid()
    {
        var launcher = new SystemProcessSupervisedLauncher();
        using ISupervisedProcess p = launcher.Launch(FixtureConfig("exits", "--exit 3"));
        int pid = p.ProcessId;
        int code = await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(3, code);
        Assert.Equal(3, p.ExitCode);
        Assert.True(p.HasExited);
        Assert.Empty(launcher.OwnedProcessIds);

        p.Dispose();
        Assert.Equal(3, p.ExitCode);
    }

    [Fact]
    public void Dispose_WhenTheKillWasRefused_ReportsTheChildAsStillRunning_AndKeepsItsPid()
    {
        // O5: after Dispose the handle is closed, but an exit that was never observed must not read as an exit.
        var launcher = new SystemProcessSupervisedLauncher
        {
            KillOverride = _ => { }, // the OS refused the kill
            KillConfirmTimeout = TimeSpan.FromMilliseconds(300),
        };
        // A short life: this child is never killed, and on Windows it holds the test runner's inherited std handles until it exits.
        ISupervisedProcess p = launcher.Launch(FixtureConfig("kill-refused", "--lifetime-ms 5000"));
        int pid = p.ProcessId;
        p.KillOwnedProcess();
        Assert.False(p.HasExited);

        p.Dispose();

        Assert.False(p.HasExited);
        Assert.Null(p.ExitCode);
        Assert.Contains(pid, launcher.OwnedProcessIds);
        AssertProcessAlive(pid);
    }

    [Fact]
    public async Task Supervisor_DrainAndStop_WhenTheChildIgnoresStdin_KillsItAfterTheDrainTimeout()
    {
        var launcher = new SystemProcessSupervisedLauncher();
        ClusterHostSupervisor supervisor = NewSupervisor(launcher, drainTimeoutMs: 500, out SupervisedOutputMultiplexer mux);
        int pid = 0;
        try
        {
            await supervisor.StartAllAsync();
            pid = supervisor.GetSnapshot("child").ProcessId!.Value;

            await supervisor.DrainAndStopAsync();

            SupervisedProcessSnapshot snapshot = supervisor.GetSnapshot("child");
            Assert.Equal(SupervisedProcessState.Drained, snapshot.State);
            Assert.Equal(0, snapshot.RestartCount);
            await AssertProcessGoneAsync(pid);
            Assert.Empty(launcher.OwnedProcessIds);
        }
        finally
        {
            supervisor.Dispose();
            mux.Dispose();
        }
    }

    [Fact]
    public async Task Supervisor_DrainAndStop_WhenCancelledMidDrain_StillKillsTheChild_AndDoesNotThrow()
    {
        var launcher = new SystemProcessSupervisedLauncher();
        ClusterHostSupervisor supervisor = NewSupervisor(launcher, drainTimeoutMs: 30_000, out SupervisedOutputMultiplexer mux);
        int pid = 0;
        try
        {
            await supervisor.StartAllAsync();
            pid = supervisor.GetSnapshot("child").ProcessId!.Value;

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(300);
            await supervisor.DrainAndStopAsync(cts.Token);

            Assert.Equal(SupervisedProcessState.Drained, supervisor.GetSnapshot("child").State);
            await AssertProcessGoneAsync(pid);
            Assert.Empty(launcher.OwnedProcessIds);
        }
        finally
        {
            supervisor.Dispose();
            mux.Dispose();
        }
    }

    [Fact]
    public async Task Supervisor_Dispose_WhileMonitoring_KillsTheChild_AndCompletesTheMonitor()
    {
        var launcher = new SystemProcessSupervisedLauncher();
        ClusterHostSupervisor supervisor = NewSupervisor(launcher, drainTimeoutMs: 500, out SupervisedOutputMultiplexer mux);
        int pid = 0;
        try
        {
            await supervisor.StartAllAsync();
            pid = supervisor.GetSnapshot("child").ProcessId!.Value;

            Task dispose = Task.Run(supervisor.Dispose);
            await dispose.WaitAsync(TimeSpan.FromSeconds(30));

            await AssertProcessGoneAsync(pid);
            Assert.Empty(launcher.OwnedProcessIds);
            Assert.Equal(SupervisedProcessState.Stopped, supervisor.GetSnapshot("child").State);
            supervisor.Dispose(); // a second dispose is a no-op
        }
        finally
        {
            mux.Dispose();
        }
    }

    [Fact]
    public async Task Launch_WithAHandler_DeliversTheChildsFirstLine_AndABareCrDoesNotSplitALine()
    {
        var launcher = new SystemProcessSupervisedLauncher();
        var lines = new ConcurrentQueue<string>();
        using ISupervisedProcess p = launcher.Launch(FixtureConfig("cr-line", "--cr-heartbeat"), (_, line, isStdErr) =>
        {
            if (!isStdErr)
            {
                lines.Enqueue(line);
            }
        });

        await TestWait.UntilAsync(() => lines.Contains("done"), "the fixture's last line");

        // "ready" is the first thing the child writes: it arrives only because the handler was subscribed before the pumps started.
        Assert.Equal(
            ["ready", $"noise{(char)13}heartbeat 2026-10-29T00:00:00.000Z pid={p.ProcessId} alive", "done"],
            lines.ToArray());
        Assert.All(lines, line => Assert.False(SupervisorProtocol.TryParseHeartbeat(line, out _), "a line holding a bare CR must not parse as a heartbeat"));
    }

    [Fact]
    public async Task Supervisor_ChildLineWithABareCrBeforeAHeartbeat_DoesNotForgeABeat()
    {
        var launcher = new SystemProcessSupervisedLauncher();
        var options = new ClusterSupervisorOptions();
        SupervisedProcessConfig config = FixtureConfig("child", "--cr-heartbeat");
        config.Console = SupervisedConsoleMode.Merged;
        options.Processes["child"] = config;
        var console = new StringWriter();
        var mux = new SupervisedOutputMultiplexer(console);
        var supervisor = new ClusterHostSupervisor(options, launcher, mux, new FixedProbe(16_384));
        try
        {
            await supervisor.StartAllAsync();

            // "done" follows the CR line on the same pipe, and one pump handles a pipe in order: once it shows, the CR line has been handled.
            await TestWait.UntilAsync(() =>
            {
                lock (console)
                {
                    return console.ToString().Contains("done", StringComparison.Ordinal);
                }
            }, "the child's last line");

            Assert.Null(supervisor.GetSnapshot("child").LastBeatAgeMs);
            string text;
            lock (console)
            {
                text = console.ToString();
            }

            Assert.Contains("noise", text, StringComparison.Ordinal); // the CR line was ordinary output, not swallowed as protocol
        }
        finally
        {
            supervisor.Dispose();
            mux.Dispose();
        }
    }

    [Fact]
    public async Task RequestDrain_WhenTheChildsStdinPipeIsFull_StillTimesOut_AndKillEndsTheBlockedWriter()
    {
        var launcher = new SystemProcessSupervisedLauncher();
        using ISupervisedProcess p = launcher.Launch(FixtureConfig("full-stdin")); // never reads stdin
        int sent = 0;
        string filler = new('x', 1000);
        Task flood = Task.Run(() =>
        {
            while (p.TrySendLine(filler))
            {
                Interlocked.Increment(ref sent);
            }
        });

        try
        {
            // The pipe is full when the sent count stops moving: the writer is now blocked inside the stdin lock.
            int last = -1;
            int stable = 0;
            for (int i = 0; i < 300 && stable < 10; i++)
            {
                await Task.Delay(100);
                int now = Volatile.Read(ref sent);
                stable = now > 0 && now == last ? stable + 1 : 0;
                last = now;
            }

            Assert.True(stable >= 10, "the child's stdin pipe never filled");
            Assert.False(flood.IsCompleted);

            // Without the fix the drain's own write blocks the caller before the timeout is armed, so this never returns.
            var stopwatch = Stopwatch.StartNew();
            bool drained = await Task.Run(() => p.RequestDrainAsync(TimeSpan.FromMilliseconds(500))).WaitAsync(TimeSpan.FromSeconds(20));

            Assert.False(drained);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"the drain took {stopwatch.Elapsed}");
            Assert.Contains(p.ProcessId, launcher.OwnedProcessIds);
        }
        finally
        {
            p.KillOwnedProcess(); // breaks the pipe, which is what ends the writer that is still blocked
        }

        await flood.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(p.HasExited);
    }
}
