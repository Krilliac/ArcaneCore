namespace ArcaneCore.World.Cluster.Supervision;

/// <summary>What the supervisor captures from a hung child before it drains it (<c>Cluster:Supervisor:Processes:&lt;key&gt;:HangCapture</c>).</summary>
public enum SupervisedHangCaptureMode
{
    /// <summary>Nothing (default).</summary>
    None = 0,

    /// <summary>A stack report of every managed thread, written by the dotnet-stack tool.</summary>
    Stacks = 1,

    /// <summary>A full memory dump. It holds secrets and can be gigabytes; opt in knowingly.</summary>
    Dump = 2,
}

/// <summary>One capture the supervisor asks for.</summary>
public sealed record SupervisedHangCaptureRequest(string Name, int ProcessId, SupervisedHangCaptureMode Mode, string Directory, string? StackTool);

/// <summary>Captures diagnostics from a hung child; the seam tests replace so no tool runs.</summary>
public interface ISupervisedHangCapture
{
    /// <summary>Writes the capture and returns when it is done. May throw; the supervisor records the fault and carries on. Cancellation means the supervisor stopped waiting.</summary>
    Task CaptureAsync(SupervisedHangCaptureRequest request, CancellationToken ct);
}

/// <summary>
/// The real capture. <see cref="SupervisedHangCaptureMode.Stacks"/> runs dotnet-stack (<c>report -p pid</c>) against the hung child and writes its
/// output to a file; <see cref="SupervisedHangCaptureMode.Dump"/> asks the child's diagnostics endpoint for a full dump. Both write under
/// the request's directory; a failed tool is an exception (the supervisor records it and carries on).
/// </summary>
public sealed class SystemSupervisedHangCapture : ISupervisedHangCapture
{
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(120);

    private readonly ArcaneCore.Kernel.Ops.Watchdog.IHangStackRunner _runner;

    public SystemSupervisedHangCapture(ArcaneCore.Kernel.Ops.Watchdog.IHangStackRunner? runner = null)
    {
        _runner = runner ?? new ArcaneCore.Kernel.Ops.Watchdog.ProcessHangStackRunner();
    }

    public async Task CaptureAsync(SupervisedHangCaptureRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mode == SupervisedHangCaptureMode.None)
            return;

        Directory.CreateDirectory(request.Directory);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string baseName = $"{SafeFileName(request.Name)}-{request.ProcessId}-{stamp}";

        if (request.Mode == SupervisedHangCaptureMode.Stacks)
        {
            string tool = string.IsNullOrWhiteSpace(request.StackTool) ? "dotnet-stack" : request.StackTool;
            (int exitCode, string output) = await _runner.RunAsync(
                tool, ["report", "-p", request.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)], ToolTimeout).WaitAsync(ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(request.Directory, baseName + ".stacks.txt"), output, ct).ConfigureAwait(false);
            if (exitCode != 0)
                throw new InvalidOperationException($"{tool} exited with code {exitCode} capturing stacks of process {request.ProcessId}.");

            return;
        }

        string path = Path.Combine(request.Directory, baseName + ".dmp");
        await Task.Run(
            () => new Microsoft.Diagnostics.NETCore.Client.DiagnosticsClient(request.ProcessId)
                .WriteDump(Microsoft.Diagnostics.NETCore.Client.DumpType.Full, path, logDumpGeneration: false),
            ct).ConfigureAwait(false);
    }

    private static string SafeFileName(string name)
    {
        char[] bad = Path.GetInvalidFileNameChars();
        return new string([.. name.Select(c => Array.IndexOf(bad, c) >= 0 ? '_' : c)]);
    }
}
