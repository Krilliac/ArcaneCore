namespace ArcaneCore.Kernel.Characters.Pets;

/// <summary>Durable hunter-pet state. The pet number is the stable identity; a world GUID is transient.</summary>
public sealed record PersistentPetSnapshot(
    int CharacterId,
    uint PetNumber,
    uint Entry,
    byte Level,
    uint Experience,
    uint Health,
    uint Mana,
    uint Happiness,
    byte ReactState,
    IReadOnlyList<uint> ActionBar,
    IReadOnlyList<PersistentPetSpell> Spells,
    bool IsCurrent = true,
    IReadOnlyList<PersistentPetCooldown>? Cooldowns = null,
    string Name = "",
    uint NameTimestamp = 0,
    bool RenameAllowed = true,
    byte LoyaltyLevel = 1,
    int LoyaltyPoints = 1000,
    int TrainingPoints = 0)
{
    public static PersistentPetSnapshot Empty(int characterId, uint petNumber, uint entry, byte level)
        => new(characterId, petNumber, entry, level, 0, 1, 0, 0, 1, [], []);
}

public readonly record struct PersistentPetSpell(uint SpellId, bool Autocast, bool Passive);

public readonly record struct PersistentPetCooldown(byte Kind, uint SpellId, uint Category, long EndsAtUnixMs);

/// <summary>Characters-database seam for current hunter pets.</summary>
public interface IPersistentPetStore
{
    bool SupportsDetachedState => false;
    Task<PersistentPetSnapshot?> LoadCurrentAsync(int characterId, CancellationToken cancellationToken = default);
    /// <summary>Callable selection includes current and detached non-stabled hunter rows.</summary>
    Task<PersistentPetSnapshot?> LoadCallableAsync(int characterId, CancellationToken cancellationToken = default)
        => LoadCurrentAsync(characterId, cancellationToken);
    Task SaveCurrentAsync(PersistentPetSnapshot snapshot, CancellationToken cancellationToken = default);
    Task SaveDetachedAsync(PersistentPetSnapshot snapshot, CancellationToken cancellationToken = default)
        => Task.FromException(new NotSupportedException("detached hunter-pet persistence is not supported by this store"));
    Task DeleteAsync(int characterId, CancellationToken cancellationToken = default);
}
