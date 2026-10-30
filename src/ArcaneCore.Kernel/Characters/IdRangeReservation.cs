using System.Collections.Immutable;

namespace ArcaneCore.Kernel.Characters;

/// <summary>
/// Canonical scope names and bounds for cluster-wide durable character-DB ID range reservations.
/// </summary>
public static class IdRangeScopes
{
    public const string Item = "item";
    public const string Group = "group";
    public const string Guild = "guild";
    public const string Instance = "instance";
    public const string Mail = "mail";
    public const string Auction = "auction";
    public const string ItemText = "item_text";

    /// <summary>
    /// Minimum first ID for the `"instance"` scope so dynamic dungeon/raid IDs never collide
    /// with static continent instance IDs (`0`..`100`).
    /// </summary>
    public const uint FirstDynamicInstanceId = 101;

    /// <summary>
    /// All canonical scopes seeded in `character_id_ranges`.
    /// </summary>
    public static readonly ImmutableArray<string> All =
    [
        Item,
        Group,
        Guild,
        Instance,
        Mail,
        Auction,
        ItemText,
    ];

    /// <summary>
    /// Returns the minimum valid starting ID for the given <paramref name="scope"/>.
    /// </summary>
    public static uint GetMinFirstId(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return string.Equals(scope, Instance, StringComparison.Ordinal)
            ? FirstDynamicInstanceId
            : 1u;
    }

    /// <summary>
    /// Returns the maximum valid ID for the given <paramref name="scope"/>.
    /// Guild and Instance IDs are stored in signed 32-bit integer columns (`int.MaxValue`),
    /// whereas Item, Group, Mail, Auction, and ItemText IDs span the full 32-bit unsigned range (`uint.MaxValue`).
    /// </summary>
    public static uint GetMaxId(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return scope switch
        {
            Guild or Instance => (uint)int.MaxValue,
            _ => uint.MaxValue,
        };
    }
}

/// <summary>
/// Represents a contiguous, non-overlapping reserved block of IDs <c>[Start, EndInclusive]</c> for a named <see cref="Scope"/>.
/// </summary>
public readonly record struct IdRangeBlock
{
    public string Scope { get; }
    public uint Start { get; }
    public uint EndInclusive { get; }

    public IdRangeBlock(string scope, uint start, uint endInclusive)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("Scope must be non-empty.", nameof(scope));
        if (start == 0)
            throw new ArgumentOutOfRangeException(nameof(start), start, "Reserved ID range cannot start at 0.");
        if (endInclusive < start)
            throw new ArgumentOutOfRangeException(nameof(endInclusive), endInclusive, "EndInclusive must be greater than or equal to Start.");

        Scope = scope;
        Start = start;
        EndInclusive = endInclusive;
    }

    /// <summary>
    /// Number of IDs contained in this block (`EndInclusive - Start + 1`).
    /// </summary>
    public uint Count => EndInclusive - Start + 1u;

    public bool Contains(uint id) => id >= Start && id <= EndInclusive;
}

/// <summary>
/// Thrown when an ID allocator or reservation store cannot allocate a requested ID or block
/// because the scope's 32-bit ID space is exhausted or no reserved block is available.
/// Inherits from <see cref="InvalidOperationException"/> so existing fail-closed overflow handlers catch it.
/// </summary>
public sealed class IdRangeExhaustedException : InvalidOperationException
{
    public string Scope { get; }

    public IdRangeExhaustedException(string scope, string message)
        : base(message)
    {
        Scope = scope;
    }

    public IdRangeExhaustedException(string scope, string message, Exception innerException)
        : base(message, innerException)
    {
        Scope = scope;
    }
}

/// <summary>
/// Durable character-database store that atomically reserves contiguous, non-overlapping ID blocks
/// across world workers and restarts.
/// </summary>
public interface IIdRangeReservationStore
{
    /// <summary>
    /// Atomically reserves a contiguous block of <paramref name="count"/> IDs for <paramref name="scope"/>.
    /// On first reservation (or whenever existing rows in the scope's backing tables exceed the stored watermark),
    /// the reservation automatically seeds above the maximum existing ID in the database.
    /// </summary>
    /// <param name="scope">Logical ID scope (see <see cref="IdRangeScopes"/>).</param>
    /// <param name="count">Number of contiguous IDs to reserve (must be &gt;= 1).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The reserved <see cref="IdRangeBlock"/> <c>[Start, EndInclusive]</c>.</returns>
    /// <exception cref="IdRangeExhaustedException">Thrown if the requested block would overflow the scope's maximum ID.</exception>
    Task<IdRangeBlock> ReserveBlockAsync(string scope, uint count, CancellationToken ct = default);
}

