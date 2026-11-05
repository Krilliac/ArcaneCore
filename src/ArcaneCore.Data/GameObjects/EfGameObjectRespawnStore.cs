using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.WorldData.GameObjects;

namespace ArcaneCore.Data.GameObjects;

/// <summary>
/// EF Core implementation of <see cref="IGameObjectRespawnStore"/> over the characters database; the body is <see cref="EfRespawnStore{TRow, TRecord, TKey}"/>
/// over <see cref="GameObjectRespawnRow"/>.
/// </summary>
public sealed class EfGameObjectRespawnStore(CharacterDbContext db, IRealmLeaseFenceSource? leaseFences = null)
    : EfRespawnStore<GameObjectRespawnRow, GameObjectRespawnRecord, GameObjectRespawnKey>(db, leaseFences), IGameObjectRespawnStore;
