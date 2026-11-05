using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// A host that fails while it stops or while it is disposed. 2026-11-04, split cluster: the failure escaped <see cref="WorldStartup.RunAsync"/> (out of <c>Host.DisposeAsync</c> in the incident; a hosted service's failed
/// <c>StopAsync</c> escapes from <c>WaitForShutdownAsync</c> the same way) into the crash handler's fail-fast, which writes a full dump. Now it is logged critical, recorded in the fault ledger once, disposal is still attempted,
/// and the run ends with a failure exit code (the relaunch delay of the supervisor is the same as after a fail-fast; the dump is what goes away). These run the real generic host: the disposal failure is a container-owned
/// singleton whose disposal throws, the stop failures are hosted services whose <c>StopAsync</c> throws.
/// </summary>
[Collection(ExitCodesCollection.Name)]
public sealed class WorldStartupDisposeFailureTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private const string Site = "world.host-dispose";

    private sealed class DisposeFailure(string message) : Exception(message);

    private sealed class FailsOnDispose : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => throw new DisposeFailure("the stop failed while the host was disposed");
    }

    /// <summary>Resolves the failing singleton (so the container owns it) and asks the host to stop as soon as it started.</summary>
    private sealed class StopsAtOnce(IServiceProvider services, IHostApplicationLifetime lifetime) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = services.GetRequiredService<FailsOnDispose>();
            lifetime.StopApplication();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private const string StopSite = "world.host-stop";

    private sealed class StopFailure(string message) : Exception(message);

    private sealed class DisposeProbe : IDisposable
    {
        private int _disposed;

        public bool Disposed => Volatile.Read(ref _disposed) == 1;

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    /// <summary>Starts at once; its stop throws, as <c>WorldHost</c> does when a feature's shutdown fails (the AggregateException "World feature shutdown failed").</summary>
    private sealed class ThrowsOnStop : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => throw new StopFailure("World feature shutdown failed");
    }

    /// <summary>A start that is still running when the stop is requested: it asks for the stop and then waits for the cancellation that follows.</summary>
    private sealed class StopsDuringStart(IHostApplicationLifetime lifetime) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            lifetime.StopApplication();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static long RecordedStopFaults() => FaultLedger.Shared.Snapshot().Where(f => f.Site == StopSite && f.ExceptionType.Contains(nameof(StopFailure), StringComparison.Ordinal)).Sum(f => f.Count);

    [Fact]
    public async Task AHostedServiceThatFailsToStop_EndsTheRunWithAFailureExitCode_RecordsOnce_AndStillDisposes()
    {
        int before = ExitCodes.Current;
        long faults = RecordedStopFaults();
        var probe = new DisposeProbe();
        try
        {
            ExitCodes.Current = ExitCodes.Success;
            IHost host = new HostBuilder().ConfigureServices(services =>
            {
                services.AddSingleton(_ => probe);
                services.AddSingleton<IHostedService>(sp => new StopsDuringStartTouch(sp, sp.GetRequiredService<IHostApplicationLifetime>()));
                services.AddSingleton<IHostedService, ThrowsOnStop>();
            }).Build();

            int code = await WorldStartup.RunAsync(host, () => Task.CompletedTask, new StringWriter(), () => ExitCodes.Current).WaitAsync(Deadline);

            Assert.Equal(ExitCodes.Failure, code);
            Assert.Equal(ExitCodes.Failure, ExitCodes.Current);
            Assert.Equal(1, RecordedStopFaults() - faults);
            Assert.True(probe.Disposed, "the container must still be disposed after a failed stop");
        }
        finally
        {
            ExitCodes.Current = before;
        }
    }

    /// <summary>Resolves the probe (so the container owns and disposes it) and asks the host to stop once it started.</summary>
    private sealed class StopsDuringStartTouch(IServiceProvider services, IHostApplicationLifetime lifetime) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = services.GetRequiredService<DisposeProbe>();
            lifetime.StopApplication();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task AStopRequestedDuringStart_WhoseStopFails_EndsTheRunWithAFailureExitCode_AndStillDisposes()
    {
        int before = ExitCodes.Current;
        long faults = RecordedStopFaults();
        var probe = new DisposeProbe();
        try
        {
            ExitCodes.Current = ExitCodes.Success;
            IHost host = new HostBuilder().ConfigureServices(services =>
            {
                services.AddSingleton(_ => probe);
                services.AddSingleton<IHostedService>(sp => new StopsDuringStartTouch(sp, sp.GetRequiredService<IHostApplicationLifetime>()));
                services.AddSingleton<IHostedService, ThrowsOnStop>();
                services.AddSingleton<IHostedService>(sp => new StopsDuringStart(sp.GetRequiredService<IHostApplicationLifetime>()));
            }).Build();

            int code = await WorldStartup.RunAsync(host, () => Task.CompletedTask, new StringWriter(), () => ExitCodes.Current).WaitAsync(Deadline);

            Assert.Equal(ExitCodes.Failure, code);
            Assert.Equal(1, RecordedStopFaults() - faults);
            Assert.True(probe.Disposed);
        }
        finally
        {
            ExitCodes.Current = before;
        }
    }

    private static IHost Build() => new HostBuilder().ConfigureServices(services =>
    {
        services.AddSingleton(_ => new FailsOnDispose());
        services.AddSingleton<IHostedService>(sp => new StopsAtOnce(sp, sp.GetRequiredService<IHostApplicationLifetime>()));
    }).Build();

    private static long RecordedFaults() => FaultLedger.Shared.Snapshot().Where(f => f.Site == Site && f.ExceptionType.Contains(nameof(DisposeFailure), StringComparison.Ordinal)).Sum(f => f.Count);

    [Fact]
    public async Task AFailedDispose_EndsTheRunWithAFailureExitCode_AndDoesNotEscape()
    {
        int before = ExitCodes.Current;
        long faults = RecordedFaults();
        try
        {
            ExitCodes.Current = ExitCodes.Success;

            int code = await WorldStartup.RunAsync(Build(), () => Task.CompletedTask, new StringWriter(), () => ExitCodes.Current).WaitAsync(Deadline);

            Assert.Equal(ExitCodes.Failure, code);
            Assert.Equal(ExitCodes.Failure, ExitCodes.Current); // what Program returns and the supervisor reads
            Assert.Equal(1, RecordedFaults() - faults); // recorded once, not per layer
        }
        finally
        {
            ExitCodes.Current = before;
        }
    }

    [Fact]
    public async Task AFailedDispose_KeepsAnExitCodeThatWasAlreadySet_SoARestartRequestStaysARestart()
    {
        int before = ExitCodes.Current;
        try
        {
            ExitCodes.Current = ExitCodes.Restart;

            int code = await WorldStartup.RunAsync(Build(), () => Task.CompletedTask, new StringWriter(), () => ExitCodes.Current).WaitAsync(Deadline);

            Assert.Equal(ExitCodes.Restart, code);
            Assert.Equal(ExitCodes.Restart, ExitCodes.Current);
        }
        finally
        {
            ExitCodes.Current = before;
        }
    }

    [Fact]
    public async Task AFailedDispose_NeverReturnsZero_WhateverTheCallersExitCodeSourceSays()
    {
        int before = ExitCodes.Current;
        try
        {
            ExitCodes.Current = ExitCodes.Success;

            int code = await WorldStartup.RunAsync(Build(), () => Task.CompletedTask, new StringWriter(), () => 0).WaitAsync(Deadline);

            Assert.Equal(ExitCodes.Failure, code);
        }
        finally
        {
            ExitCodes.Current = before;
        }
    }
}
