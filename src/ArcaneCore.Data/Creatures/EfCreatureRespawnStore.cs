using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Cluster;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Data.Creatures;

/// <summary>
/// EF Core implementation of <see cref="ICreatureRespawnStore"/> over the characters database; the body is <see cref="EfRespawnStore{TRow, TRecord, TKey}"/>
/// over <see cref="CreatureRespawnRow"/>.
/// </summary>
public sealed class EfCreatureRespawnStore(CharacterDbContext db, IRealmLeaseFenceSource? leaseFences = null)
    : EfRespawnStore<CreatureRespawnRow, CreatureRespawnRecord, CreatureRespawnKey>(db, leaseFences), ICreatureRespawnStore;
