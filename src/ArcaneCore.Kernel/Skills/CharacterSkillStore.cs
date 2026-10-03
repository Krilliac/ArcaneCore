namespace ArcaneCore.Kernel.Skills;

/// <summary>
/// Persistence of a character's skills (vmangos characters.sql <c>character_skills</c> and
/// <c>character_forgotten_skills</c>, docs/areas/skills.md). The store keeps whole snapshots, never deltas:
/// a later snapshot always supersedes every earlier one, so a lost or repeated write cannot leave a
/// half-applied state.
/// </summary>
public interface ICharacterSkillStore
{
    /// <summary>Everything stored for a character (empty for a character without rows or an unknown id).</summary>
    Task<CharacterSkillSnapshot> LoadAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replace the character's stored skills and forgotten weapon values with <paramref name="snapshot"/> in one
    /// transaction. False (and nothing written) when the character no longer exists, so a write queued before
    /// a deletion cannot resurrect rows.
    /// </summary>
    Task<bool> ReplaceSnapshotAsync(int characterId, CharacterSkillSnapshot snapshot, CancellationToken cancellationToken = default);
}
