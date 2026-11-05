using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Creatures;

/// <summary>
/// The world's <see cref="ICreatureRespawnPersistence"/>: reads answer from memory, and every change is written by one background consumer, in order,
/// off the world thread, with retained retries and a final throwing drain at stop. The whole body is <see cref="RespawnWriteQueue{TRecord, TKey, TStore}"/>;
/// this type only names the creature record, key and store. Without a registered <see cref="ICreatureRespawnStore"/> writes are discarded.
/// </summary>
public sealed class CreatureRespawnQueue(IServiceScopeFactory scopes, ILogger logger, StopDrainBudget? drain = null)
    : RespawnWriteQueue<CreatureRespawnRecord, CreatureRespawnKey, ICreatureRespawnStore>(scopes, logger, drain, "creature", "respawn"), ICreatureRespawnPersistence;
