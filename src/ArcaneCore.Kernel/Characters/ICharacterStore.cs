namespace ArcaneCore.Kernel.Characters;

/// <summary>Persistence seam for player characters.</summary>
public interface ICharacterStore
{
    Task<IReadOnlyList<CharacterRecord>> GetByAccountAsync(int accountId, CancellationToken cancellationToken = default);

    Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<bool> IsNameTakenAsync(string name, CancellationToken cancellationToken = default);

    Task<int> CountByAccountAsync(int accountId, CancellationToken cancellationToken = default);

    Task<CharacterRecord> CreateAsync(CharacterRecord character, CancellationToken cancellationToken = default);

    /// <summary>Delete a character owned by the given account. Returns true if a row was removed.</summary>
    Task<bool> DeleteAsync(int id, int accountId, CancellationToken cancellationToken = default);

    /// <summary>Persist in-world state captured by the world thread. A missing row is ignored (deleted meanwhile).</summary>
    Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActionButton>> GetActionButtonsAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Every character's identity, to fill the name cache at startup.</summary>
    Task<IReadOnlyList<CharacterIdentity>> GetAllIdentitiesAsync(CancellationToken cancellationToken = default);
}
