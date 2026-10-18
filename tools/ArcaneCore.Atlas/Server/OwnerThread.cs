using System.Collections.Concurrent;

namespace ArcaneCore.Atlas.Server;

/// <summary>
/// One dedicated thread that runs queued work in order. The ArcaneCore.Game server types (world runtime, maps, terrain
/// manager, vmap manager, navmesh pathfinder) are world-thread affine and carry unsynchronised caches, so every call into
/// them goes through <see cref="RunAsync{T}"/>; callers on any thread get a <see cref="Task{TResult}"/>.
/// <para>
/// State ownership: the queue and the thread belong to this object; a job's closure belongs to its caller until it runs.
/// Thread affinity: <see cref="RunAsync{T}(Func{T}, CancellationToken)"/> may be called from any thread. A call made by a job
/// that already runs on the owner thread executes inline (queueing it and waiting would deadlock). The thread is started by
/// the first job and is a background thread, so it never keeps the process alive.
/// </para>
/// </summary>
public sealed class OwnerThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly string _name;
    private readonly Lock _startGate = new();
    private Thread? _thread;
    private int _threadId;
    private long _completed;
    private int _disposed;

    /// <summary>Creates an owner thread that starts on the first job.</summary>
    /// <param name="name">The OS-visible thread name.</param>
    public OwnerThread(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
    }

    /// <summary>True when the calling thread is the owner thread.</summary>
    public bool IsCurrent => Environment.CurrentManagedThreadId == Volatile.Read(ref _threadId);

    /// <summary>Jobs waiting to run.</summary>
    public int Pending => _queue.Count;

    /// <summary>Jobs that have finished, whether they succeeded, failed or were cancelled before they started.</summary>
    public long Completed => Interlocked.Read(ref _completed);

    /// <summary>Whether the thread has been started.</summary>
    public bool Started => Volatile.Read(ref _threadId) != 0;

    /// <summary>
    /// Runs <paramref name="work"/> on the owner thread. The task completes with its result, faults with the exception it
    /// threw, or is cancelled as soon as <paramref name="cancellationToken"/> fires while the job is still waiting (a running
    /// job is not interrupted, and a cancelled job is skipped when its turn comes).
    /// </summary>
    /// <exception cref="ObjectDisposedException">The owner thread has been disposed.</exception>
    public Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (IsCurrent)
        {
            try
            {
                return Task.FromResult(work());
            }
            catch (Exception e)
            {
                return Task.FromException<T>(e);
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        EnsureStarted();
        CancellationTokenRegistration registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(static (state, token) => ((TaskCompletionSource<T>)state!).TrySetCanceled(token), completion)
            : default;
        try
        {
            _queue.Add(() =>
            {
                registration.Dispose();
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    completion.TrySetResult(work());
                }
                catch (Exception e)
                {
                    completion.TrySetException(e);
                }
            });
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // Dispose ran between the disposed check and the add.
            registration.Dispose();
            throw new ObjectDisposedException(nameof(OwnerThread));
        }

        return completion.Task;
    }

    /// <summary>As <see cref="RunAsync{T}(Func{T}, CancellationToken)"/> for work with no result.</summary>
    public Task RunAsync(Action work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync(
            () =>
            {
                work();
                return true;
            },
            cancellationToken);
    }

    /// <summary>Stops accepting work, lets the queued jobs finish, and joins the thread (at most ten seconds).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Thread? thread;
        lock (_startGate)
        {
            // EnsureStarted re-checks _disposed under this lock, so after this point no thread can appear.
            thread = _thread;
        }

        _queue.CompleteAdding();

        if (IsCurrent)
        {
            return;
        }

        if (thread is null || thread.Join(TimeSpan.FromSeconds(10)))
        {
            _queue.Dispose();
        }
    }

    private void EnsureStarted()
    {
        if (Volatile.Read(ref _threadId) != 0)
        {
            return;
        }

        lock (_startGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_thread is not null)
            {
                return;
            }

            _thread = new Thread(Loop) { IsBackground = true, Name = _name };
            _thread.Start();
        }
    }

    private void Loop()
    {
        Volatile.Write(ref _threadId, Environment.CurrentManagedThreadId);
        foreach (Action job in _queue.GetConsumingEnumerable())
        {
            job();
            Interlocked.Increment(ref _completed);
        }
    }
}
