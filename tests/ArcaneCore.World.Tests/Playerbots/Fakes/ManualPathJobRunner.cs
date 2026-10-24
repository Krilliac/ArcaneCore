using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;

namespace ArcaneCore.World.Tests.Playerbots.Fakes;

/// <summary>
/// Holds the <see cref="NavJob"/>s a bot think submits and runs them only when a test says so, on the test's thread, through
/// <see cref="NavJobSystem.Run"/> (the pool's own worker code: abandoned jobs are skipped, the missed-tile list is filled): no real
/// thread decides when a job finishes.
/// </summary>
internal sealed class ManualPathJobRunner : IPlayerbotPathJobRunner, IDisposable
{
    private readonly NavJobSystem _system = new(1, 8);
    private readonly Queue<NavJob> _queue = new();

    public int Threads => 1;

    /// <summary>Every job ever submitted.</summary>
    public List<NavJob> Submitted { get; } = [];

    /// <summary>Jobs held and not run.</summary>
    public int Held => _queue.Count;

    /// <summary>Refuse every submission (a full pool).</summary>
    public bool Full { get; set; }

    public bool TrySubmit(NavJob job)
    {
        if (Full) return false;
        Submitted.Add(job);
        _queue.Enqueue(job);
        return true;
    }

    /// <summary>Run the oldest held job to completion (a worker taking it); false when none is held.</summary>
    public bool CompleteNext()
    {
        if (!_queue.TryDequeue(out NavJob? job)) return false;
        _system.Run(job);
        return true;
    }

    public void CompleteAll()
    {
        while (CompleteNext())
        {
        }
    }

    /// <summary>End the oldest held job as canceled (the owner changed a tile under it).</summary>
    public bool CancelNext()
    {
        if (!_queue.TryDequeue(out NavJob? job)) return false;
        job.Finish(NavJobStatus.Canceled, null, default, 0, 0);
        return true;
    }

    /// <summary>End the oldest held job as faulted.</summary>
    public bool FaultNext()
    {
        if (!_queue.TryDequeue(out NavJob? job)) return false;
        job.Finish(NavJobStatus.Faulted, null, default, 0, 0);
        return true;
    }

    public void Dispose() => _system.Dispose();
}
