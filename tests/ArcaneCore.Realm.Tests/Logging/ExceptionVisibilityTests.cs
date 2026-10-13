using ArcaneCore.Kernel.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Realm.Tests.Logging;

/// <summary>
/// "I never saw the exception in the server console": an Error with an exception prints its type, message, stack trace and inner
/// exception on the console sink, and an Error or Critical line is not dropped when the console queue is full of ordinary lines.
/// </summary>
public sealed class ExceptionVisibilityTests
{
    /// <summary>A destination that blocks until released, then records every line.</summary>
    private sealed class GatedWriter : ILineWriter
    {
        private readonly ManualResetEventSlim _open = new(false);
        private readonly StringWriter _text = new();

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        public void Release() => _open.Set();

        public void Write(ReadOnlySpan<char> line)
        {
            _open.Wait();
            lock (_text)
            {
                _text.Write(line);
            }
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }

    private static Exception Thrown()
    {
        try
        {
            try
            {
                throw new ArgumentException("inner-cause-marker");
            }
            catch (ArgumentException inner)
            {
                throw new InvalidOperationException("outer-fault-marker", inner);
            }
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    private static (ILogger Logger, ArcaneLoggerProvider Provider, QueuedLineWriter Queue) QueuedConsole(GatedWriter destination, int capacity)
    {
        LogSink console = ArcaneLoggerProvider.Queued("console", destination, capacity, static (name, writer) => new TextLogSink(name, writer, color: false));
        var options = new ArcaneLoggingOptions();
        options.Console.Mode = ConsoleMode.Plain;
        var provider = new ArcaneLoggerProvider(options, colorAllowed: false, [console]);
        return (provider.CreateLogger("Test.Category"), provider, (QueuedLineWriter)console.Writer);
    }

    [Fact]
    public void ErrorWithException_PrintsTypeMessageStackTraceAndInnerException_OnTheConsole()
    {
        var output = new StringWriter();
        var options = new ArcaneLoggingOptions();
        options.Console.Mode = ConsoleMode.Plain;
        using var provider = new ArcaneLoggerProvider(options, colorAllowed: false, [new TextLogSink("console", new TextWriterLineWriter(output), color: false)]);
        ILogger logger = provider.CreateLogger("Test.Category");

        logger.LogError(Thrown(), "Playerbot {BotId} {What} failed", 7, "action");

        string text = output.ToString();
        Assert.Contains(" FAIL Test.Category: Playerbot 7 action failed", text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: outer-fault-marker", text, StringComparison.Ordinal);
        Assert.Contains("System.ArgumentException: inner-cause-marker", text, StringComparison.Ordinal);
        Assert.Contains(" ---> ", text, StringComparison.Ordinal); // how Exception.ToString joins the inner exception
        Assert.Contains("   at ", text, StringComparison.Ordinal);
        Assert.Contains(nameof(Thrown), text, StringComparison.Ordinal); // a stack frame, not just the message
    }

    [Fact]
    public void ErrorLines_AreNotDropped_WhenTheConsoleQueueIsFullOfOrdinaryLines()
    {
        var destination = new GatedWriter();
        (ILogger logger, ArcaneLoggerProvider provider, QueuedLineWriter queue) = QueuedConsole(destination, capacity: 2);
        using (provider)
        {
            logger.LogInformation("held-by-writer");
            Assert.True(SpinWait.SpinUntil(() => queue.Pending == 0, TimeSpan.FromSeconds(5)));
            logger.LogInformation("ordinary-1");
            logger.LogInformation("ordinary-2");
            logger.LogInformation("ordinary-3-dropped");
            Assert.Equal(1, queue.Dropped);

            logger.LogError(Thrown(), "the-error-line");
            logger.LogCritical("the-critical-line");
            destination.Release();
            Assert.True(queue.Flush(TimeSpan.FromSeconds(5)));

            string text = destination.Text;
            Assert.Contains("the-error-line", text, StringComparison.Ordinal);
            Assert.Contains("outer-fault-marker", text, StringComparison.Ordinal);
            Assert.Contains("the-critical-line", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ordinary-3-dropped", text, StringComparison.Ordinal);
            Assert.Equal(0, queue.DroppedPriority);
            Assert.True(text.IndexOf("the-error-line", StringComparison.Ordinal) < text.IndexOf("the-critical-line", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ErrorLines_LostOnlyWhenTheQueueHoldsNothingButErrors_AndTheLossIsReportedAtError()
    {
        var destination = new GatedWriter();
        (ILogger logger, ArcaneLoggerProvider provider, QueuedLineWriter queue) = QueuedConsole(destination, capacity: 2);
        using (provider)
        {
            logger.LogInformation("held-by-writer");
            Assert.True(SpinWait.SpinUntil(() => queue.Pending == 0, TimeSpan.FromSeconds(5)));
            logger.LogError("error-1");
            logger.LogError("error-2");
            logger.LogError("error-3-lost");
            Assert.Equal(1, queue.DroppedPriority);

            destination.Release();
            Assert.True(queue.Flush(TimeSpan.FromSeconds(5)));

            string text = destination.Text;
            Assert.Contains("error-1", text, StringComparison.Ordinal);
            Assert.Contains("error-2", text, StringComparison.Ordinal);
            Assert.DoesNotContain("error-3-lost", text, StringComparison.Ordinal);
            Assert.Contains(" FAIL ArcaneCore.Logging: console sink LOST 1 Error/Critical log line(s)", text, StringComparison.Ordinal);
        }
    }
}
