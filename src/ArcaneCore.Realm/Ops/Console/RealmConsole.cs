using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Ops.ServerConsole;
using ArcaneCore.Realm.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.Realm.Ops.ServerConsole;

/// <summary>
/// The realm daemon's console (docs/areas/server-console.md). A background thread reads lines and runs each through
/// <see cref="RealmConsoleCommands"/>; nothing starts when <c>Realm:Console:Enabled</c> is off or input is redirected. The
/// reader is a background thread and <see cref="StopAsync"/> never joins it, so it cannot keep the process alive.
/// Every line is logged as "Realm console command:" with secrets redacted.
/// </summary>
public sealed class RealmConsole : IHostedService
{
    /// <summary>Logger category of the console's lines.</summary>
    public const string LogCategory = "ArcaneCore.Console";

    private readonly RealmConsoleOptions _options;
    private readonly ConsoleInputSource _source;
    private readonly RealmConsoleCommands _commands;
    private readonly ILogger _logger;
    private readonly TextWriter _output;
    private readonly CancellationTokenSource _stopping = new();
    private ConsoleLineReader? _reader;

    public RealmConsole(
        IOptions<RealmConsoleOptions> options,
        ConsoleInputSource source,
        IServiceScopeFactory scopes,
        LogonServer logon,
        TimeProvider time,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory,
        IOptions<RealmLivenessOptions>? liveness = null)
        : this(
            options,
            source,
            new RealmConsoleCommands(
                scopes,
                (logon ?? throw new ArgumentNullException(nameof(logon))) is var validLogon ? () => validLogon.ActiveSessions : null!,
                time,
                lifetime,
                liveness?.Value),
            loggerFactory)
    {
    }

    public RealmConsole(
        IOptions<RealmConsoleOptions> options, ConsoleInputSource source, RealmConsoleCommands commands, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _options = options.Value;
        _source = source;
        _commands = commands;
        _logger = loggerFactory.CreateLogger(LogCategory);
        _output = TextWriter.Synchronized(source.Output);
    }

    /// <summary>The reader thread; null when no reader was started. Exposed for tests.</summary>
    public Thread? ReaderThread => _reader?.Thread;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogDebug("console commands off: Realm:Console:Enabled is false");
            return Task.CompletedTask;
        }

        if (!_source.IsInteractive)
        {
            _logger.LogInformation("console commands off: standard input is redirected or the session is not interactive");
            return Task.CompletedTask;
        }

        _reader = new ConsoleLineReader(_source.Input, "realm-console");
        _reader.Start(Submit, _logger);
        _output.WriteLine(RealmConsoleCommands.Prefix + "Console ready; type 'help' for the commands, 'quit' to stop the realm daemon.");
        return Task.CompletedTask;
    }

    /// <summary>Log one console line (secrets redacted) and run it (reader thread).</summary>
    internal void Submit(string line)
    {
        _logger.LogInformation("Realm console command: {Line}", ConsoleCommandRedactor.Redact(line));
        try
        {
            _commands.ExecuteAsync(line, _output, _stopping.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            _output.WriteLine(RealmConsoleCommands.Prefix + "the realm is stopping; command not run");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Record("realm.console", ex);
            _logger.LogError(ex, "realm console command failed");
            _output.WriteLine(RealmConsoleCommands.Prefix + "The command failed; see the server log.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _reader?.Stop();
        _stopping.Cancel();
        return Task.CompletedTask;
    }
}
