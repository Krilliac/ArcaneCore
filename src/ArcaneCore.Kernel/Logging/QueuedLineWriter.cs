using System.Buffers;
using System.Diagnostics;

namespace ArcaneCore.Kernel.Logging;

/// <summary>
/// Decouples producers (the world thread, network threads) from a slow destination (disk, a terminal, a pipe nobody reads).
/// <see cref="Write(ReadOnlySpan{char})"/> copies the line into a pooled array and puts it on a bounded ring; one background thread hands the
/// lines to the inner writer in order. When the ring is full the line is dropped and counted; the next time the writer thread drains
/// the ring it writes a notice with the count (through <c>dropNotice</c>, so each sink's own format is used) and the total is
/// visible as <see cref="Dropped"/>. Producers never wait on the destination and never block each other beyond a short lock.
/// <para>
/// Priority lines (<see cref="Write(ReadOnlySpan{char}, bool)"/> with <c>priority</c>: Error and Critical events) go to a second, separate
/// ring of <c>min(capacity, 1024)</c> slots, so a flood of ordinary lines can never push them out and a write costs O(1) whatever the
/// state of the ordinary ring. Every accepted line gets a sequence number and the writer thread always takes the lower sequence of the
/// two ring heads, so delivery order is the order of acceptance. A priority line is lost only when the priority ring itself is full;
/// that is counted in <see cref="DroppedPriority"/> and reported by its own notice (<c>priorityDropNotice</c>), never by the ordinary one.
/// </para>
/// <para>
/// Ownership: the inner writer belongs to this object and is disposed with it. <see cref="Dispose"/> drains what is queued
/// (bounded by <c>shutdownDrain</c>) and flushes, so a clean shutdown loses nothing. Allocation: the rings and their
/// slots are allocated once; a line costs one <see cref="ArrayPool{T}"/> rent on the producer and a return on the writer thread.
/// </para>
/// </summary>
public sealed class QueuedLineWriter : ILineWriter
{
    private const int MaxPriorityCapacity = 1024;

    private readonly ILineWriter _inner;
    private readonly Func<long, string> _dropNotice;
    private readonly Func<long, string>? _priorityDropNotice;
    private readonly TimeSpan _shutdownDrain;
    private readonly Ring _ordinary;
    private readonly Ring _priority;
    private readonly Slot[] _batch;
    private readonly object _gate = new();
    private readonly Thread _thread;
    private long _enqueued;
    private long _processed;
    private long _dropped;
    private long _reportedDropped;
    private long _droppedPriority;
    private long _reportedDroppedPriority;
    private long _faults;
    private bool _disposing;

    /// <param name="inner">The destination; written and flushed only by the writer thread.</param>
    /// <param name="capacity">Lines the ordinary ring holds; the next ordinary line is dropped when it is full.</param>
    /// <param name="name">Thread name suffix (<c>ArcaneCore.Log.&lt;name&gt;</c>).</param>
    /// <param name="dropNotice">Builds the line reporting <c>n</c> dropped ordinary lines, in the sink's own format.</param>
    /// <param name="shutdownDrain">Longest wait for the queue to empty when disposing (default 10 s).</param>
    /// <param name="priorityDropNotice">Builds the line reporting <c>n</c> lost priority (Error and above) lines; defaults to <paramref name="dropNotice"/>.</param>
    public QueuedLineWriter(ILineWriter inner, int capacity, string name, Func<long, string> dropNotice, TimeSpan? shutdownDrain = null, Func<long, string>? priorityDropNotice = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _inner = inner;
        _dropNotice = dropNotice;
        _priorityDropNotice = priorityDropNotice;
        _shutdownDrain = shutdownDrain ?? TimeSpan.FromSeconds(10);
        _ordinary = new Ring(capacity);
        _priority = new Ring(Math.Min(capacity, MaxPriorityCapacity));
        _batch = new Slot[Math.Min(capacity + _priority.Capacity, 256)];
        _thread = new Thread(Run) { IsBackground = true, Name = "ArcaneCore.Log." + name };
        _thread.Start();
    }

    /// <summary>Lines dropped because their ring was full (ordinary and priority), since start.</summary>
    public long Dropped => Volatile.Read(ref _dropped) + Volatile.Read(ref _droppedPriority);

    /// <summary>Priority lines (Error and above) lost because the priority ring was full; included in <see cref="Dropped"/>.</summary>
    public long DroppedPriority => Volatile.Read(ref _droppedPriority);

    /// <summary>Exceptions thrown by the inner writer and swallowed (the thread keeps running).</summary>
    public long Faults => Volatile.Read(ref _faults);

