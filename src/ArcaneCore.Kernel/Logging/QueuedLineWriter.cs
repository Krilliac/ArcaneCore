using System.Buffers;
using System.Diagnostics;

namespace ArcaneCore.Kernel.Logging;

/// <summary>
/// Decouples producers (the world thread, network threads) from a slow destination (disk, a terminal, a pipe nobody reads).
/// <see cref="Write"/> copies the line into a pooled array and puts it on a bounded ring; one background thread hands the lines
/// to the inner writer in order. When the ring is full the line is dropped and counted; the next time the writer thread drains
/// the ring it writes a notice with the count (through <c>dropNotice</c>, so each sink's own format is used) and the total is
/// visible as <see cref="Dropped"/>. Producers never wait on the destination and never block each other beyond a short lock.
/// <para>
/// Ownership: the inner writer belongs to this object and is disposed with it. <see cref="Dispose"/> drains what is queued
/// (bounded by <paramref name="shutdownDrain"/>) and flushes, so a clean shutdown loses nothing. Allocation: the ring and its
/// slots are allocated once; a line costs one <see cref="ArrayPool{T}"/> rent on the producer and a return on the writer thread.
/// </para>
/// </summary>
public sealed class QueuedLineWriter : ILineWriter
{
    private readonly ILineWriter _inner;
    private readonly Func<long, string> _dropNotice;
    private readonly TimeSpan _shutdownDrain;
    private readonly Slot[] _ring;
    private readonly Slot[] _batch;
    private readonly object _gate = new();
    private readonly Thread _thread;
    private int _head;
    private int _count;
    private long _enqueued;
    private long _processed;
    private long _dropped;
    private long _reportedDropped;
    private long _faults;
    private bool _disposing;

    /// <param name="inner">The destination; written and flushed only by the writer thread.</param>
    /// <param name="capacity">Lines the ring holds; the next line is dropped when it is full.</param>
    /// <param name="name">Thread name suffix (<c>ArcaneCore.Log.&lt;name&gt;</c>).</param>
    /// <param name="dropNotice">Builds the line reporting <c>n</c> dropped lines, in the sink's own format.</param>
    /// <param name="shutdownDrain">Longest wait for the queue to empty when disposing (default 10 s).</param>
    public QueuedLineWriter(ILineWriter inner, int capacity, string name, Func<long, string> dropNotice, TimeSpan? shutdownDrain = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _inner = inner;
        _dropNotice = dropNotice;
        _shutdownDrain = shutdownDrain ?? TimeSpan.FromSeconds(10);
        _ring = new Slot[capacity];
        _batch = new Slot[Math.Min(capacity, 256)];
        _thread = new Thread(Run) { IsBackground = true, Name = "ArcaneCore.Log." + name };
        _thread.Start();
    }

    /// <summary>Lines dropped because the ring was full, since start.</summary>
    public long Dropped => Volatile.Read(ref _dropped);

    /// <summary>Exceptions thrown by the inner writer and swallowed (the thread keeps running).</summary>
    public long Faults => Volatile.Read(ref _faults);

    /// <summary>Lines currently waiting.</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public void Write(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty)
        {
            return;
        }

        char[] copy = ArrayPool<char>.Shared.Rent(line.Length);
        line.CopyTo(copy);
        lock (_gate)
        {
            if (_count < _ring.Length && !_disposing)
            {
                int tail = _head + _count;
                if (tail >= _ring.Length)
                {
                    tail -= _ring.Length;
                }

                _ring[tail] = new Slot(copy, line.Length);
                _count++;
                _enqueued++;
                Monitor.PulseAll(_gate);
                return;
            }
        }

        ArrayPool<char>.Shared.Return(copy);
        Interlocked.Increment(ref _dropped);
    }

    /// <summary>Blocks until every line queued before the call has reached the inner writer and it was flushed, or <paramref name="timeout"/> passes.</summary>
    public bool Flush(TimeSpan timeout)
    {
        long target;
        var clock = Stopwatch.StartNew();
        lock (_gate)
        {
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
            int taken;
            lock (_gate)
            {
                while (_count == 0 && !_disposing)
                {
                    Monitor.Wait(_gate);
                }

                if (_count == 0)
                {
                    break;
                }

                taken = Math.Min(_count, _batch.Length);
                for (int i = 0; i < taken; i++)
                {
                    _batch[i] = _ring[_head];
                    _ring[_head] = default;
                    _head++;
                    if (_head == _ring.Length)
                    {
                        _head = 0;
                    }
                }

                _count -= taken;
                Debug.Assert(_count >= 0, "ring underflow");
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

    private readonly record struct Slot(char[] Buffer, int Length);
}
