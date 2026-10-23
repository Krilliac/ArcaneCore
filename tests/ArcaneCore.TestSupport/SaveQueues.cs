using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;

namespace ArcaneCore.TestSupport;

/// <summary>A save queue that drops every snapshot. <see cref="Instance"/> is the shared one for call sites that need no state of their own.</summary>
public sealed class NullSaveQueue : ICharacterSaveQueue
{
    public static readonly NullSaveQueue Instance = new();

    public void Enqueue(CharacterState state)
    {
    }
}

/// <summary>Records snapshots instead of saving them.</summary>
public sealed class RecordingSaveQueue : ICharacterSaveQueue
{
    public List<CharacterState> Saved { get; } = [];

    /// <summary>Character ids reported as having a snapshot waiting (<see cref="IsPending"/>).</summary>
    public HashSet<int> PendingIds { get; } = [];

    public void Enqueue(CharacterState state) => Saved.Add(state);

    public bool IsPending(int characterId) => PendingIds.Contains(characterId);
}
