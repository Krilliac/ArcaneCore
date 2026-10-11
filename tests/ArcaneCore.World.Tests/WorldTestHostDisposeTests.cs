using Xunit;

namespace ArcaneCore.World.Tests;

/// <summary>
/// The harness's own teardown. Node CI (three runs sharing one machine) once failed
/// ManagedPlayerbotLifecycleTests.DisabledAndUnknownHumanName_DoNothing in its dispose with InvalidOperationException "Not listening.
/// You must call the Start() method before calling this method.": the dispose stopped the listener before its accept loop had ended,
/// and a loop the thread pool had not yet run past its cancellation check then accepted on a stopped listener.
/// </summary>
public sealed class WorldTestHostDisposeTests
{
    [Fact]
    public async Task ADispose_WhileTheAcceptLoopIsBetweenItsCheckAndItsAccept_IsClean()
    {
        var atGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WorldTestHost.AcceptGate.Value = () =>
        {
            atGate.TrySetResult();
            return release.Task;
        };
        WorldTestHost host;
        try
        {
            host = WorldTestHost.Start();
        }
        finally
        {
            WorldTestHost.AcceptGate.Value = null;
        }

        await atGate.Task.WaitAsync(TimeSpan.FromSeconds(30)); // the loop passed its cancellation check and waits to accept
        ValueTask disposing = host.DisposeAsync();              // runs up to its first await: the cancellation (and, before, the stop)
        release.TrySetResult();                                 // the loop goes on to its accept
        await disposing.AsTask().WaitAsync(TimeSpan.FromSeconds(60));
    }
}
