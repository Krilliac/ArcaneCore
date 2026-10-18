namespace ArcaneCore.Kernel.Characters.Pets;

/// <summary>Where a stored hunter pet is: its owner, its name and whether it is the owner's current pet or in a stable slot.</summary>
public sealed record StoredPetLocation(uint PetNumber, int CharacterId, string Name, bool Stabled);

/// <summary>
/// GM access to stored hunter pets by pet number, whoever owns them (vmangos HandlePetRenameCommand / HandlePetDeleteCommand work on
/// character_pet by id). Callers keep the owner offline; the world's pet cache is the caller's to forget. Every write is one transaction.
/// </summary>
public interface IPetAdminStore
{
    /// <summary>The stored pet <paramref name="petNumber"/> (the current pet row first, then the stable), or null.</summary>
    Task<StoredPetLocation?> FindAsync(uint petNumber, CancellationToken cancellationToken = default);

    /// <summary>Rename the stored pet; false when no row has that number.</summary>
    Task<bool> RenameAsync(uint petNumber, string name, CancellationToken cancellationToken = default);

    /// <summary>Delete the stored pet and its cooldowns; false when no row has that number.</summary>
    Task<bool> DeleteAsync(uint petNumber, CancellationToken cancellationToken = default);
}