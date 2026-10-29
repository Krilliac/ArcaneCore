using System.Collections.Concurrent;
using ArcaneCore.Kernel.Ops;
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
        private readonly CancellationTokenSource _stopping = new();
        private int _stops;

        public int Stops => Volatile.Read(ref _stops);

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            Interlocked.Increment(ref _stops);
            _stopping.Cancel(); // runs the ApplicationStopping callbacks on this thread, as the real host does
        }
    }

    private sealed class RecordingHandler : ISupervisedLifecycleRequestHandler
    {
        public ConcurrentQueue<SupervisorLifecycleRequest> Requests { get; } = new();

        public ConcurrentQueue<SupervisorLifecycleResult> Results { get; } = new();

        public void Request(SupervisorLifecycleRequest request) => Requests.Enqueue(request);

        public void Result(SupervisorLifecycleResult result) => Results.Enqueue(result);
    }

    private static ConsoleInputSource ProtocolSource(TextReader reader, TextWriter output, bool protocol = true, string? token = "tok")
        => new(reader, output, IsInputRedirected: true, IsUserInteractive: true, IsSupervisedChild: true, SupervisorProtocol: protocol, SupervisorToken: token);

    [Theory]
    [InlineData(2, "restart")]
    [InlineData(0, "shutdown")]
    [InlineData(5, "failure")]
    public async Task SupervisedChildDrain_Stopping_AnnouncesIntentAndCode(int code, string intent)
    {
        int saved = ExitCodes.Current;
        var reader = new BlockingReader();
        var output = new StringWriter();
        var lifetime = new FakeLifetime();
        var drain = new SupervisedChildDrain(ProtocolSource(reader, output), lifetime, NullLoggerFactory.Instance);
        try
        {
            await drain.StartAsync(CancellationToken.None);
            ExitCodes.Current = code;

            lifetime.StopApplication(); // returns before the line is written: the write is off the calling (world) thread

            await drain.StopAsync(CancellationToken.None); // waits (bounded) for the announcement
            Assert.Equal($"lifecycle stopping token=tok intent={intent} code={code}{Environment.NewLine}", output.ToString());
        }
        finally
        {
            ExitCodes.Current = saved;
            reader.Post(null);
            await drain.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SupervisedChildDrain_NoProtocolEnv_AnnouncesNothing()
    {
        var reader = new BlockingReader();
        var output = new StringWriter();
        var lifetime = new FakeLifetime();
        var drain = new SupervisedChildDrain(ProtocolSource(reader, output, protocol: false), lifetime, NullLoggerFactory.Instance);

        await drain.StartAsync(CancellationToken.None);
        lifetime.StopApplication();

        reader.Post(null);
        await drain.StopAsync(CancellationToken.None);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public async Task SupervisedChildDrain_Stopping_WithoutAToken_AnnouncesNothing()
    {
        var reader = new BlockingReader();
        var output = new StringWriter();
        var lifetime = new FakeLifetime();
        var drain = new SupervisedChildDrain(ProtocolSource(reader, output, token: null), lifetime, NullLoggerFactory.Instance);

        await drain.StartAsync(CancellationToken.None);
        lifetime.StopApplication();

        reader.Post(null);
        await drain.StopAsync(CancellationToken.None);
        Assert.Equal(string.Empty, output.ToString());
    }

    private sealed class BlockedWriter : StringWriter
    {
        public ManualResetEventSlim Release { get; } = new(false);

        public override void WriteLine(string? value)
        {
            Release.Wait();
            base.WriteLine(value);
        }
    }

    [Fact]
    public async Task SupervisedChildDrain_Stopping_DoesNotBlockTheCallerOnAStalledPipe()
    {
        var reader = new BlockingReader();
        var output = new BlockedWriter();
        var lifetime = new FakeLifetime();
        var drain = new SupervisedChildDrain(ProtocolSource(reader, output), lifetime, NullLoggerFactory.Instance);
        try
        {
            await drain.StartAsync(CancellationToken.None);

            lifetime.StopApplication(); // would hang here if the write ran on the calling thread

            output.Release.Set();
            await drain.StopAsync(CancellationToken.None);
            Assert.Equal($"lifecycle stopping token=tok intent=shutdown code=0{Environment.NewLine}", output.ToString());
        }
        finally
        {
            output.Release.Set();
            reader.Post(null);
        }
    }

    [Fact]
    public async Task SupervisedChildDrain_ForwardedLifecycleLine_RequestsTheCommand()
    {
        var reader = new BlockingReader();
        var lifetime = new FakeLifetime();
        var handler = new RecordingHandler();
        var drain = new SupervisedChildDrain(ProtocolSource(reader, new StringWriter()), lifetime, NullLoggerFactory.Instance, handler);

        await drain.StartAsync(CancellationToken.None);
        reader.Post("lifecycle restart delay=30 code=2 by=Admin");
        reader.Post("lifecycle result realm=4 status=unknown-realm by=Admin");
        reader.Post("lifecycle restart delay=notanumber code=2 by=Admin");

        await WaitForAsync(() => handler.Requests.Count >= 1 && handler.Results.Count >= 1);
        await Task.Delay(50);
        SupervisorLifecycleRequest request = Assert.Single(handler.Requests);
        Assert.Equal(SupervisorLifecycleAction.Restart, request.Action);
        Assert.Equal(30u, request.DelaySeconds);
        Assert.Equal(2, request.ExitCode);
        Assert.Equal("Admin", request.By);
        SupervisorLifecycleResult result = Assert.Single(handler.Results);
        Assert.Equal(SupervisorLifecycleStatus.UnknownRealm, result.Status);
        Assert.Equal(4, result.RealmId);
        Assert.Equal(0, lifetime.Stops); // a lifecycle line is a request to the lifecycle feature, never a direct stop

        reader.Post("drain"); // the drain line still works beside the new lines
        await WaitForAsync(() => lifetime.Stops >= 1);
        reader.Post(null);
        await drain.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SupervisedChildDrain_ForwardedLifecycleLine_WithoutAHandler_IsIgnored()
    {
        var reader = new BlockingReader();
        var lifetime = new FakeLifetime();
        var drain = new SupervisedChildDrain(ProtocolSource(reader, new StringWriter()), lifetime, NullLoggerFactory.Instance);

        await drain.StartAsync(CancellationToken.None);
        reader.Post("lifecycle shutdown delay=0 code=0 by=Admin");
        await Task.Delay(100);

        Assert.Equal(0, lifetime.Stops);
        reader.Post(null);
        await drain.StopAsync(CancellationToken.None);
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