    /// <summary>Lines currently waiting.</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _ordinary.Count + _priority.Count;
            }
        }
    }

    public void Write(ReadOnlySpan<char> line) => Write(line, priority: false);

    /// <summary>Queues a line; a <paramref name="priority"/> line uses its own ring, so ordinary lines cannot crowd it out.</summary>
    public void Write(ReadOnlySpan<char> line, bool priority)
    {
        if (line.IsEmpty)
        {
            return;
        }

        char[] copy = ArrayPool<char>.Shared.Rent(line.Length);
        line.CopyTo(copy);
        Ring ring = priority ? _priority : _ordinary;
        lock (_gate)
        {
            if (!ring.IsFull && !_disposing)
            {
                ring.Push(new Slot(copy, line.Length, ++_enqueued));
                Monitor.PulseAll(_gate);
                return;
            }
        }

        ArrayPool<char>.Shared.Return(copy);
        Interlocked.Increment(ref priority ? ref _droppedPriority : ref _dropped);
    }

    /// <summary>Blocks until every line queued before the call has reached the inner writer and it was flushed, or <paramref name="timeout"/> passes.</summary>
    public bool Flush(TimeSpan timeout)
    {
        long target;
        var clock = Stopwatch.StartNew();
        lock (_gate)
        {
            // Lines are numbered as they are accepted and delivered in that order, so "delivered count >= target" means every
            // line accepted before this call is out. A dropped line takes no number.
            target = _enqueued;
            while (_processed < target)
            {
                TimeSpan left = timeout - clock.Elapsed;
                if (left <= TimeSpan.Zero || !Monitor.Wait(_gate, left))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public void Flush() => Flush(_shutdownDrain);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposing)
            {
                return;
            }

            _disposing = true;
            Monitor.PulseAll(_gate);
        }

        if (!_thread.Join(_shutdownDrain))
        {
            // the destination is wedged; the thread is background and dies with the process
            return;
        }

        try
        {
            _inner.Dispose();
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _faults);
        }
    }

    private void Run()
    {
        while (true)
        {
            int taken = 0;
            lock (_gate)
            {
                while (_ordinary.Count + _priority.Count == 0 && !_disposing)
                {
                    Monitor.Wait(_gate);
                }

                if (_ordinary.Count + _priority.Count == 0)
                {
                    break;
                }

                while (taken < _batch.Length && _ordinary.Count + _priority.Count > 0)
                {
                    // the lower sequence number is the line accepted first
                    bool fromPriority = _ordinary.Count == 0 || (_priority.Count > 0 && _priority.Peek().Sequence < _ordinary.Peek().Sequence);
                    _batch[taken++] = (fromPriority ? _priority : _ordinary).Pop();
                }

                Debug.Assert(_ordinary.Count >= 0 && _priority.Count >= 0, "ring underflow");
            }

            for (int i = 0; i < taken; i++)
            {
                Slot slot = _batch[i];
                _batch[i] = default;
                Deliver(slot.Buffer.AsSpan(0, slot.Length));
                ArrayPool<char>.Shared.Return(slot.Buffer);
            }

            long dropped = Volatile.Read(ref _dropped);
            if (dropped != _reportedDropped)
            {
                Deliver(_dropNotice(dropped - _reportedDropped));
                _reportedDropped = dropped;
            }

            long lost = Volatile.Read(ref _droppedPriority);
            if (lost != _reportedDroppedPriority)
            {
                Deliver((_priorityDropNotice ?? _dropNotice)(lost - _reportedDroppedPriority));
                _reportedDroppedPriority = lost;
            }

            // One flush per batch (a batch is up to 256 lines, so a busy sink amortises it) and before the batch counts as
            // processed, so Flush(timeout) returning true means the lines are at their destination.
            try
            {
                _inner.Flush();
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _faults);
            }

            lock (_gate)
            {
                _processed += taken;
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void Deliver(ReadOnlySpan<char> line)
    {
        try
        {
            _inner.Write(line);
        }
        catch (Exception)
        {
            // logging must never take the process down; the inner writer reports its own trouble
            Interlocked.Increment(ref _faults);
        }
    }

    private readonly record struct Slot(char[] Buffer, int Length, long Sequence);

    /// <summary>A fixed-size FIFO ring; every member is called under the writer's gate.</summary>
    private sealed class Ring(int capacity)
    {
        private readonly Slot[] _slots = new Slot[capacity];
        private int _head;

        public int Capacity => _slots.Length;

        public int Count { get; private set; }

        public bool IsFull => Count == _slots.Length;

        public Slot Peek() => _slots[_head];

        public void Push(Slot slot)
        {
            int tail = _head + Count;
            if (tail >= _slots.Length)
            {
                tail -= _slots.Length;
            }

            _slots[tail] = slot;
            Count++;
        }

        public Slot Pop()
        {
            Slot slot = _slots[_head];
            _slots[_head] = default;
            _head++;
            if (_head == _slots.Length)
            {
                _head = 0;
            }

            Count--;
            return slot;
        }
    }
}
