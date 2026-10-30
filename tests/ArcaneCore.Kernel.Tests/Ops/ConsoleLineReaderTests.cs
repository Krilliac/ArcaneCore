using System.Collections.Concurrent;
using ArcaneCore.Kernel.Ops.ServerConsole;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Ops;

/// <summary>The console's reader thread: line delivery, end of input, handler failures and a stop that never joins.</summary>
public sealed class ConsoleLineReaderTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private sealed class ListLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message, Exception? Error)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }

    private sealed class BlockingReader : TextReader
    {
        private readonly BlockingCollection<string?> _lines = [];

        public void Post(string? line) => _lines.Add(line);

        public override string? ReadLine() => _lines.Take();
    }

    private sealed class ThrowingReader(Exception error) : TextReader
    {
        public override string? ReadLine() => throw error;
    }

    private static async Task<bool> JoinAsync(Thread thread) => await Task.Run(() => thread.Join(Bound));

    private static async Task WaitAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for a condition");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Reader_DeliversTrimmedNonEmptyLines_InOrder_AndStopsAtEndOfInput()
    {
        var logger = new ListLogger();
        var reader = new ConsoleLineReader(new StringReader("  first  \n\n   \nsecond\r\n\tthird with  spaces \t\n"), "test-console");
        var received = new List<string>();

        reader.Start(received.Add, logger);
        Thread thread = Assert.IsType<Thread>(reader.Thread);

        Assert.True(await JoinAsync(thread), "the reader must end at end of input");
        Assert.Equal(["first", "second", "third with  spaces"], received);
        Assert.Equal("test-console", thread.Name);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("console input closed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reader_EmptyInput_EndsWithoutDeliveringAnything()
    {
        var reader = new ConsoleLineReader(new StringReader(string.Empty), "test-console");
        var received = new List<string>();

        reader.Start(received.Add, new ListLogger());

        Assert.True(await JoinAsync(reader.Thread!));
        Assert.Empty(received);
    }

    [Fact]
    public async Task Reader_HandlerException_IsLogged_AndReadingContinues()
    {
        var logger = new ListLogger();
        var received = new List<string>();
        var reader = new ConsoleLineReader(new StringReader("one\nboom\nthree\n"), "test-console");

        reader.Start(line =>
        {
            received.Add(line);
            if (line == "boom")
            {
                throw new InvalidOperationException("handler failed");
            }
        }, logger);

        Assert.True(await JoinAsync(reader.Thread!));
        Assert.Equal(["one", "boom", "three"], received);
        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.IsType<InvalidOperationException>(error.Error);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(ObjectDisposedException))]
    public async Task Reader_ClosedInput_EndsTheReaderAndIsLogged(Type errorType)
    {
        var logger = new ListLogger();
        Exception error = errorType == typeof(ObjectDisposedException) ? new ObjectDisposedException("stdin") : new IOException("pipe closed");
        var reader = new ConsoleLineReader(new ThrowingReader(error), "test-console");

        reader.Start(_ => { }, logger);

        Assert.True(await JoinAsync(reader.Thread!));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("console input closed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reader_Stop_ReturnsAtOnce_AndTheBlockedReadIsNotJoined()
    {
        var input = new BlockingReader();
        var received = new ConcurrentQueue<string>();
        var reader = new ConsoleLineReader(input, "test-console");
        reader.Start(received.Enqueue, NullLogger.Instance);
        input.Post("before");
        await WaitAsync(() => received.Count == 1);

        Assert.True(reader.Thread!.IsBackground, "a background thread cannot keep the process alive");
        // The read is blocked until "after" is posted below, so a Stop that joined it would never return; the bound only turns a hang
        // into a failure.
        try
        {
            await Task.Run(reader.Stop).WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            Assert.Fail("Stop waited for the blocked read");
        }
        Assert.True(reader.Thread!.IsAlive, "the read is blocked and cannot be interrupted; Stop must not wait for it");

        // The line that unblocks the read after Stop is dropped, and the thread then ends.
        input.Post("after");
        Assert.True(await JoinAsync(reader.Thread));
        Assert.Equal(["before"], received.ToArray());
    }

    [Fact]
    public void Reader_Start_TwiceThrows_AndNullArgumentsAreRejected()
    {
        var input = new BlockingReader();
        var reader = new ConsoleLineReader(input, "test-console");
        Assert.Throws<ArgumentNullException>(() => reader.Start(null!, NullLogger.Instance));
        Assert.Throws<ArgumentNullException>(() => reader.Start(_ => { }, null!));
        Assert.Null(reader.Thread);

        reader.Start(_ => { }, NullLogger.Instance);
        try
        {
            Assert.Throws<InvalidOperationException>(() => reader.Start(_ => { }, NullLogger.Instance));
        }
        finally
        {
            reader.Stop();
            input.Post(null);
        }
    }
}
