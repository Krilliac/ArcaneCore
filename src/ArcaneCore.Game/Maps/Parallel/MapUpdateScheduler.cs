using System.Diagnostics;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// The map workers of the parallel maps phase (docs/research/multithreading-plan-20261017.md, W3): N-1 dedicated threads plus the
/// calling (world) thread take the items of one batch from a shared cursor until none is left, and <see cref="Run"/> returns only
/// when every item is done (the barrier). The world thread hands the items over in longest-first order, so the slowest map starts
/// first and the short ones fill the gaps (LPT). A worker that is not needed for a batch (fewer items than threads) stays asleep.
/// </summary>
internal sealed class MapUpdateScheduler : IDisposable
{
    private readonly Thread[] _workers;
    private readonly SemaphoreSlim _go = new(0);
    private readonly CountdownEvent _done = new(1);
    private volatile bool _stopping;
    private Action<int>? _body;
    private int _count;
    private int _next;

    public MapUpdateScheduler(int threads, string name = "map-worker")
    {
        if (threads < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(threads), "a scheduler needs at least two threads (the world thread is one)");
        }

        _workers = new Thread[threads - 1];
        for (int i = 0; i < _workers.Length; i++)
        {
            _workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"{name}-{i + 1}" };
            _workers[i].Start();
        }
    }

    /// <summary>Threads that take part in a batch, the caller included.</summary>
    public int Threads => _workers.Length + 1;

    /// <summary>Batches run (diagnostics).</summary>
    public long Batches { get; private set; }

    /// <summary>Microseconds the caller waited at the barrier after it ran out of items, summed over all batches (diagnostics).</summary>
    public long BarrierWaitMicros { get; private set; }

    /// <summary>
    /// Run <paramref name="body"/> for every index in [0, <paramref name="count"/>), on this thread and the workers, and return when
    /// all are done. <paramref name="body"/> must not throw (an exception that escapes is caught here and rethrown on this thread after
    /// the barrier, so no worker dies). Not reentrant; one caller.
    /// </summary>
    public void Run(int count, Action<int> body)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (count <= 0)
        {
            return;
        }

        _body = body;
        _count = count;
        _next = -1;
        _fault = null;
        int helpers = Math.Min(_workers.Length, count - 1);
        _done.Reset(helpers + 1);
        if (helpers > 0)
        {
            _go.Release(helpers);
        }

        Drain();
        _done.Signal();
        long waitStart = Stopwatch.GetTimestamp();
        _done.Wait();
        BarrierWaitMicros += (Stopwatch.GetTimestamp() - waitStart) * 1_000_000 / Stopwatch.Frequency;
        Batches++;
        _body = null;
        if (_fault is { } fault)
        {
            _fault = null;
            throw new AggregateException("a map worker item failed", fault);
        }
    }

    private Exception? _fault;

    private void Drain()
    {
        Action<int>? body = _body;
        if (body is null)
        {
            return;
        }

        int index;
        while ((index = Interlocked.Increment(ref _next)) < _count)
        {
            try
            {
                body(index);
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _fault, ex, null);
            }
        }
    }

    private void WorkerLoop()
    {
        while (true)
        {
            _go.Wait();
            if (_stopping)
            {
                return;
            }

            try
            {
                Drain();
            }
            finally
            {
                _done.Signal();
            }
        }
    }

    public void Dispose()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _go.Release(_workers.Length);
        foreach (Thread worker in _workers)
        {
            worker.Join(TimeSpan.FromSeconds(5));
        }

        _go.Dispose();
        _done.Dispose();
    }
}
