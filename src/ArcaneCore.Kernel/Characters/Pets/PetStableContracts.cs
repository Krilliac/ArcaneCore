namespace ArcaneCore.Kernel.Characters.Pets;

/// <summary>A hunter pet in a stable slot (vmangos character_pet.slot PET_SAVE_FIRST_STABLE_SLOT..PET_SAVE_LAST_STABLE_SLOT, 1-based).</summary>
public sealed record StabledPet(byte Slot, PersistentPetSnapshot Pet);

/// <summary>A hunter's stable: the slots bought from a stable master (vmangos characters.stable_slots) and the pets in them.</summary>
public sealed record PetStableState(byte PurchasedSlots, IReadOnlyList<StabledPet> Pets)
{
    public static PetStableState Empty { get; } = new(0, []);
}

/// <summary>Characters-database seam for the hunter pet stable. Every call is one transaction.</summary>
public interface IPetStableStore
{
    Task<PetStableState> LoadAsync(int characterId, CancellationToken cancellationToken = default);

    Task SetPurchasedSlotsAsync(int characterId, byte slots, CancellationToken cancellationToken = default);

    /// <summary>Move <paramref name="active"/> (the character's current or dismissed pet) into the free <paramref name="slot"/>; the character_pet row is removed.</summary>
    Task StableAsync(int characterId, byte slot, PersistentPetSnapshot active, CancellationToken cancellationToken = default);

    /// <summary>
    /// Take the stabled pet <paramref name="petNumber"/> out as the character's current pet; <paramref name="active"/>, when given, goes into the slot it
    /// leaves (vmangos HandleUnstablePet / HandleStableSwapPet). Null when the pet is not in the stable (nothing changes).
    /// </summary>
    Task<PersistentPetSnapshot?> UnstableAsync(int characterId, uint petNumber, PersistentPetSnapshot? active, CancellationToken cancellationToken = default);
}