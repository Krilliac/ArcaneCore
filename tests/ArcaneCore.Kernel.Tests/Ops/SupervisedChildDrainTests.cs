using System.Collections.Concurrent;
using ArcaneCore.Kernel.Ops.ServerConsole;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Ops;

/// <summary>
/// The child side of the cluster supervisor's drain: the stdin line <c>drain</c> (and end of input) stops a supervised child through
/// <see cref="IHostApplicationLifetime.StopApplication"/>, the graceful path; an unsupervised process ignores it.
/// </summary>
public sealed class SupervisedChildDrainTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private sealed class BlockingReader : TextReader
    {
        private readonly BlockingCollection<string?> _lines = [];

        public void Post(string? line) => _lines.Add(line);

        public override string? ReadLine() => _lines.Take();
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private int _stops;

        public int Stops => Volatile.Read(ref _stops);

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Interlocked.Increment(ref _stops);
    }

    private static ConsoleInputSource Source(TextReader reader, bool supervised, bool redirected = true)
        => new(reader, new StringWriter(), IsInputRedirected: redirected, IsUserInteractive: true, IsSupervisedChild: supervised);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for a condition");
            await Task.Delay(5);
        }
    }

    [Theory]
    [InlineData("drain")]
    [InlineData("  DRAIN  ")]
    public async Task Supervised_DrainLine_CallsStopApplication_WhileInputStaysOpen(string line)
    {
        var reader = new BlockingReader();
        var lifetime = new FakeLifetime();
        var drain = new SupervisedChildDrain(Source(reader, supervised: true), lifetime, NullLoggerFactory.Instance);

        await drain.StartAsync(CancellationToken.None);
        reader.Post(line);

        await WaitForAsync(() => lifetime.Stops >= 1); // the line did it: stdin is still open
        reader.Post(null);
        await drain.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Supervised_EndOfInput_CallsStopApplication()
    {
        var reader = new BlockingReader();
        var lifetime = new FakeLifetime();
        var drain = new SupervisedChildDrain(Source(reader, supervised: true), lifetime, NullLoggerFactory.Instance);

        await drain.StartAsync(CancellationToken.None);
        reader.Post(null); // the supervisor closed stdin or died

        await WaitForAsync(() => lifetime.Stops >= 1);
        await drain.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Supervised_OtherLines_AreIgnored()
    {
        var reader = new BlockingReader();
        var lifetime = new FakeLifetime();
        var drain = new SupervisedChildDrain(Source(reader, supervised: true), lifetime, NullLoggerFactory.Instance);

        await drain.StartAsync(CancellationToken.None);
        reader.Post("server shutdown 5");
        reader.Post("drains");
        await Task.Delay(100);

        Assert.Equal(0, lifetime.Stops);
        reader.Post(null);
        await drain.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(false, true)] // not supervised: "drain" and end of input behave as before (nothing reads, nothing stops)
    [InlineData(true, false)] // supervised but stdin is a console: the daemon console owns it
    public async Task NotASupervisedRedirectedChild_StartsNoReader_AndNeverStops(bool supervised, bool redirected)
    {
        var reader = new BlockingReader();
        var lifetime = new FakeLifetime();
        var drain = new SupervisedChildDrain(Source(reader, supervised, redirected), lifetime, NullLoggerFactory.Instance);

        await drain.StartAsync(CancellationToken.None);
        reader.Post("drain");
        reader.Post(null);
        await Task.Delay(100);

        Assert.Null(drain.ReaderThread);
        Assert.Equal(0, lifetime.Stops);
        await drain.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SupervisedHost_ExitsGracefully_WhenItReadsDrain()
    {
        var reader = new BlockingReader();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton(Source(reader, supervised: true));
        builder.Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        builder.Services.AddHostedService<SupervisedChildDrain>();
        using IHost host = builder.Build();

        Task run = host.RunAsync();
        await Task.Delay(100);
        reader.Post("drain");

        await run.WaitAsync(Bound); // the host stopped inside the drain window
    }
}
