using System.Diagnostics;
using ArcaneCore.Kernel.Ops;
using ArcaneCore.Kernel.Ops.ServerConsole;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Kernel.Tests.Ops;

/// <summary>
/// <see cref="SupervisedChildDrain"/> as the container builds it, and <c>StopAsync</c>'s handling of the host's shutdown token.
/// These change or read <see cref="ExitCodes.Current"/> (process-global), so they run in the non-parallel collection.
/// </summary>
[Collection(ProcessGlobalStateCollection.Name)]
public sealed class SupervisedChildDrainContainerTests
{
    private sealed class StubLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.Cancel();
    }

    private sealed class EmptyReader : TextReader
    {
        private readonly ManualResetEventSlim _end = new(false);

        public void End() => _end.Set();

        public override string? ReadLine()
        {
            _end.Wait();
            return null;
        }
    }

    private sealed class NeverFinishingWriter : StringWriter
    {
        public ManualResetEventSlim Release { get; } = new(false);

        public override void WriteLine(string? value) => Release.Wait();
    }

    [Fact]
    public async Task Container_IgnoresARegisteredFuncOfInt_AndAnnouncesExitCodesCurrent()
    {
        int previous = ExitCodes.Current;
        var reader = new EmptyReader();
        var output = new StringWriter();
        var lifetime = new StubLifetime();
        var services = new ServiceCollection();
        services.AddSingleton(new ConsoleInputSource(reader, output, IsInputRedirected: true, IsUserInteractive: true, IsSupervisedChild: true, SupervisorProtocol: true, SupervisorToken: "tok"));
        services.AddSingleton<IHostApplicationLifetime>(lifetime);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton<Func<int>>(static () => 99); // registered for some other purpose: must not become the exit-code source
        services.AddSingleton<SupervisedChildDrain>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        try
        {
            ExitCodes.Current = ExitCodes.Restart;
            SupervisedChildDrain drain = provider.GetRequiredService<SupervisedChildDrain>();

            await drain.StartAsync(CancellationToken.None);
            lifetime.StopApplication();
            await drain.StopAsync(CancellationToken.None);

            Assert.Equal($"lifecycle stopping token=tok intent=restart code=2{Environment.NewLine}", output.ToString());
        }
        finally
        {
            ExitCodes.Current = previous;
            reader.End();
        }
    }

    [Fact]
    public async Task StopAsync_HonoursItsCancellationToken_WhileTheAnnouncementNeverCompletes()
    {
        var reader = new EmptyReader();
        var output = new NeverFinishingWriter();
        var lifetime = new StubLifetime();
        var source = new ConsoleInputSource(reader, output, IsInputRedirected: true, IsUserInteractive: true, IsSupervisedChild: true, SupervisorProtocol: true, SupervisorToken: "tok");
        var drain = new SupervisedChildDrain(source, lifetime, NullLoggerFactory.Instance, exitCode: static () => ExitCodes.Success);
        using var cts = new CancellationTokenSource();
        try
        {
            await drain.StartAsync(CancellationToken.None);
            lifetime.StopApplication(); // starts the announcement, which blocks in WriteLine

            cts.CancelAfter(TimeSpan.FromMilliseconds(200));
            var watch = Stopwatch.StartNew();
            await drain.StopAsync(cts.Token); // must swallow the cancellation, not throw
            watch.Stop();

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"StopAsync took {watch.Elapsed.TotalMilliseconds:0} ms; AnnouncementWait is {SupervisedChildDrain.AnnouncementWait.TotalMilliseconds:0} ms and the token fired at 200 ms");
            Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(150), $"StopAsync returned after {watch.Elapsed.TotalMilliseconds:0} ms, before the token fired");
        }
        finally
        {
            output.Release.Set();
            reader.End();
        }
    }

    [Fact]
    public async Task StopAsync_WithAnAlreadyCancelledToken_ReturnsWithoutThrowing()
    {
        var reader = new EmptyReader();
        var output = new NeverFinishingWriter();
        var lifetime = new StubLifetime();
        var source = new ConsoleInputSource(reader, output, IsInputRedirected: true, IsUserInteractive: true, IsSupervisedChild: true, SupervisorProtocol: true, SupervisorToken: "tok");
        var drain = new SupervisedChildDrain(source, lifetime, NullLoggerFactory.Instance, exitCode: static () => ExitCodes.Success);
        try
        {
            await drain.StartAsync(CancellationToken.None);
            lifetime.StopApplication();

            var watch = Stopwatch.StartNew();
            await drain.StopAsync(new CancellationToken(canceled: true));

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1.5), $"StopAsync took {watch.Elapsed.TotalMilliseconds:0} ms with a token that was already cancelled");
        }
        finally
        {
            output.Release.Set();
            reader.End();
        }
    }
}
