namespace ArcaneCore.Kernel.Ops.ServerConsole;

/// <summary>
/// Where a daemon console reads commands from and writes replies to (docs/areas/server-console.md). The default is the
/// process console; tests pass their own readers and writers. The folder is <c>Ops/Console</c> but the namespace is
/// <c>ServerConsole</c>: a namespace named <c>Console</c> would hide <see cref="System.Console"/> from the rest of
/// <c>ArcaneCore.*.Ops</c>.
/// </summary>
/// <param name="Input">The command source (read on a background thread only).</param>
/// <param name="Output">The reply sink; must be safe to write from several threads.</param>
/// <param name="IsInputRedirected">Whether standard input is a pipe or file (services, tests, the deploy scripts): no reader is started then.</param>
/// <param name="IsUserInteractive">Whether the process runs in an interactive session (false for a Windows service).</param>
/// <param name="IsSupervisedChild">Whether the cluster host supervisor launched this process (<see cref="SupervisedChildDrain.EnvironmentVariable"/> is set): its redirected stdin carries the supervisor's <c>drain</c> line.</param>
/// <param name="SupervisorToken">The supervisor's per-run secret (<see cref="ServerConsole.SupervisorProtocol.TokenEnvironmentVariable"/>); the <c>lifecycle stopping</c> line carries it, and without one nothing is announced.</param>
/// <param name="SupervisorProtocol">Whether the supervisor asked for its line protocol (<see cref="ServerConsole.SupervisorProtocol.EnvironmentVariable"/> is <c>1</c>): <see cref="Output"/> then also carries the child's <c>lifecycle stopping</c> line.</param>
public sealed record ConsoleInputSource(TextReader Input, TextWriter Output, bool IsInputRedirected, bool IsUserInteractive, bool IsSupervisedChild = false, bool SupervisorProtocol = false, string? SupervisorToken = null)
{
    /// <summary>Whether a reader may be started on this source.</summary>
    public bool IsInteractive => !IsInputRedirected && IsUserInteractive;

    /// <summary>The process console: <see cref="Console.In"/> and a synchronized <see cref="Console.Out"/>.</summary>
    public static ConsoleInputSource Default()
        => new(Console.In, TextWriter.Synchronized(Console.Out), Console.IsInputRedirected, Environment.UserInteractive,
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SupervisedChildDrain.EnvironmentVariable)),
            ServerConsole.SupervisorProtocol.IsEnabled(),
            Environment.GetEnvironmentVariable(ServerConsole.SupervisorProtocol.TokenEnvironmentVariable));
}
