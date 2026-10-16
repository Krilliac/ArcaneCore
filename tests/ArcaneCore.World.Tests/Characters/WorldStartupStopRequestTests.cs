using System.Collections.Concurrent;
using ArcaneCore.Data;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace ArcaneCore.World.Tests.Characters;

/// <summary>
/// A stop request (Ctrl-C, Ctrl-Break, SIGTERM) during the database step of start-up, before the host's console lifetime listens, is answered with a log
/// line and a clean exit, never a silent process end. The 2026-10-16 22:44 live incident: a World interrupted there ended with STATUS_CONTROL_C_EXIT and
/// nothing after the writer-lock line (no exception, no crash report, no event log entry). A stop while the hosted services start is
/// <see cref="WorldStopDuringStartupTests"/>.
/// </summary>
public sealed class WorldStartupStopRequestTests
{
    [Fact]
    public async Task AStopRequestDuringTheDatabaseStep_IsLogged_TheStepFinishes_AndTheWorldDoesNotStart()
    {
        using var directory = new TempDirectory();
        string database = Path.Combine(directory.Path, "characters.db");
        var log = new CapturingLoggerProvider();
        var started = new StartRecorder();
        IHost host = BuildHost(DatabaseOptions(database), log, _ => started);
        var listener = new FakeStopListener();
        bool stepFinished = false;

        int code = await WorldStartup.RunAsync(
            host,
            async () =>
            {
                listener.Raise("SIGINT"); // Ctrl-C half-way through the schema initializers
                await Task.Yield();
                stepFinished = true;
            },
            new StringWriter(),
            () => 0,
            listener.Listen);

        Assert.Equal(0, code);
        Assert.True(stepFinished); // the database step is never cut in half
        Assert.False(started.Started); // and the world does not start afterwards
        Assert.True(listener.Disposed); // the host's own console lifetime takes over from here
        Assert.Contains(log.Lines, l => l.Contains("Stop requested (SIGINT) during start-up", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Contains("stopped on request (SIGINT) before the world started", StringComparison.Ordinal));
        using CharacterDatabaseWriterLock released = CharacterDatabaseWriterLock.TryAcquire(DatabaseOptions(database).Value.Resolve(DatabaseComponent.Characters))!;
        Assert.NotNull(released); // the container was disposed: lock released, log queue drained
    }

    [Fact]
    public async Task ARefusedDatabaseStep_IsInTheLogAsWellAsOnStandardError()
    {
        using var directory = new TempDirectory();
        string database = Path.Combine(directory.Path, "characters.db");
        using var owner = new WorldWriterGuard(DatabaseOptions(database));
        owner.Acquire();
        var log = new CapturingLoggerProvider();
        IHost host = BuildHost(DatabaseOptions(database), log, _ => new StartRecorder());
        var error = new StringWriter();

        int code = await WorldStartup.RunAsync(host, () => throw new InvalidOperationException("must not initialize"), error, () => 0, new FakeStopListener().Listen);

        Assert.NotEqual(0, code);
        Assert.Contains("already owned by another world process", error.ToString());
        Assert.Contains(log.Lines, l => l.Contains("refused by the database step", StringComparison.Ordinal) && l.Contains("already owned", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDefaultListener_RegistersAndReleasesTheSignals()
    {
        var requests = new List<string>();
        using (IDisposable listening = WorldStartup.ListenForStopSignals(requests.Add))
        {
            Assert.NotNull(listening);
        }

        Assert.Empty(requests);
    }

    private static IHost BuildHost(IOptions<DatabaseOptions> database, CapturingLoggerProvider log, Func<IHostApplicationLifetime, IHostedService> service)
        => new HostBuilder().ConfigureLogging(logging => logging.AddProvider(log)).ConfigureServices(services =>
        {
            services.AddSingleton(database);
            services.AddSingleton<WorldWriterGuard>();
            services.AddSingleton<IHostedService>(sp => service(sp.GetRequiredService<IHostApplicationLifetime>()));
        }).Build();

    private static IOptions<DatabaseOptions> DatabaseOptions(string database)
        => Options.Create(new DatabaseOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = $"Data Source={database}" });

    /// <summary>Stands in for the console signal registrations: <see cref="Raise"/> is a Ctrl-C.</summary>
    private sealed class FakeStopListener
    {
        private Action<string>? _onStop;

        public bool Disposed { get; private set; }

        public IDisposable Listen(Action<string> onStop)
        {
            _onStop = onStop;
            return new Registration(this);
        }

        public void Raise(string request)
        {
            Assert.False(Disposed, "a stop request after the listener was released would not reach the start-up");
            _onStop!(request);
        }

        private sealed class Registration(FakeStopListener owner) : IDisposable
        {
            public void Dispose() => owner.Disposed = true;
        }
    }

    private sealed class StartRecorder : IHostedService
    {
        public bool Started { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines;

        public ILogger CreateLogger(string categoryName) => new Logger(_lines);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => lines.Enqueue(formatter(state, exception));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "arcanecore-startupstop-" + Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
