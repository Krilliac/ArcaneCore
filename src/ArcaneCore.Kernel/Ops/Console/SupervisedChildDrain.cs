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
public sealed class SupervisedChildDrain(ConsoleInputSource source, IHostApplicationLifetime lifetime, ILoggerFactory loggerFactory, ISupervisedLifecycleRequestHandler? lifecycle = null) : IHostedService
{
    /// <summary>Environment variable the supervisor sets on every child (its value is the child's process name).</summary>
    public const string EnvironmentVariable = "ARCANECORE_SUPERVISED_CHILD";

    /// <summary>The stdin line that asks the child to stop gracefully.</summary>
    public const string DrainLine = "drain";

    private readonly ILogger _logger = loggerFactory.CreateLogger("ArcaneCore.SupervisedChild");
    private ConsoleLineReader? _reader;
    private CancellationTokenRegistration _stoppingRegistration;
    private Task _announcement = Task.CompletedTask;

    /// <summary>The longest <see cref="StopAsync"/> waits for the stopping announcement to be written.</summary>
    public static readonly TimeSpan AnnouncementWait = TimeSpan.FromSeconds(2);

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

        if (source.SupervisorProtocol)
        {
            _stoppingRegistration = lifetime.ApplicationStopping.Register(AnnounceStopping);
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
        else if (SupervisorProtocol.TryParseForwardedLifecycle(line, out SupervisorLifecycleRequest request))
        {
            if (lifecycle is null)
            {
                _logger.LogWarning("ignored a lifecycle {Action} forwarded by the supervisor: this process has no lifecycle handler", request.Action);
                return;
            }

            _logger.LogWarning("The supervisor forwarded a lifecycle {Action} (delay {Delay} s, exit code {Code}) requested by {By}", request.Action, request.DelaySeconds, request.ExitCode, request.By);
            lifecycle.Request(request);
        }
        else if (SupervisorProtocol.TryParseLifecycleResult(line, out SupervisorLifecycleResult result))
        {
            lifecycle?.Result(result);
        }
        else
        {
            _logger.LogDebug("ignored a supervisor stdin line that is not 'drain' or a lifecycle line");
        }
    }

    // The host began to stop: say why, from the exit code that will be returned (ServerLifecycleFeature stores it before it calls StopApplication).
    // StopApplication can be called on the world thread, so the line is composed here but written on the thread pool (a full stdout pipe must not stall
    // the world); StopAsync waits a bounded time for the write so the line still precedes the exit.
    private void AnnounceStopping()
    {
        if (string.IsNullOrEmpty(source.SupervisorToken))
        {
            return; // no per-run token, nothing the supervisor could trust
        }

        int code = ExitCodes.Current;
        string line = SupervisorProtocol.FormatStopping(source.SupervisorToken, SupervisorProtocol.IntentFor(code), code);
        _announcement = Task.Run(() =>
        {
            try
            {
                source.Output.WriteLine(line);
                source.Output.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _logger.LogDebug("the stopping announcement could not be written: {Reason}", ex.GetType().Name);
            }
        });
    }

    private void Stop(string reason)
    {
        _logger.LogInformation("Supervised child stopping gracefully: {Reason}", reason);
        lifetime.StopApplication();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stoppingRegistration.Dispose();
        _reader?.Stop();
        try
        {
            await _announcement.WaitAsync(AnnouncementWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogDebug("the stopping announcement was still being written after {Seconds} s", AnnouncementWait.TotalSeconds);
        }
    }
}
