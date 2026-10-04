using ArcaneCore.Kernel.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ArcaneCore.Realm.Tests.Logging;

/// <summary>
/// The hot path (world thread logging a constant template through the factory into the text sinks) must not allocate: the line is
/// formatted into a stack buffer and handed to the writer as a span. Measured with <see cref="GC.GetAllocatedBytesForCurrentThread"/>
/// after a warm-up that lets tiering and pools settle.
/// </summary>
public sealed class LoggingAllocationTests
{
    private const int Iterations = 2_000;

    [Fact]
    public void ConstantTemplate_ThroughFactoryAndTextSink_AllocatesNothing()
    {
        var options = new ArcaneLoggingOptions();
        options.Console.Mode = ConsoleMode.Plain;
        LogSink[] sinks = [new TextLogSink("console", NullLineWriter.Instance, color: false), new TextLogSink("file", NullLineWriter.Instance, color: false)];
        using var provider = new ArcaneLoggerProvider(options, colorAllowed: false, sinks);
        using ILoggerFactory factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        ILogger logger = factory.CreateLogger("ArcaneCore.World.Net.WorldServer");

        long allocated = Measure(() => logger.LogInformation("World tick completed on schedule"), Iterations);

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ConstantTemplate_WithEventIdAndColor_AllocatesNothing()
    {
        var options = new ArcaneLoggingOptions();
        options.Console.Mode = ConsoleMode.Color;
        LogSink[] sinks = [new TextLogSink("console", NullLineWriter.Instance, color: true)];
        using var provider = new ArcaneLoggerProvider(options, colorAllowed: true, sinks);
        using ILoggerFactory factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        ILogger logger = factory.CreateLogger("ArcaneCore.Game.Maps.Map");
        var perf = new EventId(1, "Perf");

        long allocated = Measure(() => logger.LogWarning(perf, "Slow world update"), Iterations);

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Formatter_TextLine_AllocatesNothing()
    {
        var evt = new LogEvent(new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc), LogLevel.Information, "ArcaneCore.Realm.Net.LogonServer", new EventId(5, "Auth"), "Realm list sent", null);
        long allocated = Measure(
            () =>
            {
                var buffer = new LogBuffer(stackalloc char[512]);
                LogLineFormatter.Write(ref buffer, in evt, null, color: true);
                buffer.Dispose();
            },
            Iterations);

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Formatter_JsonLine_WithoutState_AllocatesNothing()
    {
        var evt = new LogEvent(new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc), LogLevel.Information, "ArcaneCore.Realm.Net.LogonServer", new EventId(5, "Auth"), "Realm list sent", null);
        long allocated = Measure(
            () =>
            {
                var buffer = new LogBuffer(stackalloc char[512]);
                JsonLineFormatter.Write<object?>(ref buffer, in evt, null, null);
                buffer.Dispose();
            },
            Iterations);

        Assert.Equal(0, allocated);
    }

    private static long Measure(Action action, int iterations)
    {
        for (int i = 0; i < iterations; i++)
        {
            action();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            action();
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
