using System.Diagnostics;
using System.Globalization;
using System.Text;
using ArcaneCore.Kernel.Configuration;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Ops.Watchdog;

/// <summary>A finished hang stack capture, as handed to <see cref="HangStackCapture.Captured"/>.</summary>
/// <param name="AtUtc">When the capture started (the hang was first reported).</param>
/// <param name="RunningMs">How long the tick had been running at that moment (0 when the caller did not say).</param>
/// <param name="WorldThreadFrames">The innermost <c>ArcaneCore</c> frames of the world thread (the one inside <c>WorldRuntime.RunTick</c>), as <c>Type.Method</c>; empty when the world thread was not found.</param>
/// <param name="Path">The file the full capture was written to.</param>
public sealed record HangStackReport(DateTime AtUtc, long RunningMs, IReadOnlyList<string> WorldThreadFrames, string Path);

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

    /// <summary>
    /// Set by the host to be told of each capture that produced a stack (exit code 0): called on the capture's thread-pool task after the file is written, never on the
    /// watchdog thread. A handler that throws is swallowed.
    /// </summary>
    public Action<HangStackReport>? Captured { get; set; }

    /// <summary>The capture started by the last successful <see cref="TryCapture"/> (completes, never faults), for tests and shutdown.</summary>
    public Task Last { get; private set; } = Task.CompletedTask;

    /// <summary>The configured tool with <c>%VAR%</c> expanded, or null when none is configured.</summary>
    public string? Tool => string.IsNullOrWhiteSpace(_options.StackCaptureTool) ? null : Environment.ExpandEnvironmentVariables(_options.StackCaptureTool.Trim());

    /// <summary>
    /// Watchdog thread, when a tick has been running past the hang threshold: start a capture unless one is running, the last started less
    /// than the interval ago, or no tool is available. True when a capture was started. Never throws.
    /// </summary>
    public bool TryCapture(long nowMicros, string reason, long runningMs = 0)
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
            Last = Task.Run(() => CaptureAsync(tool, directory, at, reason, runningMs));
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "hang stack capture could not start");
            return false;
        }
    }

    /// <summary>How many world-thread frames a <see cref="HangStackReport"/> carries.</summary>
    public const int TopWorldFrames = 4;

    private void NotifyCaptured(HangStackReport report)
    {
        try
        {
            Captured?.Invoke(report);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "a hang stack capture handler failed");
        }
    }

    /// <summary>
    /// The innermost <c>ArcaneCore</c> frames of the thread inside <c>WorldRuntime.RunTick</c> in a <c>dotnet-stack report</c> text (<c>Thread (0x..):</c> blocks of
    /// <c>assembly!Namespace.Type.Method(args)</c> lines, innermost first), as <c>Type.Method</c> with compiler artefacts removed, at most <paramref name="max"/>. Empty when
    /// no thread is in <c>RunTick</c> (the tick ended before the capture ran).
    /// </summary>
    public static IReadOnlyList<string> WorldThreadFrames(string report, int max)
    {
        var frames = new List<string>(max);
        if (string.IsNullOrEmpty(report) || max <= 0)
        {
            return frames;
        }

        var block = new List<string>();
        foreach (string raw in report.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith('#'))
            {
                continue; // the capture's own header, which names RunTick in prose
            }

            if (line.StartsWith("Thread (", StringComparison.Ordinal) || line.Trim().Length == 0)
            {
                if (TakeWorldBlock(block, max, frames))
                {
                    return frames;
                }

                block.Clear();
                continue;
            }

            block.Add(line.Trim());
        }

        TakeWorldBlock(block, max, frames);
        return frames;
    }

    private static bool TakeWorldBlock(List<string> block, int max, List<string> frames)
    {
        if (!block.Exists(line => line.Contains("WorldRuntime.RunTick", StringComparison.Ordinal)))
        {
            return false;
        }

        foreach (string line in block)
        {
            int bang = line.IndexOf('!', StringComparison.Ordinal);
            if (bang <= 0 || !line.StartsWith("ArcaneCore.", StringComparison.Ordinal))
            {
                continue;
            }

            string name = line[(bang + 1)..];
            int paren = name.IndexOf('(', StringComparison.Ordinal);
            if (paren > 0)
            {
                name = name[..paren];
            }

            string[] parts = name.Split('.');
            if (parts.Length < 2)
            {
                continue;
            }

            string method = ArcaneCore.Kernel.Diagnostics.FaultLedger.CleanName(parts[^1]);
            string type = ArcaneCore.Kernel.Diagnostics.FaultLedger.CleanName(parts[^2]);
            string frame = type.Length == 0 ? method : method.Length == 0 ? type : type + "." + method;
            if (frame.Length == 0 || (frames.Count > 0 && frames[^1] == frame))
            {
                continue;
            }

            frames.Add(frame);
            if (frames.Count == max)
            {
                break;
            }
        }

        return true;
    }

    private async Task CaptureAsync(string tool, string directory, DateTime at, string reason, long runningMs)
    {
        try
        {
            (int exitCode, string output) = await _runner.RunAsync(tool, ["report", "-p", _processId.ToString(CultureInfo.InvariantCulture)], ToolTimeout).ConfigureAwait(false);
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"hang-stacks-{at.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{_processId}.txt");
            var text = new StringBuilder(output.Length + 512);
            text.Append("# ").Append(at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" UTC, process ").Append(_processId)
                .Append(", ").Append(tool).Append(" exit ").Append(exitCode).AppendLine();
            text.Append("# ").AppendLine(reason.ReplaceLineEndings(" "));
            text.AppendLine("# The world thread is the one inside ArcaneCore.Game.Maps.WorldRuntime.RunTick; the capture starts when the hang is first reported, so the stack is where the tick was stuck then.");
            text.Append(output);
            await File.WriteAllTextAsync(path, text.ToString()).ConfigureAwait(false);
            if (exitCode == 0)
            {
                _logger.LogWarning("world tick hang: the stacks of every managed thread were written to {Path}", path);
                NotifyCaptured(new HangStackReport(at, runningMs, WorldThreadFrames(output, TopWorldFrames), path));
            }
            else
            {
                _logger.LogWarning("world tick hang: the stack tool exited with {ExitCode}; its output is in {Path}", exitCode, path);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "world tick hang: the stack capture failed");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }
}
