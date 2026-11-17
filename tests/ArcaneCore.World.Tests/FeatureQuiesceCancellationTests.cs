using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.FeatureLifecycle;

/// <summary>The host cancels abandoned work before rollback without joining a feature's callbacks or unfinished task.</summary>
[Trait("Category", "Timing")]
public sealed class FeatureQuiesceCancellationTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAbandonedQuiesce_CancelsBeforeRollback_AndDoesNotWaitForItsBlockedCallback(bool innerThrowsTimeout)
    {
        using var rig = new HostRig(innerThrowsTimeout ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(100));
        using var releaseCallback = new ManualResetEventSlim();
        var callbackEntered = Signal();
        var callbackExited = Signal();
        var finishInner = Signal();
        Task inner = innerThrowsTimeout
            ? Task.FromException(new TimeoutException("The feature's own timeout reached the host."))
            : finishInner.Task;
        var feature = new CancellationFeature(token =>
        {
            token.Register(() =>
            {
                callbackEntered.TrySetResult(true);
                try { releaseCallback.Wait(); }
                finally { callbackExited.TrySetResult(true); }
            });
            return inner;
        });
        rig.Attach(feature);
        Task<FeatureResult> pending = Task.Run(() => rig.Host.DetachAsync(nameof(CancellationFeature)));
        try
        {
            FeatureResult result = await pending.WaitAsync(TestDeadline);

            Assert.False(result.Ok);
            Assert.Contains("nothing was detached", result.Detail, StringComparison.Ordinal);
            Assert.True(feature.CancellationAtRollback);
            Assert.Equal(2, feature.Attaches);
            Assert.Equal(FeatureState.Attached, rig.Host.Find(nameof(CancellationFeature))!.State);
            await callbackEntered.Task.WaitAsync(TestDeadline);
            Assert.False(releaseCallback.IsSet);
            Assert.False(callbackExited.Task.IsCompleted);
            Assert.NotNull(feature.Token.WaitHandle); // The callback still owns the cancellation source.

            releaseCallback.Set();
            await callbackExited.Task.WaitAsync(TestDeadline);
            if (!innerThrowsTimeout)
            {
                Assert.False(finishInner.Task.IsCompleted);
                Assert.NotNull(feature.Token.WaitHandle); // The unfinished inner task still owns it too.
            }

            finishInner.TrySetResult(true);
            await WaitUntilAsync(() => SourceWasDisposed(feature.Token));
        }
        finally
        {
            releaseCallback.Set();
            finishInner.TrySetResult(true);
            await pending.WaitAsync(TestDeadline);
            if (callbackEntered.Task.IsCompleted) await callbackExited.Task.WaitAsync(TestDeadline);
        }
    }

    [Fact]
    public async Task AThrowingCancellationCallback_IsRecorded_AndDoesNotPreventOtherCallbacksOrRollback()
    {
        using var rig = new HostRig(TimeSpan.FromSeconds(10));
        var callbackRan = Signal();
        var callbackFault = new InvalidOperationException("quiesce-callback-" + Guid.NewGuid().ToString("N"));
        var feature = new CancellationFeature(token =>
        {
            token.Register(() => callbackRan.TrySetResult(true));
            token.Register(() => throw callbackFault); // Registered last, so it runs before the first callback.
            return Task.FromException(new TimeoutException("An inner timeout must request cancellation too."));
        });
        rig.Attach(feature);

        FeatureResult result = await Task.Run(() => rig.Host.DetachAsync(nameof(CancellationFeature))).WaitAsync(TestDeadline);

        Assert.False(result.Ok);
        Assert.True(feature.CancellationAtRollback);
        Assert.Equal(FeatureState.Attached, rig.Host.Find(nameof(CancellationFeature))!.State);
        await callbackRan.Task.WaitAsync(TestDeadline);
        await WaitUntilAsync(() => FaultLedger.WasRecorded(callbackFault));
        await WaitUntilAsync(() => SourceWasDisposed(feature.Token));
        Assert.Contains("nothing was detached", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInnerFaultAfterTimeout_IsObserved_WithoutChangingTheReturnedOutcomeOrBlockingTheNextOperation()
    {
        using var rig = new HostRig(TimeSpan.FromMilliseconds(100));
        var finishInner = Signal();
        var lateFault = new InvalidOperationException("quiesce-late-" + Guid.NewGuid().ToString("N"));
        var feature = new CancellationFeature(_ => finishInner.Task);
        rig.Attach(feature);
        Task<FeatureResult> pending = Task.Run(() => rig.Host.DetachAsync(nameof(CancellationFeature)));
        try
        {
            FeatureResult result = await pending.WaitAsync(TestDeadline);
            string detail = result.Detail;
            Assert.False(result.Ok);
            Assert.True(feature.CancellationAtRollback);
            Assert.False(FaultLedger.WasRecorded(lateFault));
            Assert.NotNull(feature.Token.WaitHandle);

            finishInner.TrySetException(lateFault);
            await WaitUntilAsync(() => FaultLedger.WasRecorded(lateFault));
            await WaitUntilAsync(() => SourceWasDisposed(feature.Token));

            Assert.False(result.Ok);
            Assert.Equal(detail, result.Detail);
            Assert.Equal(FeatureState.Attached, rig.Host.Find(nameof(CancellationFeature))!.State);
            feature.RunQuiesce = _ => Task.CompletedTask;
            FeatureResult detached = await rig.Host.DetachAsync(nameof(CancellationFeature)).WaitAsync(TestDeadline);
            Assert.True(detached.Ok, detached.Detail);
            Assert.Equal(FeatureState.Detached, rig.Host.Find(nameof(CancellationFeature))!.State);
        }
        finally
        {
            finishInner.TrySetResult(true);
            await pending.WaitAsync(TestDeadline);
        }
    }

    [Fact]
    public async Task ACompletedQuiesce_DoesNotCancelItsToken()
    {
        using var rig = new HostRig(TimeSpan.FromSeconds(10));
        var feature = new CancellationFeature(_ => Task.CompletedTask);
        rig.Attach(feature);

        FeatureResult result = await rig.Host.DetachAsync(nameof(CancellationFeature)).WaitAsync(TestDeadline);

        Assert.True(result.Ok, result.Detail);
        Assert.False(feature.Token.IsCancellationRequested);
        Assert.Equal(1, feature.Attaches);
        Assert.Equal(FeatureState.Detached, rig.Host.Find(nameof(CancellationFeature))!.State);
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool SourceWasDisposed(CancellationToken token)
    {
        try { _ = token.WaitHandle; return false; }
        catch (ObjectDisposedException) { return true; }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TestDeadline);
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class HostRig : IDisposable
    {
        private readonly WorldRuntime _world = new(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);
        private readonly FeatureLifecycleTests.GatedWorld _poster = new();

        public HostRig(TimeSpan quiesceTimeout)
            => Host = new FeatureHost(_poster, commitTimeout: TestDeadline, quiesceTimeout: quiesceTimeout);

        public FeatureHost Host { get; }

        public void Attach(CancellationFeature feature) => Host.AttachAll(_world, [feature]);

        public void Dispose()
        {
            _poster.Dispose();
            _world.Dispose();
        }
    }

    private sealed class CancellationFeature(Func<CancellationToken, Task> runQuiesce) : IDetachableWorldFeature
    {
        public DetachKind DetachKind => DetachKind.Stateless;
        public Func<CancellationToken, Task> RunQuiesce { get; set; } = runQuiesce;
        public CancellationToken Token { get; private set; }
        public bool CancellationAtRollback { get; private set; }
        public int Attaches { get; private set; }

        public Task QuiesceAsync(CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            return RunQuiesce(cancellationToken);
        }

        public void Detach(FeatureHandover handover) { }

        public void AttachFrom(WorldRuntime world, FeatureHandover? handover, FeatureSubscriptions subscriptions)
        {
            Attaches++;
            if (Attaches > 1) CancellationAtRollback = Token.IsCancellationRequested;
        }

    }
}
