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

    private static readonly object TokenGate = new();
    private static bool _tokenCaptured;
    private static string? _capturedToken;

    /// <summary>
    /// The process console: <see cref="Console.In"/> and a synchronized <see cref="Console.Out"/>. The supervisor token is taken from the environment
    /// the first time this runs and the variable is then removed from the process environment, so it is not inherited by processes this one starts nor
    /// readable from a diagnostic dump of the environment; later calls reuse the captured value (the variable is gone by then).
    /// <para>
    /// Ordering: callers resolve this lazily from a DI singleton factory, which runs after the host builder was created. The builder's environment-variable
    /// configuration provider copies the environment when it is built, so the token is still present in the host's <c>IConfiguration</c> (under its raw
    /// variable name, which nothing binds) even though it is gone from the process environment. This clears the process environment only; it does not
    /// scrub configuration providers that already copied it.
    /// </para>
    /// </summary>
    public static ConsoleInputSource Default()
        => new(Console.In, TextWriter.Synchronized(Console.Out), Console.IsInputRedirected, Environment.UserInteractive,
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(SupervisedChildDrain.EnvironmentVariable)),
            ServerConsole.SupervisorProtocol.IsEnabled(),
            CaptureSupervisorToken());

    private static string? CaptureSupervisorToken()
    {
        lock (TokenGate)
        {
            if (!_tokenCaptured)
            {
                _capturedToken = Environment.GetEnvironmentVariable(ServerConsole.SupervisorProtocol.TokenEnvironmentVariable);
                Environment.SetEnvironmentVariable(ServerConsole.SupervisorProtocol.TokenEnvironmentVariable, null);
                _tokenCaptured = true;
            }

            return _capturedToken;
        }
    }

    /// <summary>Forgets the captured token so the next <see cref="Default"/> reads the environment again. For tests only.</summary>
    internal static void ResetCapturedTokenForTests()
    {
        lock (TokenGate)
        {
            _tokenCaptured = false;
            _capturedToken = null;
        }
    }
}
