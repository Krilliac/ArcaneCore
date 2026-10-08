using ArcaneCore.Kernel.Gm;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Gm.Audit;

/// <summary>
/// Persists mute and ticket writes made on the world thread, off it and in order: the shared
/// <see cref="KeyedStoreWriteQueue{TStore}"/> over <see cref="IGmAuditStore"/>. Every write sets one absolute value for its
/// key (<c>mute:&lt;account&gt;</c>, <c>ticket:&lt;id&gt;</c>), so a newer write for a key that is still waiting replaces
/// the older one; a write that fails every attempt is retained, never dropped, and carried by the next write of the key,
/// the periodic retry, <see cref="KeyedStoreWriteQueue{TStore}.RetryRetainedAsync"/> and the stop. The world keeps its own
/// in-memory working set, so a retained write never changes what staff see.
/// </summary>
public sealed class GmAuditWriteQueue(IServiceScopeFactory scopes, ILogger logger)
    : KeyedStoreWriteQueue<IGmAuditStore>(scopes, logger, "GM audit");
