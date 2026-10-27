using System.Diagnostics;
using System.Globalization;
using System.Text;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Ops.Watchdog;

/// <summary>Runs the stack tool; the seam the tests replace so no process is started.</summary>
public interface IHangStackRunner
{
    /// <summary>Run <paramref name="tool"/> with <paramref name="arguments"/> and return its exit code and combined output; kill it after <paramref name="timeout"/>.</summary>
    Task<(int ExitCode, string Output)> RunAsync(string tool, IReadOnlyList<string> arguments, TimeSpan timeout);
}

/// <summary>The real runner: a child process with redirected output.</summary>
public sealed class ProcessHangStackRunner : IHangStackRunner
{
    public async Task<(int ExitCode, string Output)> RunAsync(string tool, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        ProcessStartInfo start = CreateStartInfo(tool, arguments);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{tool} did not start");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return (-1, $"{tool} did not finish within {timeout.TotalSeconds:F0} s and was stopped");
        }

        return (process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    }

    /// <summary>
    /// The child's start info: output redirected, no window, and the runtime's crash-dump variables removed from its environment. The World
    /// has them set (launch-daemon.ps1 -Diagnostics Max) for its own crash; a crashing stack tool that inherited them would write a dump named
    /// like the World's into the same directory and could push the World's real crash dump out of the dump-count retention.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string tool, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        RemoveDumpVariables(start.Environment);
        return start;
    }

    /// <summary>Removes <c>DOTNET_</c>/<c>COMPlus_</c> DbgEnableMiniDump, DbgMiniDump* and CreateDump* (any case) from <paramref name="environment"/>.</summary>
    public static void RemoveDumpVariables(IDictionary<string, string?> environment)
    {
        foreach (string name in environment.Keys.Where(IsDumpVariable).ToList())
        {
            environment.Remove(name);
        }
    }

    private static bool IsDumpVariable(string name)
    {
        foreach (string prefix in (string[])["DOTNET_", "COMPlus_"])
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string rest = name[prefix.Length..];
                return rest.StartsWith("DbgEnableMiniDump", StringComparison.OrdinalIgnoreCase)
                    || rest.StartsWith("DbgMiniDump", StringComparison.OrdinalIgnoreCase)
                    || rest.StartsWith("CreateDump", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }
}

/// <summary>
/// Captures every managed thread's stack, the world thread's among them, once per hung tick (<c>Ops:Watchdog:TickMonitor:StackCaptureTool</c>,
/// dotnet-stack). A hang line says which phase and feature the world thread is in; the stack says where in it, which is what a hang that
/// does not reproduce needs (issues #744/#755). In-process code cannot read another thread's managed stack, so the tool attaches from
/// outside through the runtime's diagnostics port (EventPipe, a brief sample of every thread, no suspension of the world thread beyond it).
/// <para>
/// Cheap when idle: nothing runs until a tick is past the hang threshold. <see cref="TryCapture"/> is called on the watchdog thread, never
/// blocks (the tool runs on the thread pool), starts at most one capture at a time and at most one per <c>StackCaptureIntervalSeconds</c>,
/// and is off when the tool is not configured or not found (said once).
/// </para>
/// </summary>
public sealed class HangStackCapture
{
    /// <summary>The longest a capture may run before the tool is stopped.</summary>
    public static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The GC pause share of the hung frame at or above which the capture header says the stack is not the cause.</summary>
    public const double GcDominatedShare = 0.5;

    /// <summary>Passed as the GC pause when reading it faulted: the capture header then says the pause is unknown.</summary>
    public const long GcPauseUnknown = -1;

    /// <summary>The marker of one thread's block in dotnet-stack report output; none in the output means the tool captured no stacks.</summary>
    public const string ThreadMarker = "Thread (";

    /// <summary>The fault-ledger site of a capture that holds no stacks or could not be written (empty output, tool failure, runner fault).</summary>
    public const string FaultSite = "watchdog.hang-stack-capture";