/// <summary>
/// Non-blocking, thread-safe ID allocator backed by durable <see cref="IdRangeBlock"/> reservations.
/// <para>
/// <see cref="Next"/> never blocks on I/O: it hands out IDs from the active in-memory block (or a queued prefetched block),
/// triggers a background low-watermark prefetch when remaining capacity drops to or below <see cref="LowWatermark"/>,
/// and fails closed with <see cref="IdRangeExhaustedException"/> if all reserved blocks are exhausted.
/// </para>
/// </summary>
public sealed class DurableIdBlockAllocator
{
    private readonly Lock _gate = new();
    private readonly Queue<IdRangeBlock> _queuedBlocks = new();
    private readonly Func<string, uint, CancellationToken, Task<IdRangeBlock>>? _reserveBlockAsync;

    private uint _currentNext;
    private uint _currentEndInclusive;
    private bool _hasActiveBlock;
    private ulong _remainingTotal;
    private Task? _prefetchTask;
    private Exception? _lastPrefetchError;

    public string Scope { get; }
    public uint BlockSize { get; }
    public uint LowWatermark { get; }
    public uint MaxId { get; }

    public DurableIdBlockAllocator(
        string scope,
        uint blockSize = 1024,
        uint lowWatermark = 256,
        Func<string, uint, CancellationToken, Task<IdRangeBlock>>? reserveBlockAsync = null,
        uint? maxId = null)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("Scope must be non-empty.", nameof(scope));
        if (blockSize == 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize, "BlockSize must be at least 1.");
        if (lowWatermark >= blockSize && blockSize > 1)
            throw new ArgumentOutOfRangeException(nameof(lowWatermark), lowWatermark, "LowWatermark must be less than BlockSize.");

