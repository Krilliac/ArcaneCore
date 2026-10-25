namespace ArcaneCore.Atlas.Core;

/// <summary>
/// A cache a module owns that <c>status</c> reports and <c>cache.clear</c> empties. Implementations must be thread-safe: the
/// properties are read from request threads while the cache is in use.
/// </summary>
public interface ICacheParticipant
{
    string Name { get; }

    /// <summary>Approximate bytes held.</summary>
    long Bytes { get; }

    /// <summary>Entries held.</summary>
    int Count { get; }

    /// <summary>Drops every entry; the next request rebuilds it.</summary>
    void Clear();
}

/// <summary>
/// The caches besides the tile cache (textures, model packages, and what later lanes add). Modules add theirs while registering;
/// the list is frozen after composition, so no lane edits the status command to add a cache.
/// </summary>
public sealed class CacheParticipants
{
    private readonly List<ICacheParticipant> _participants = [];
    private readonly Lock _gate = new();
    private bool _frozen;

    /// <summary>A snapshot of the participants in registration order.</summary>
    public IReadOnlyList<ICacheParticipant> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _participants];
            }
        }
    }

    /// <summary>Sum of <see cref="ICacheParticipant.Bytes"/> over every participant.</summary>
    public long TotalBytes => All.Sum(p => p.Bytes);

    /// <exception cref="ArgumentException">The name is empty or already taken.</exception>
    /// <exception cref="InvalidOperationException">The list is frozen.</exception>
    public void Add(ICacheParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentException.ThrowIfNullOrWhiteSpace(participant.Name);
        lock (_gate)
        {
            if (_frozen)
            {
                throw new InvalidOperationException("Cache participants cannot be added after startup.");
            }

            if (_participants.Any(p => string.Equals(p.Name, participant.Name, StringComparison.Ordinal)))
            {
                throw new ArgumentException($"A cache named '{participant.Name}' is already registered.");
            }

            _participants.Add(participant);
        }
    }

    public void Freeze()
    {
        lock (_gate)
        {
            _frozen = true;
        }
    }

    /// <summary>Empties every participant.</summary>
    public void ClearAll()
    {
        foreach (ICacheParticipant participant in All)
        {
            participant.Clear();
        }
    }
}
