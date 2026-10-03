using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Loot;

namespace ArcaneCore.Game.Loot;

/// <summary>Where a durable loot operation ended up.</summary>
public enum LootOutcome
{
    /// <summary>Nothing was attempted (shutdown, a failed pre-commit save).</summary>
    NotStarted,

    /// <summary>The transaction did not commit; the stored state stands.</summary>
    Before,

    /// <summary>The transaction committed.</summary>
    After,

    /// <summary>The store could not be read back; the key stays blocked until restart and the actor is kicked.</summary>
    Unknown,
}

/// <summary>The online character whose inventory a loot operation changes, frozen with its planned complete state.</summary>
public sealed class LootActor(Player player, CharacterState before, CharacterState after)
{
    public Player Player { get; } = player;

    /// <summary>The snapshot saved through the ordered save queue immediately before the commit (complete inventory).</summary>
    public CharacterState Before { get; } = before;

    /// <summary>The complete state to publish: <see cref="Before"/> with the awarded inventory.</summary>
    public CharacterState After { get; } = after;
}

/// <summary>
/// One durable chest operation: a generation (no actor), an owner release (no actor) or an item
/// take (one actor). <see cref="Expected"/> is always the coordinator's committed cache value,
/// never the live bag.
/// </summary>
public sealed class LootOperation
{
    public required LootStateKey Key { get; init; }

    public LootStateRecord? Expected { get; init; }

    public required LootStateRecord Updated { get; init; }

    public IReadOnlyList<LootAward> Awards { get; init; } = [];

    public LootActor? Actor { get; init; }

    /// <summary>
    /// World thread, after a committed outcome and only while the actor is still current: runs
    /// inside the actor's settlement publication window (apply the staged inventory here).
    /// </summary>
    public Action? PublishActor { get; init; }

    /// <summary>
    /// World thread, always, after the actor was released. Arguments: the outcome, whether the
    /// world is still publishing (false during shutdown: touch nothing), and whether the actor was
    /// still the current online character.
    /// </summary>
    public required Action<LootOutcome, bool, bool> Finished { get; init; }
}

/// <summary>
/// What the game needs to keep a chest's consumed and remaining loot across map unload,
/// recreation and restart. Implemented by the world daemon, which owns the durable store, the
/// committed cache and the off-thread settlement of each operation. World thread only.
/// <para>The cache holds committed state only. A key with an operation in flight is
/// <see cref="IsPending"/>: nothing may be rebuilt, opened or taken for it until the finalizer
/// ran. A key whose outcome could not be read back is blocked until restart.</para>
/// </summary>
public interface ILootStateCoordinator
{
    /// <summary>Unix seconds now (respawn times are stored in this timeline).</summary>
    long UnixNow { get; }

    /// <summary>A store exists and <paramref name="map"/> is a dungeon instance map with a live, non-deleted logical save.</summary>
    bool CanPersist(Map map);

    /// <summary>The committed state of the chest, or null.</summary>
    LootStateRecord? Find(LootStateKey key);

    /// <summary>An operation for the key is in flight.</summary>
    bool IsPending(LootStateKey key);

    /// <summary>The key is pending, or an earlier outcome was unreadable and the key is blocked until restart.</summary>
    bool IsBlocked(LootStateKey key);

    /// <summary>
    /// Freeze <paramref name="player"/>'s planned inventory (<paramref name="before"/> to
    /// <paramref name="after"/>) for an operation, or null when the player may not take part now
    /// (not current, teleporting, held, quarantined, logging out, in another settlement).
    /// </summary>
    LootActor? CreateActor(Player player, InventorySnapshot before, InventorySnapshot after);

    /// <summary>Start an operation; false (nothing started, nothing frozen) when capacity, shutdown, a pending key or an actor refuses.</summary>
    bool TryStart(LootOperation operation);
}
