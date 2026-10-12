namespace ArcaneCore.Kernel.Characters;

/// <summary>
/// The ownership authority of the current process, read by the characters-database write fence when a snapshot carries no token of its own.
/// An explicit per-snapshot token always takes priority over this ambient one.
/// </summary>
public interface ICharacterOwnershipContext
{
    /// <summary>The token this process holds, or null while it holds none (fencing off, not yet claimed, or released).</summary>
    CharacterOwnershipToken? Current { get; }
}

/// <summary>
/// The one mutable holder behind <see cref="ICharacterOwnershipContext"/>: a singleton the world host sets after it claims the
/// database and clears when it releases. Thread-safe: <see cref="Current"/> is an atomic reference read and write.
/// </summary>
public sealed class CharacterOwnershipContext : ICharacterOwnershipContext
{
    private CharacterOwnershipToken? _current;

    /// <summary>The token this process holds; set by the host that owns the claim.</summary>
    public CharacterOwnershipToken? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}
