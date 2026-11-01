namespace ArcaneCore.Game.Maps;

/// <summary>
/// Shared facts of the data-parallel stages inside one map update (docs/research/map-local-stages-design-20261024.md, section 4.3,
/// "option C", and wave 6a). The stage itself is <see cref="MapLocalStage{TItem, TResult}"/>.
/// </summary>
public static class MapLocalStages
{
    [ThreadStatic] private static bool t_isHelper;

    /// <summary>
    /// True on a helper thread while it computes items of a stage. Code that runs there must be a pure function of its item and of data
    /// that nothing writes during the compute step: a helper has no current map (<see cref="TickOwner.CurrentMap"/> is null), never takes
    /// the serial gate, and must not post world work or change any map.
    /// </summary>
    public static bool IsHelper => t_isHelper;

    internal static void EnterHelper() => t_isHelper = true;

    internal static void LeaveHelper() => t_isHelper = false;
}

/// <summary>
/// One data-parallel stage inside a map update: a compute step that maps every item to a result slot, then an apply step that consumes
/// the results on the calling (map) thread in item order. The outcome never depends on the helper count, because
/// <list type="bullet">
/// <item>compute may only read (it must be a pure function of its item and of frozen data) and writes nothing but its item's slot,</item>
/// <item>apply runs after every compute is done, on the caller, in index order, with the caller's own ownership of the map,</item>
/// <item>the caller drains the same item cursor as the helpers (caller-runs), so a run completes with zero helpers, and with every
///   pool thread busy.</item>
/// </list>
/// With <c>helpers</c> 0 (<see cref="ParallelMapOptions.MapHelpers"/>, default 0) nothing is queued: the compute loop runs inline.
/// <para>
/// A compute that throws does not stop the others; after the join the fault of the lowest item index is thrown as an
/// <see cref="AggregateException"/> and apply does not run (at every helper count alike).
/// </para>
/// <para>
/// Ownership and affinity: one instance belongs to one call site and one map thread at a time; <see cref="Run"/> is not reentrant and not
/// thread-safe. The instance keeps its result buffer between runs (so a steady-state run allocates nothing: no closures, no tasks; the
/// delegates are the caller's and should be cached) and clears the buffer after each run when <typeparamref name="TResult"/> holds
/// references. Helpers are thread-pool work items; <see cref="Run"/> returns only after every queued helper job has finished, so no
/// helper touches the instance after <see cref="Run"/> returns. A pool that is starved delays the join, never deadlocks it, as long as
/// compute does not wait on the caller. <see cref="Run"/> with helpers must not be called while holding the world's serial gate (a
/// helper that needed it would deadlock with its caller), and it throws if it is.
/// </para>
/// </summary>
public sealed class MapLocalStage<TItem, TResult> : IThreadPoolWorkItem
{
    private readonly object _faultLock = new();
    private readonly ManualResetEventSlim _joined = new(false);
    private TResult[] _results = [];
    private IReadOnlyList<TItem>? _items;
    private Func<TItem, TResult>? _compute;
    private int _count;
    private int _next;
    private int _pending;
    private int _faultIndex;
    private Exception? _fault;
    private long _helperBytes;
    private long _itemsOnHelpers;

    /// <summary>Items computed on a helper thread, summed over all runs (diagnostics, tests).</summary>
    public long ItemsOnHelpers => Interlocked.Read(ref _itemsOnHelpers);

    /// <summary>
    /// Bytes the helpers allocated (<see cref="GC.GetAllocatedBytesForCurrentThread"/> around their share) in the last run; the owner adds
    /// it to the map's allocated bytes so the maps phase total stays complete.
    /// </summary>
    public long LastHelperBytes => Interlocked.Read(ref _helperBytes);

    /// <summary>Runs completed (diagnostics).</summary>
    public long Runs { get; private set; }

    /// <summary>
    /// Compute every item of <paramref name="items"/>, then apply the results in index order.
    /// </summary>
    /// <param name="items">The items; not changed during the run (read by helpers).</param>
    /// <param name="compute">Pure per-item function (see <see cref="MapLocalStage{TItem, TResult}"/>); may run on helper threads.</param>
    /// <param name="apply">Consumes an item and its result on the calling thread, in index order, after every compute.</param>
    /// <param name="helpers">Helper jobs to queue (0: none); at most <c>items.Count - 1</c> are queued.</param>
    /// <param name="world">The world, to check the gate precondition when <paramref name="helpers"/> is above 0; may be null.</param>
    public void Run(IReadOnlyList<TItem> items, Func<TItem, TResult> compute, Action<TItem, TResult> apply, int helpers, WorldRuntime? world = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(compute);
        ArgumentNullException.ThrowIfNull(apply);
        int count = items.Count;
        if (count == 0)
        {
            return;
        }

        int jobs = Math.Min(Math.Max(helpers, 0), count - 1);
        if (jobs > 0 && world is { IsParallelPhase: true } && world.HoldsSerialGate)
        {
            throw new InvalidOperationException("a map-local stage with helpers must run outside the serial gate (a helper cannot take it while its caller holds it)");
        }

        if (_results.Length < count)
        {
            _results = new TResult[count];
        }

        _items = items;
        _compute = compute;
        _count = count;
        _next = -1;
        _faultIndex = int.MaxValue;
        _fault = null;
        Interlocked.Exchange(ref _helperBytes, 0);
        try
        {
            if (jobs > 0)
            {
                _joined.Reset();
                _pending = jobs;
                for (int i = 0; i < jobs; i++)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
                }
            }

            Drain();
            if (jobs > 0)
            {
                _joined.Wait();
            }

            if (_fault is { } fault)
            {
                throw new AggregateException("a map-local stage item failed (lowest failing index " + _faultIndex + ")", fault);
            }

            TResult[] results = _results;
            for (int i = 0; i < count; i++)
            {
                apply(items[i], results[i]);
            }

            Runs++;
        }
        finally
        {
            _items = null;
            _compute = null;
            _fault = null;
            if (System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<TResult>())
            {
                Array.Clear(_results, 0, count);
            }
        }
    }

    void IThreadPoolWorkItem.Execute()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        MapLocalStages.EnterHelper();
        try
        {
            Drain();
        }
        finally
        {
            MapLocalStages.LeaveHelper();
            Interlocked.Add(ref _helperBytes, GC.GetAllocatedBytesForCurrentThread() - before);
            if (Interlocked.Decrement(ref _pending) == 0)
            {
                _joined.Set();
            }
        }
    }

    private void Drain()
    {
        IReadOnlyList<TItem>? items = _items;
        Func<TItem, TResult>? compute = _compute;
        if (items is null || compute is null)
        {
            return;
        }

        bool helper = MapLocalStages.IsHelper;
        int index;
        while ((index = Interlocked.Increment(ref _next)) < _count)
        {
            try
            {
                _results[index] = compute(items[index]);
                if (helper)
                {
                    Interlocked.Increment(ref _itemsOnHelpers);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                lock (_faultLock)
                {
                    if (index < _faultIndex)
                    {
                        _faultIndex = index;
                        _fault = ex;
                    }
                }
            }
        }
    }
}
