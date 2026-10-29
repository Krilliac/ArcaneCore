using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Kernel.Ops.ServerConsole;

/// <summary>
/// The child side of the cluster host supervisor's drain (docs/CLUSTERING_DESIGN.md, M15.4). The supervisor launches a child with
/// <see cref="EnvironmentVariable"/> set and its stdin redirected, and drains it by writing the line <see cref="DrainLine"/> and closing
/// stdin. This hosted service reads that stdin through <see cref="ConsoleLineReader"/> and, on <c>drain</c> or on end of input (the
/// supervisor died), calls <see cref="IHostApplicationLifetime.StopApplication"/>: the same graceful path as a console shutdown or Ctrl+C
/// (the world saves, the realm stops its listeners). It starts nothing unless the source is a supervised child with redirected input, so
/// the daemon consoles (<c>WorldConsole</c>, <c>RealmConsole</c>, which stay off on redirected input) never see the line as a command.
/// <para>Thread affinity: <see cref="StartAsync"/>/<see cref="StopAsync"/> on the host thread; the handlers run on the reader thread.</para>
/// </summary>
public sealed class SupervisedChildDrain(ConsoleInputSource source, IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory) : IHostedService
{
    /// <summary>Environment variable the supervisor sets on every child (its value is the child's process name).</summary>
    public const string EnvironmentVariable = "ARCANECORE_SUPERVISED_CHILD";

    /// <summary>The stdin line that asks the child to stop gracefully.</summary>
    public const string DrainLine = "drain";

    private readonly ILogger _logger = loggerFactory.CreateLogger("ArcaneCore.SupervisedChild");
    private ConsoleLineReader? _reader;

    /// <summary>The reader thread; null when no reader was started. Exposed for tests.</summary>
    public Thread? ReaderThread => _reader?.Thread;

    /// <summary>Whether <paramref name="line"/> is the supervisor's drain request.</summary>
    public static bool IsDrainLine(string line) => string.Equals(line.Trim(), DrainLine, StringComparison.OrdinalIgnoreCase);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!source.IsSupervisedChild || !source.IsInputRedirected)
        {
            return Task.CompletedTask;
        }

        _reader = new ConsoleLineReader(source.Input, "supervised-drain");
        _reader.Start(OnLine, _logger, () => Stop("the supervisor closed stdin"));
        return Task.CompletedTask;
    }

    private void OnLine(string line)
    {
        if (IsDrainLine(line))
        {
            Stop("the supervisor asked for a drain");
        }
        else
        {
            _logger.LogDebug("ignored a supervisor stdin line that is not 'drain'");
        }
    }

    private void Stop(string reason)
    {
        _logger.LogInformation("Supervised child stopping gracefully: {Reason}", reason);
        lifetime.StopApplication();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _reader?.Stop();
        return Task.CompletedTask;
    }
}
