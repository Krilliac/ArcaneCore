namespace ArcaneCore.Game.Maps.Collision.VMaps;

/// <summary>Why a shared (outside the serial gate) ground drape gave up and left the leg to the gated one (<see cref="GroundHeight.TryDrapePathShared"/>).</summary>
public enum SharedDrapeAbort
{
    /// <summary>The shared drape finished.</summary>
    None = 0,

    /// <summary>A terrain tile the drape needs is not in memory (the gated drape would keep the height or take the mesh fallback).</summary>
    TerrainTileNotLoaded,

    /// <summary>The map's vmap tree, or the model tile under a sampled point, is not loaded.</summary>
    ModelTileNotLoaded,

    /// <summary>The drape reached a navmesh branch (floor fallback or mesh walk) and the navmesh is not one a shared query may use.</summary>
    MeshNotShared,

    /// <summary>A liquid read needed a terrain tile that is not in memory.</summary>
    LiquidTileNotLoaded,

    /// <summary>The services are not ones whose reads are known to be safe outside the gate, or the query asks to load tiles.</summary>
    Unsupported,
}

/// <summary>
/// Process-wide counts of the creature drapes that ran outside the serial gate and of those that fell back to it (any thread writes, the
/// metrics feature reads), so a live server can confirm the hit rate of <see cref="GroundHeight.TryDrapePathShared"/>.
/// </summary>
public static class SharedDrapeStats
{
    // One init-only array rather than writable static fields (StaticMutableStateAuditTests): [0] hits, [1] fallbacks, [2 + reason] per reason.
    private static readonly long[] s_counts = new long[2 + 6];

    /// <summary>Drapes answered outside the gate.</summary>
    public static long Hits => Interlocked.Read(ref s_counts[0]);

    /// <summary>Drapes that gave up and ran again inside the gate.</summary>
    public static long Fallbacks => Interlocked.Read(ref s_counts[1]);

    /// <summary>Fallbacks for one reason.</summary>
    public static long FallbacksFor(SharedDrapeAbort reason) => Interlocked.Read(ref s_counts[2 + (int)reason]);

    internal static void RecordHit() => Interlocked.Increment(ref s_counts[0]);

    internal static void RecordFallback(SharedDrapeAbort reason)
    {
        Interlocked.Increment(ref s_counts[1]);
        Interlocked.Increment(ref s_counts[2 + (int)reason]);
    }
}

/// <summary>
/// The thread-local mode in which <see cref="VMapManager"/> (and the terrain and navmesh reads of <see cref="GroundHeight"/>) answer a
/// ground drape from what is already loaded, outside the serial gate, and say so when they would have to load or look something up.
/// <para>
/// Memory model. <see cref="VMapManager"/> is single-writer: only the owner thread (the world thread, or the thread holding the serial
/// gate) loads and unloads tiles. A shared reader runs on another map thread at the same time, so every mutation of the state a shared
/// read looks at (the tree table, the tiles-known-empty set, the per-tile slot tables of <see cref="VMapTree"/>) is made under the write
/// side of one <see cref="ReaderWriterLockSlim"/>, and every shared read is made under its read side. The lock gives the two things a
/// bare volatile read cannot: mutual exclusion, so a read never sees a half-installed or half-released tile (a dictionary mid-insert, a
/// slot count raised before its instance is stored), and a full fence on both sides, so what the owner wrote before releasing the write
/// lock is visible to the reader that acquires the read lock next. A tile that is unloaded has its slots released inside the write lock;
/// a reader is either wholly before that (it sees the models, which stay reachable through its own references and are only collected by the
/// GC after it drops them) or wholly after it (it sees the tile as not loaded and aborts). Nothing is freed that a reader still uses, so
/// no reference count or retire list is needed: the GC owns the memory and the lock orders the visibility. The owner's own reads take
/// nothing (it is the only writer), as in <c>NavMeshPathfinder</c>; the write lock is held only for the in-memory install or release,
/// never for a file read or a model parse.
/// </para>
/// <para>
/// A shared read that finds a needed piece not loaded does not wait and does not load: it records the reason here, and the drape
/// returns null so the caller repeats it inside the gate, where loading is allowed. Every sample the shared drape returns was therefore
/// read from fully loaded tiles by the same arithmetic as the gated drape. The vmap read lock is held once for the whole drape, so the
/// model data is one snapshot; the terrain tiles are not freed during the maps phase. The navmesh read lock is taken per query (it is also
/// what in-flight path jobs hold, and holding it across a drape would make every tile change cancel them), so a navmesh tile added or
/// removed mid-drape can make two samples reflect two mesh states: each sample equals the gated answer for the mesh state at that moment.
/// </para>
/// </summary>
internal static class VMapSharedReader
{
    [ThreadStatic]
    private static bool t_active;

    [ThreadStatic]
    private static SharedDrapeAbort t_abort;

    /// <summary>Whether this thread is inside a shared drape.</summary>
    internal static bool Active => t_active;

    /// <summary>Whether this thread's shared drape has given up.</summary>
    internal static bool Aborted => t_abort != SharedDrapeAbort.None;

    /// <summary>Record why the shared drape gives up (the first reason stays).</summary>
    internal static void Abort(SharedDrapeAbort reason)
    {
        if (t_abort == SharedDrapeAbort.None)
        {
            t_abort = reason;
        }
    }

    [ThreadStatic]
    private static ReaderWriterLockSlim? t_heldLock;

    /// <summary>Whether this thread's shared drape already holds <paramref name="vmapLock"/> for reading (it takes it once for the whole drape).</summary>
    internal static bool HoldsLock(ReaderWriterLockSlim vmapLock) => ReferenceEquals(t_heldLock, vmapLock);

    /// <summary>Enter shared mode on this thread; dispose to restore the previous mode.</summary>
    internal static Scope Enter(ReaderWriterLockSlim? vmapLock = null) => new(t_active, t_abort, vmapLock);

    /// <summary>The reason this thread's shared drape gave up, or <see cref="SharedDrapeAbort.None"/>.</summary>
    internal static SharedDrapeAbort Reason => t_abort;

    internal readonly struct Scope : IDisposable
    {
        private readonly bool _wasActive;
        private readonly SharedDrapeAbort _wasAbort;
        private readonly ReaderWriterLockSlim? _entered;
        private readonly ReaderWriterLockSlim? _wasHeld;

        // Nesting: a scope opened inside another on the same thread and the same lock joins the outer one (it does not take the lock again,
        // which the non-recursive lock would refuse) and restores the outer state when it ends; the outer scope releases the lock.
        internal Scope(bool wasActive, SharedDrapeAbort wasAbort, ReaderWriterLockSlim? vmapLock)
        {
            _wasActive = wasActive;
            _wasAbort = wasAbort;
            _wasHeld = t_heldLock;
            if (vmapLock is not null && !ReferenceEquals(t_heldLock, vmapLock))
            {
                vmapLock.EnterReadLock(); // once for the whole drape: one vmap snapshot, one acquire/release pair instead of one per sample
                _entered = vmapLock;
                t_heldLock = vmapLock;
            }

            t_active = true;
            t_abort = SharedDrapeAbort.None;
        }

        public void Dispose()
        {
            t_active = _wasActive;
            t_abort = _wasAbort;
            t_heldLock = _wasHeld;
            _entered?.ExitReadLock();
        }
    }
}
