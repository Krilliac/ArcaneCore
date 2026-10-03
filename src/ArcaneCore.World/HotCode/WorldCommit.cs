namespace ArcaneCore.World.HotCode;

/// <summary>
/// Run a state change on the world thread (<see cref="IHotCodeWorld.Post"/>, start of a tick) and
/// wait for it from a background caller. Shared by the registry refresh and the module host.
/// </summary>
internal static class WorldCommit
{
    /// <summary>
    /// Post <paramref name="apply"/> and wait up to <paramref name="timeout"/>. Null when it applied,
    /// else why it did not. A command the world thread only reaches after the caller gave up is
    /// skipped, so it can never apply behind the caller's back. An exception from
    /// <paramref name="apply"/> is reported, never thrown into the world thread.
    /// </summary>
    public static async Task<string?> RunAsync(IHotCodeWorld world, TimeSpan timeout, Func<string?> apply)
    {
        const int Pending = 0;
        const int Running = 1;
        const int Abandoned = 2;
        int phase = Pending;
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        world.Post(() =>
        {
            if (Interlocked.CompareExchange(ref phase, Running, Pending) != Pending)
            {
                return;
            }

            try
            {
                done.SetResult(apply());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                done.SetResult($"commit threw: {ex.Message}");
            }
        });

        // The timer is cancelled as soon as the commit finishes: a pending Delay would keep this method's
        // state (and so the closure over what is being swapped, e.g. a module's load context) reachable until it fires.
        using var cancelTimer = new CancellationTokenSource();
        Task finished = await Task.WhenAny(done.Task, Task.Delay(timeout, cancelTimer.Token)).ConfigureAwait(false);
        if (finished == done.Task)
        {
            await cancelTimer.CancelAsync().ConfigureAwait(false);
        }

        if (finished != done.Task && Interlocked.CompareExchange(ref phase, Abandoned, Pending) == Pending)
        {
            return $"the world thread did not run the commit within {timeout.TotalSeconds:0.#}s; nothing was applied";
        }

        return await done.Task.ConfigureAwait(false);
    }
}