        Scope = scope;
        BlockSize = blockSize;
        LowWatermark = lowWatermark;
        MaxId = maxId ?? IdRangeScopes.GetMaxId(scope);
        _reserveBlockAsync = reserveBlockAsync;
    }

    public DurableIdBlockAllocator(
        string scope,
        IIdRangeReservationStore store,
        uint blockSize = 1024,
        uint lowWatermark = 256,
        uint? maxId = null)
        : this(
            scope,
            blockSize,
            lowWatermark,
            store is not null
                ? store.ReserveBlockAsync
                : throw new ArgumentNullException(nameof(store)),
            maxId)
    {
    }

    /// <summary>
    /// Total remaining reserved IDs across the active block and any queued prefetched blocks.
    /// </summary>
    public ulong RemainingCount
    {
        get
        {
            lock (_gate)
            {
                return _remainingTotal;
            }
        }
    }

    /// <summary>
    /// True if a background prefetch task is currently in flight.
    /// </summary>
    public bool IsPrefetchInFlight
    {
        get
        {
            lock (_gate)
            {
                return _prefetchTask is { IsCompleted: false };
            }
        }
    }

    /// <summary>
    /// Enqueues a pre-reserved <see cref="IdRangeBlock"/> for consumption by <see cref="Next"/>.
    /// </summary>
    public void EnqueueBlock(IdRangeBlock block)
    {
        if (!string.Equals(block.Scope, Scope, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Block scope '{block.Scope}' does not match allocator scope '{Scope}'.",
                nameof(block));
        }

        if (block.Start > MaxId || block.EndInclusive > MaxId)
        {
            throw new IdRangeExhaustedException(
                Scope,
                $"Reserved block [{block.Start}, {block.EndInclusive}] exceeds MaxId {MaxId} for scope '{Scope}'.");
        }

        lock (_gate)
        {
            _lastPrefetchError = null;
            if (!_hasActiveBlock)
            {
                _currentNext = block.Start;
                _currentEndInclusive = block.EndInclusive;
                _hasActiveBlock = true;
            }
            else
            {
                _queuedBlocks.Enqueue(block);
            }

            _remainingTotal += block.Count;
        }
    }

    /// <summary>
    /// Reserves and enqueues a single block synchronously/asynchronously (intended for startup priming).
    /// </summary>
    public async Task<IdRangeBlock> ReserveAndEnqueueAsync(CancellationToken ct = default)
    {
        if (_reserveBlockAsync is null)
        {
            throw new InvalidOperationException(
                $"Allocator for scope '{Scope}' has no reservation store configured.");
        }

        IdRangeBlock block = await _reserveBlockAsync(Scope, BlockSize, ct).ConfigureAwait(false);
        EnqueueBlock(block);
        return block;
    }

    /// <summary>
    /// Ensures at least one block is reserved before the world loop begins.
    /// </summary>
    public async Task EnsurePrimedAsync(CancellationToken ct = default)
    {
        if (RemainingCount > 0)
            return;

        await ReserveAndEnqueueAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for any currently running background prefetch task to finish (useful in tests and shutdown).
    /// </summary>
    public Task WaitForPendingPrefetchAsync()
    {
        Task? task;
        lock (_gate)
        {
            task = _prefetchTask;
        }

        return task ?? Task.CompletedTask;
    }

    /// <summary>
    /// Allocates the next ID from the currently reserved block without blocking on I/O.
    /// Triggers a background prefetch when remaining reserved capacity reaches or drops below <see cref="LowWatermark"/>.
    /// Fails closed with <see cref="IdRangeExhaustedException"/> if no reserved IDs remain.
    /// </summary>
    public uint Next()
    {
        uint allocated;
        lock (_gate)
        {
            if (!_hasActiveBlock)
            {
                if (_queuedBlocks.TryDequeue(out IdRangeBlock nextBlock))
                {
                    _currentNext = nextBlock.Start;
                    _currentEndInclusive = nextBlock.EndInclusive;
                    _hasActiveBlock = true;
                }
                else
                {
                    // Fail closed, but keep trying: without a retry the allocator could never recover after the
                    // final prefetch failed (nothing else reserves at runtime). The caller still gets the exception.
                    if (_reserveBlockAsync is not null && (_prefetchTask is null || _prefetchTask.IsCompleted))
                    {
                        _prefetchTask = Task.Run(PrefetchNextBlockAsync);
                    }

                    Exception? prefetchErr = _lastPrefetchError;
                    throw new IdRangeExhaustedException(
                        Scope,
                        $"ID range allocator for scope '{Scope}' has no reserved IDs available (fail-closed).",
                        prefetchErr!);
                }
            }

            allocated = _currentNext;
            if (allocated == 0 || allocated > MaxId)
            {
                _hasActiveBlock = false;
                _queuedBlocks.Clear();
                _remainingTotal = 0;
                throw new IdRangeExhaustedException(
                    Scope,
                    $"ID range allocator for scope '{Scope}' exhausted valid ID space (MaxId={MaxId}).");
            }

            _remainingTotal--;

            if (_currentNext == _currentEndInclusive)
            {
                if (_queuedBlocks.TryDequeue(out IdRangeBlock queued))
                {
                    _currentNext = queued.Start;
                    _currentEndInclusive = queued.EndInclusive;
                    _hasActiveBlock = true;
                }
                else
                {
                    _hasActiveBlock = false;
                }
            }
            else
            {
                _currentNext++;
            }

            if (_reserveBlockAsync is not null
                && _remainingTotal <= LowWatermark
                && (_prefetchTask is null || _prefetchTask.IsCompleted))
            {
                _prefetchTask = Task.Run(PrefetchNextBlockAsync);
            }
        }

        return allocated;
    }

    /// <summary>
    /// Allocates the next ID as a signed 32-bit integer (for scopes such as <c>"guild"</c> and <c>"instance"</c>).
    /// Fails closed if the allocated value exceeds <see cref="int.MaxValue"/>.
    /// </summary>
    public int NextInt32()
    {
        uint next = Next();
        if (next > (uint)int.MaxValue)
        {
            throw new IdRangeExhaustedException(
                Scope,
                $"Allocated ID {next} for scope '{Scope}' exceeds Int32.MaxValue.");
        }

        return (int)next;
    }

    private async Task PrefetchNextBlockAsync()
    {
        if (_reserveBlockAsync is not { } reserveBlockAsync)
        {
            return;
        }

        try
        {
            IdRangeBlock block = await reserveBlockAsync(Scope, BlockSize, CancellationToken.None).ConfigureAwait(false);
            EnqueueBlock(block);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Diagnostics.FaultLedger.Shared.Record($"character.id-range-prefetch:{Scope}", ex);
            lock (_gate)
            {
                _lastPrefetchError = ex;
            }
        }
    }
}
