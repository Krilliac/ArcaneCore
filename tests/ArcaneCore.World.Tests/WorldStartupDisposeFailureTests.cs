using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Ops;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// A host whose disposal (which stops what is still running) throws. 2026-11-04, split cluster: the failed stop escaped <c>Host.DisposeAsync</c> through <see cref="WorldStartup.RunAsync"/>
/// into the crash handler's fail-fast, adding a full dump and a restart delay. Now it is logged, recorded in the fault ledger once, and ends the run with a failure exit code. These run the real
/// generic host: the failing service is a container-owned singleton, so the exception comes out of the container's own disposal.
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
