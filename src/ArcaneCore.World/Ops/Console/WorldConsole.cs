using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Ops.ServerConsole;

/// <summary>
/// The world daemon's console (docs/areas/server-console.md; mangosd CliRunnable, cmangos mangos-classic
/// CliRunnable.cpp:147). A background thread reads lines; each is queued to the world thread
/// (<see cref="WorldRuntime.InvokeAsync{T}"/>, the <c>World::QueueCliCommand</c> equivalent) and run by
/// <see cref="WorldConsoleExecutor"/>. Nothing starts when the option is off or input is redirected.
/// <para>
/// Threads: <see cref="StartAsync"/>/<see cref="StopAsync"/> on the host thread; <see cref="Submit"/> on the reader thread;
/// the command itself on the world thread. <see cref="StopAsync"/> never joins the reader (a blocked ReadLine cannot be
/// interrupted); the reader is a background thread, so it cannot keep the process alive.
/// </para>
/// </summary>
public sealed class WorldConsole : IHostedService
{
    /// <summary>Logger category of the "Console command:" lines.</summary>
    public const string LogCategory = "ArcaneCore.Console";

    /// <summary>How long the reader waits for the world thread to run a command.</summary>
    public static readonly TimeSpan WorldWait = TimeSpan.FromSeconds(30);

    private readonly WorldConsoleOptions _options;
    private readonly ConsoleInputSource _source;
    private readonly IServiceScopeFactory _scopes;
    private readonly OpcodeTable _opcodes;
    private readonly WorldRuntime _world;
    private readonly SessionRegistry _registry;
    private readonly WorldSessionOptions _sessionOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly TextWriter _output;
    private readonly CancellationTokenSource _stopping = new();
    private AsyncServiceScope _scope;
    private bool _hasScope;
    private WorldConsoleExecutor? _executor;
    private ConsoleLineReader? _reader;

    public WorldConsole(
        IOptions<WorldConsoleOptions> options,
        ConsoleInputSource source,
        IServiceScopeFactory scopes,
        OpcodeTable opcodes,
        WorldRuntime world,
        SessionRegistry registry,
        IOptions<WorldSessionOptions> sessionOptions,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _source = source;
        _scopes = scopes;
        _opcodes = opcodes;
        _world = world;
        _registry = registry;
        _sessionOptions = sessionOptions.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger(LogCategory);
        _output = TextWriter.Synchronized(source.Output);
    }

    /// <summary>The reader thread; null when no reader was started (option off, input redirected, not yet started). Exposed for tests.</summary>
    public Thread? ReaderThread => _reader?.Thread;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogDebug("console commands off: World:Console:Enabled is false");
            return Task.CompletedTask;
        }

        if (!_source.IsInteractive)
        {
            _logger.LogInformation("console commands off: standard input is redirected or the session is not interactive");
            return Task.CompletedTask;
        }

        _scope = _scopes.CreateAsyncScope();
        _hasScope = true;
        _executor = WorldConsoleExecutor.Create(
            _scope.ServiceProvider, _opcodes, _world, _registry, _sessionOptions, _loggerFactory.CreateLogger<WorldSession>(), _output);
        _reader = new ConsoleLineReader(_source.Input, "world-console");
        _reader.Start(Submit, _logger);
        _executor.WriteLine("Console ready; type 'help' for the commands, 'quit' to stop the server.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Log one console line (secrets redacted), run it on the world thread and wait for it (reader thread). A stopping world
    /// answers "command not run" instead of hanging.
    /// </summary>
    internal void Submit(string line)
    {
        WorldConsoleExecutor? executor = _executor;
        if (executor is null)
        {
            return;
        }

        _logger.LogInformation("Console command: {Line}", ConsoleCommandRedactor.Redact(line));
        try
        {
            Task<int> run = _world.InvokeAsync(() =>
            {
                executor.Execute(line);
                return 0;
            }, _stopping.Token);
            if (!run.Wait(WorldWait, _stopping.Token))
            {
                executor.WriteLine("the world did not run the command within " + (int)WorldWait.TotalSeconds + " s; it stays queued");
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is AggregateException { InnerException: OperationCanceledException })
        {
            executor.WriteLine("the world is stopping; command not run");
        }
        catch (AggregateException ex)
        {
            _logger.LogError(ex.InnerException ?? ex, "console command could not be run");
            executor.WriteLine("The command failed; see the server log.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _reader?.Stop();
        _stopping.Cancel();
        if (_hasScope)
        {
            _hasScope = false;
            _ = _scope.DisposeAsync().AsTask();
        }

        return Task.CompletedTask;
    }
}
