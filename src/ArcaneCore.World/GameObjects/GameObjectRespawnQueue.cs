using ArcaneCore.Game.GameObjects;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// The world's <see cref="IGameObjectRespawnPersistence"/>: reads answer from memory, and every change is written by one background consumer, in order,
/// off the world thread, with retained retries and a final throwing drain at stop. The whole body is <see cref="RespawnWriteQueue{TRecord, TKey, TStore}"/>;
/// this type only names the game object record, key and store. Without a registered <see cref="IGameObjectRespawnStore"/> writes are discarded.
/// </summary>
public sealed class GameObjectRespawnQueue(IServiceScopeFactory scopes, ILogger logger, StopDrainBudget? drain = null)
    : RespawnWriteQueue<GameObjectRespawnRecord, GameObjectRespawnKey, IGameObjectRespawnStore>(scopes, logger, drain, "game object", "gameobject-respawn"), IGameObjectRespawnPersistence;
