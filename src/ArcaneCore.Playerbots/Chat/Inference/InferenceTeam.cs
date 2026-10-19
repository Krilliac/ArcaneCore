using System.Runtime.ExceptionServices;

namespace ArcaneCore.World.Playerbots.Chat.Inference;

/// <summary>One piece of parallel work: items [<c>start</c>, <c>end</c>) on worker <c>worker</c> (0 = the caller).</summary>
internal interface IInferenceJob
{
    void Execute(int worker, int start, int end);
}

/// <summary>
/// A fixed team of dedicated BelowNormal-priority threads for the in-process chat engine's matrix work. <see cref="Run"/> splits a
/// job's items into <see cref="Size"/> contiguous ranges: the calling thread (itself the engine's own generation thread) runs the
/// first, the helpers the rest, and the call returns when all are done. The pool's threads are never used, and the threads sit below
/// the world and network threads, so a reply being written can never take the CPU from the live tick. Nothing allocates per call.
/// </summary>
internal sealed class InferenceTeam : IDisposable
{
    private readonly Thread[] _helpers;
    private readonly ManualResetEventSlim[] _go;
    private readonly CountdownEvent _done;
    private IInferenceJob? _job;
    private int _items;
    private Exception? _error;
    private volatile bool _stopping;

    public InferenceTeam(int size, string name)
    {
        if (size is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(size), "a team has 1..64 threads");
        Size = size;
        _helpers = new Thread[size - 1];
        _go = new ManualResetEventSlim[size - 1];
        _done = new CountdownEvent(1);
        for (int i = 0; i < _helpers.Length; i++)
        {
            int index = i + 1;
            _go[i] = new ManualResetEventSlim(false, spinCount: 64);
            _helpers[i] = new Thread(() => HelperLoop(index))
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = $"{name} worker {index}",
            };
            _helpers[i].Start();
        }
    }

    /// <summary>Threads that share a job, the caller included.</summary>
    public int Size { get; }

    /// <summary>Run <paramref name="job"/> over <paramref name="items"/> items on the whole team (the caller's thread takes the first range).</summary>
    public void Run(IInferenceJob job, int items)
    {
        if (items <= 0) return;
        if (Size == 1 || items < Size * 2)
        {
            job.Execute(0, 0, items);
            return;
        }

        ObjectDisposedException.ThrowIf(_stopping, this);
        _job = job;
        _items = items;
        _error = null;
        _done.Reset(_helpers.Length);
        foreach (ManualResetEventSlim go in _go) go.Set();
        Exception? own = null;
        try
        {
            (int start, int end) = Range(0, items);
            job.Execute(0, start, end);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            own = error;
        }

        _done.Wait();
        _job = null;
        Exception? failure = own ?? _error;
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private (int Start, int End) Range(int worker, int items)
        => ((int)((long)items * worker / Size), (int)((long)items * (worker + 1) / Size));

    private void HelperLoop(int index)
    {
        ManualResetEventSlim go = _go[index - 1];
        while (true)
        {
            go.Wait();
            go.Reset();
            if (_stopping) return;
            try
            {
                (int start, int end) = Range(index, _items);
                if (start < end) _job?.Execute(index, start, end);
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref _error, error, null);
            }
            finally
            {
                _done.Signal();
            }
        }
    }

    /// <summary>Stop the helpers and wait for them (at most <paramref name="timeout"/> each). False when one did not stop in time.</summary>
    public bool Stop(TimeSpan timeout)
    {
        _stopping = true;
        foreach (ManualResetEventSlim go in _go) go.Set();
        bool all = true;
        foreach (Thread helper in _helpers) all &= helper.Join(timeout);
        return all;
    }

    public void Dispose()
    {
        if (!Stop(TimeSpan.FromSeconds(5))) return; // a helper still running keeps its handles
        foreach (ManualResetEventSlim go in _go) go.Dispose();
        _done.Dispose();
    }
}
