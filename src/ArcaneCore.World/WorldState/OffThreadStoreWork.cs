using ArcaneCore.Game.Maps;

using ArcaneCore.Kernel.Diagnostics;
namespace ArcaneCore.World.WorldState;

/// <summary>
/// Single-flight runner for a world-state feature's realm-store round trips. The work runs off the world thread
/// (<see cref="Offload"/>, the thread pool by default) and its completion is applied back on the world thread through
/// <see cref="WorldRuntime.Post"/>, so a slow or contended database can delay a refresh but never stall a world tick.
/// At most one job is in flight: <see cref="TryRun"/> refuses while one runs, and the feature retries on a later tick.
/// </summary>
/// <remarks>
/// vmangos keeps event state in memory (ObjectMgr::m_SavedVariables) and its Update reads only that memory; writes go
/// through the asynchronous WorldDatabase.PExecute queue (ObjectMgr::_SaveVariable). This keeps the same property: no
/// world-thread wait on storage.
/// </remarks>
internal sealed class OffThreadStoreWork(WorldRuntime world)
{
    private bool _busy;

    /// <summary>
    /// Starts the work. Production hands it to the thread pool; tests whose fake stores complete synchronously run it
    /// inline (<c>work =&gt; work()</c>), in which case the completion is applied before <see cref="TryRun"/> returns.
    /// </summary>
    internal Func<Func<Task>, Task> Offload { get; set; } = static work => Task.Run(work);

    /// <summary>A job is running or its completion is waiting for the world thread.</summary>
    public bool Busy => _busy;

    /// <summary>The store work of the job in flight (completed when none); a feature's StopAsync waits for it.</summary>
    public Task InFlight { get; private set; } = Task.CompletedTask;

    /// <summary>Jobs started (diagnostics and tests).</summary>
    public long Started { get; private set; }

    /// <summary>
    /// Runs <paramref name="work"/> off the world thread, then <paramref name="apply"/> (or <paramref name="fail"/>) on it.
    /// Returns false, doing nothing, while another job is in flight. Call only on the world thread.
    /// </summary>
    public bool TryRun(Func<Task> work, Action apply, Action<Exception> fail)
    {
        if (_busy) return false;
        _busy = true;
        Started++;
        Task task;
        try
        {
            task = Offload(work);
        }
        catch (Exception ex)
        {
            FaultLedger.Shared.Record("world-event.offload", ex);
            AnomalyLedger.Shared.Report("world-event", "lifecycle-fault", "offthread-store-work", $"offload error={ex.GetType().Name}: {ex.Message}");
            task = Task.FromException(ex);
        }
        InFlight = task;
        if (task.IsCompleted) Complete(task, apply, fail);
        else task.ContinueWith(done => world.Post(() => Complete(done, apply, fail)), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return true;
    }

    private void Complete(Task task, Action apply, Action<Exception> fail)
    {
        _busy = false;
        if (task.Exception is { } ex) fail(ex.GetBaseException());
        else if (task.IsCanceled) fail(new TaskCanceledException(task));
        else apply();
    }
}