    /// <summary>The pause before the single retry of an exit-0 capture that printed no thread stacks (dotnet-stack's sample can miss them).</summary>
    public static readonly TimeSpan EmptyRetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly TickMonitorOptions _options;
    private readonly ILogger _logger;
    private readonly IHangStackRunner _runner;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<DateTime> _utcNow;
    private readonly int _processId;
    private long _lastStartMicros = long.MinValue;
    private int _running;
    private bool _missingSaid;

    public HangStackCapture(TickMonitorOptions options, ILogger logger, IHangStackRunner? runner = null, Func<string, bool>? fileExists = null, Func<DateTime>? utcNow = null, int? processId = null)
    {
        _options = options;
        _logger = logger;
        _runner = runner ?? new ProcessHangStackRunner();
        _fileExists = fileExists ?? File.Exists;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
        _processId = processId ?? Environment.ProcessId;
    }

    /// <summary>The capture started by the last successful <see cref="TryCapture"/> (completes, never faults), for tests and shutdown.</summary>
    public Task Last { get; private set; } = Task.CompletedTask;

    /// <summary>The configured tool with <c>%VAR%</c> expanded, or null when none is configured.</summary>
    public string? Tool => string.IsNullOrWhiteSpace(_options.StackCaptureTool) ? null : Environment.ExpandEnvironmentVariables(_options.StackCaptureTool.Trim());

    /// <summary>
    /// Watchdog thread, when a tick has been running past the hang threshold: start a capture unless one is running, the last started less
    /// than the interval ago, or no tool is available. True when a capture was started. Never throws.
    /// <paramref name="runningMicros"/> is how long the tick had been running and <paramref name="gcPauseMicros"/> the GC pause inside that time (<see cref="GcPauseUnknown"/> when unknown);
    /// when the pause covers <see cref="GcDominatedShare"/> or more of it the capture header says the stack shows where the thread was parked, not the cause.
    /// </summary>
    public bool TryCapture(long nowMicros, string reason, long runningMicros = 0, long gcPauseMicros = 0)
    {
        try
        {
            if (Tool is not { } tool)
            {
                return false;
            }

            if (!_fileExists(tool))
            {
                if (!_missingSaid)
                {
                    _missingSaid = true;
                    _logger.LogInformation("hang stack capture is off: the stack tool {Tool} was not found (install it with dotnet tool install -g dotnet-stack, or set Ops:Watchdog:TickMonitor:StackCaptureTool)", tool);
                }

                return false;
            }

            long interval = Math.Max(10, _options.StackCaptureIntervalSeconds) * 1_000_000L;
            if (_lastStartMicros != long.MinValue && nowMicros - _lastStartMicros < interval)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            {
                return false;
            }

            _lastStartMicros = nowMicros;
            DateTime at = _utcNow();
            string directory = _options.StackCaptureDirectory;
            Last = Task.Run(() => CaptureAsync(tool, directory, at, reason, runningMicros, gcPauseMicros));
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record(FaultSite, ex);
            _logger.LogFault("watchdog-hang-stack-capture", ex, "hang stack capture could not start");
            return false;
        }
    }

