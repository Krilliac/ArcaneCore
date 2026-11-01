using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.Data.Characters.Ownership;

/// <summary>
/// This process fenced itself off the characters database (<see cref="CharacterDatabaseWriterFence.Trip"/>): another world holds the writer lock, so nothing more goes into that database.
/// A <see cref="CharacterOwnershipRejectedException"/>, so every writer that already treats an ownership rejection as terminal (no retry spin, no further attempt) treats this the same way.
/// </summary>
public sealed class CharacterDatabaseFencedException(string reason)
    : CharacterOwnershipRejectedException("world", 0, $"the characters database writer lock is held by another world ({reason}); this process writes nothing more into that database")
{
    /// <summary>Why the fence was tripped.</summary>
    public string Reason { get; } = reason;
}

/// <summary>
/// The context-wide, one-way backstop of the characters database writer lock: once <see cref="Trip"/> is called, <see cref="CharacterDatabaseWriterFenceInterceptor"/> refuses everything
/// this process does on a <c>CharacterDbContext</c> (connection open, transaction start, every statement reads included, commit), whichever feature issues it. The drain lanes
/// (<c>StopDrainBudget</c>) are the polite path; this catches the writers that have no lane. Never reset: the process stops after the trip.
/// Thread-safe; the first reason stays. Reads are lock-free.
/// </summary>
public sealed class CharacterDatabaseWriterFence
{
    private string? _reason;

    /// <summary>True once <see cref="Trip"/> has been called.</summary>
    public bool IsTripped => Volatile.Read(ref _reason) is not null;

    /// <summary>The reason of the first <see cref="Trip"/>, or null while the fence is open.</summary>
    public string? Reason => Volatile.Read(ref _reason);

    /// <summary>Closes the fence. A second call keeps the first reason.</summary>
    public void Trip(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Interlocked.CompareExchange(ref _reason, reason, null);
    }

    /// <summary>Throws <see cref="CharacterDatabaseFencedException"/> once the fence is tripped.</summary>
    public void ThrowIfTripped()
    {
        if (Volatile.Read(ref _reason) is { } reason) throw new CharacterDatabaseFencedException(reason);
    }
}