    private async Task CaptureAsync(string tool, string directory, DateTime at, string reason, long runningMicros, long gcPauseMicros)
    {
        string path = Path.Combine(directory, $"hang-stacks-{at.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{_processId}.txt");
        try
        {
            IReadOnlyList<string> arguments = ["report", "-p", _processId.ToString(CultureInfo.InvariantCulture)];
            (int exitCode, string output) = await _runner.RunAsync(tool, arguments, ToolTimeout).ConfigureAwait(false);
            bool retried = false;
            if (exitCode == 0 && IsEmpty(exitCode, output))
            {
                // An exit-0 run with no thread blocks is the intermittent EventPipe miss; one more sample usually holds the stacks.
                retried = true;
                await Task.Delay(EmptyRetryDelay).ConfigureAwait(false);
                (exitCode, output) = await _runner.RunAsync(tool, arguments, ToolTimeout).ConfigureAwait(false);
            }

            Directory.CreateDirectory(directory);
            var text = new StringBuilder(output.Length + 512);
            text.Append("# ").Append(at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" UTC, process ").Append(_processId)
                .Append(", ").Append(tool).Append(" exit ").Append(exitCode).AppendLine();
            text.Append("# ").AppendLine(reason.ReplaceLineEndings(" "));
            bool empty = IsEmpty(exitCode, output);
            bool gcDominated = runningMicros > 0 && gcPauseMicros >= runningMicros * GcDominatedShare;
            if (retried)
            {
                text.AppendLine("# RETRIED: the first run exited 0 with no thread stacks; this is the second run.");
            }

            if (empty)
            {
                text.Append("# EMPTY CAPTURE: the tool printed no thread stacks, so this hang has no stack. Tool exit ").Append(exitCode)
                    .Append(", ").Append(output.Length).Append(" output chars").Append(retried ? ", after one retry" : string.Empty).AppendLine(".");
            }

            if (gcPauseMicros == GcPauseUnknown)
            {
                text.AppendLine("# GC pause unknown: reading the host-load sampler failed (see the fault ledger), so this stack may be GC-dominated.");
            }

            if (gcDominated)
            {
                text.Append("# GC-DOMINATED: the GC pause was ").Append(gcPauseMicros / 1000).Append(" ms of the ").Append(runningMicros / 1000)
                    .AppendLine(" ms the tick had been running. The watchdog was suspended by that pause too, so the stack below shows where the threads were parked after it, not what caused the stall.");
            }
            else
            {
                text.AppendLine("# The world thread is the one inside ArcaneCore.Game.Maps.WorldRuntime.RunTick; the capture starts when the hang is first reported, so the stack is where the tick was stuck then.");
            }

            text.Append(output);
            await File.WriteAllTextAsync(path, text.ToString()).ConfigureAwait(false);
            if (empty || exitCode != 0)
            {
                // Not a caught exception: counted in the ledger so .server faults and the announce channel see it, and logged on every capture
                // (captures are already rate-limited by StackCaptureIntervalSeconds, so ledger log throttling would only hide them).
                FaultLedger.Shared.Record(FaultSite, new InvalidOperationException(
                    $"hang stack capture {(empty ? "is empty" : "failed")}: tool exit {exitCode}, {output.Length} output chars{(retried ? ", after one retry" : string.Empty)}"));
            }

            if (empty)
            {
                _logger.LogWarning("world tick hang: the stack tool captured no thread stacks (exit {ExitCode}{Retry}), the capture is empty: {Path}", exitCode, retried ? ", after one retry" : string.Empty, path);
            }
            else if (exitCode == 0)
            {
                _logger.LogWarning("world tick hang: the stacks of every managed thread were written to {Path}{GcNote}", path, gcDominated ? " (GC-dominated frame: the stack shows where the threads were parked, not the cause)" : string.Empty);
            }
            else
            {
                _logger.LogWarning("world tick hang: the stack tool exited with {ExitCode}; its output is in {Path}", exitCode, path);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record(FaultSite, ex);
            _logger.LogFault("watchdog-hang-stack-capture", ex, "world tick hang: the stack capture failed: {Path}", path);
            WriteFailure(path, at, tool, reason, ex);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private static bool IsEmpty(int exitCode, string output)
        => string.IsNullOrWhiteSpace(output) || (exitCode == 0 && !output.Contains(ThreadMarker, StringComparison.Ordinal)); // the marker assumes dotnet-stack report prints "Thread (" per thread

    /// <summary>After a faulted capture, leave a file that says why it holds no stacks, so no hang is left without a capture file or a reason.</summary>
    private void WriteFailure(string path, DateTime at, string tool, string reason, Exception fault)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, string.Create(CultureInfo.InvariantCulture,
                $"# {at:yyyy-MM-dd HH:mm:ss} UTC, process {_processId}, {tool} not run to completion\n# {reason.ReplaceLineEndings(" ")}\n# CAPTURE FAILED: {fault.GetType().Name}: {fault.Message.ReplaceLineEndings(" ")}\n"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record(FaultSite, ex);
            _logger.LogFault("watchdog-hang-stack-capture", ex, "world tick hang: the failed capture's reason could not be written: {Path}", path);
        }
    }
}
