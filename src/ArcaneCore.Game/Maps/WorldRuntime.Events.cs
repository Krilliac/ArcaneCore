using System.Diagnostics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Maps;

/// <summary>
/// The world events (<see cref="MapCreated"/>, <see cref="Updated"/>, <see cref="WorldTick"/>, the player events) and the timed handler dispatch.
/// </summary>
public sealed partial class WorldRuntime
{
    /// <summary>
    /// The handlers of <see cref="Updated"/> and <see cref="WorldTick"/> with their tick-statistics names, kept for the multicast delegate they
    /// were split from (<see cref="HandlersOf"/>): the invocation list, its cast and the name lookups were per tick. A delegate is immutable,
    /// so a subscription change makes a new one and the next tick splits it again. World thread.
    /// </summary>
    private HandlerTable _updatedHandlers = HandlerTable.None;
    private HandlerTable _worldTickHandlers = HandlerTable.None;

    /// <summary>
    /// Raised on the world thread after a map and its default updaters have been registered.
    /// Features install their per-map simulation here, including maps first visited later.
    /// </summary>
    public event Action<Map>? MapCreated;

    /// <summary>
    /// Raised on the world thread when an instance map is about to be unloaded (it is empty,
    /// nobody is in transit from it, and its owner asked with <see cref="UnloadMap"/>), before
    /// its grids are unloaded and it leaves <see cref="Maps"/>. Features drop their per-map
    /// state here (vmangos <c>MapManager::DeleteInstance</c> → <c>Map::UnloadAll</c>).
    /// </summary>
    public event Action<Map>? MapUnloading;

    /// <summary>
    /// Raised once per world tick after posted commands and map updates, even when no maps
    /// exist. The argument is elapsed milliseconds. World features use this for global
    /// maintenance; a failing handler is logged and does not stop the others.
    /// </summary>
    public event Action<uint>? Updated;

    /// <summary>
    /// Raised on the world thread once a player has entered the world and its client has the
    /// full login sequence, i.e. after vmangos' <c>SendInitialPacketsAfterAddToMap</c> — where
    /// vmangos <c>HandlePlayerLogin</c> announces the member to its group and sends friend
    /// status. Raised by the login path through <see cref="NotifyLoggedIn"/>. A failing handler
    /// is logged and does not stop the others.
    /// </summary>
    public event Action<Player>? PlayerLoggedIn;

    /// <summary>
    /// Raised on the world thread when a player is about to leave the world (logout completion
    /// or disconnect), while it is still in its map and the online registry, before it is
    /// saved. A failing handler is logged and does not stop the others or the removal.
    /// </summary>
    public event Action<Player>? PlayerLoggingOut;

    /// <summary>
    /// Raised once per world tick on the world thread, after the posted commands and before the maps update,
    /// with the tick's elapsed milliseconds (vmangos <c>World::Update</c> drives <c>sGameEventMgr.Update</c> from
    /// its own timers, World.cpp:2106-2111; this is the equivalent seam for world-level services). A failing
    /// handler is logged and does not stop the others.
    /// </summary>
    public event Action<uint>? WorldTick;

    /// <summary>
    /// Raised on the world thread as the very first step of every tick, before the posted commands run and while the previous tick's phases
    /// (<see cref="LastTickPhases"/>), duration (<see cref="LastTickDurationMicros"/>) and slowest features (<see cref="CopyLastTickTopFeatures"/>)
    /// are still readable. Untimed: a handler must be a few stores (the tick watchdog's frame boundary). A failing handler is logged; it stops the handlers after it.
    /// </summary>
    public event Action<uint>? TickStarting;

    /// <summary>Raise <see cref="Updated"/>, timing each world feature for the tick statistics.</summary>
    private void RaiseTimed(Action<uint>? handlers, uint diffMs)
    {
        if (handlers is null)
        {
            return;
        }

        foreach ((Action<uint> handler, string name) in HandlersOf(ref _updatedHandlers, handlers, TickWorkNames.Of))
        {
            long start = Stopwatch.GetTimestamp();
            long startBytes = GC.GetAllocatedBytesForCurrentThread();
            SetPosition(TickPhase.Features, handler, start);
            try
            {
                handler(diffMs);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("world.updated-handler", ex);
                _logger.LogFault("handler.updated", ex, "{Event} handler {Handler} failed for {Subject}", nameof(Updated), name, diffMs);
            }

            long micros = Micros(Stopwatch.GetTimestamp() - start);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;
            Stats.RecordFeature(name, micros, bytes);
            NoteSlowest(name, micros);
            if (FeatureObserver is { } featureObserver)
            {
                try
                {
                    featureObserver(name, micros, bytes);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("world.tick-observer", ex);
                    _logger.LogFault("world.tick-observer", ex, "FeatureObserver failed for feature {Feature}", name);
                }
            }
        }
    }

    private void RaiseTickStarting(uint diffMs)
    {
        Action<uint>? handlers = TickStarting;
        if (handlers is null)
        {
            return;
        }

        try
        {
            handlers(diffMs);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("world.tick-starting", ex);
            _logger.LogFault("world.tick-starting", ex, "{Event} handler failed for {Subject}", nameof(TickStarting), diffMs);
        }
    }

    /// <summary>Raise <see cref="WorldTick"/>, timing each handler as the feature "WorldTick:&lt;Type&gt;" in the tick statistics.</summary>
    private void RaiseWorldTick(uint diffMs)
    {
        Action<uint>? handlers = WorldTick;
        if (handlers is null)
        {
            return;
        }

        foreach ((Action<uint> handler, string name) in HandlersOf(ref _worldTickHandlers, handlers, WorldTickName))
        {
            long start = Stopwatch.GetTimestamp();
            long startBytes = GC.GetAllocatedBytesForCurrentThread();
            SetPosition(TickPhase.WorldTick, handler, start);
            try
            {
                handler(diffMs);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("world.tick", ex);
                _logger.LogFault("worldtick", ex, "{Event} handler {Handler} failed for {Subject}", nameof(WorldTick), TickWorkNames.Of(handler), diffMs);
            }

            long micros = Micros(Stopwatch.GetTimestamp() - start);
            Stats.RecordFeature(name, micros, GC.GetAllocatedBytesForCurrentThread() - startBytes);
            NoteSlowest(name, micros);
        }
    }

    /// <summary>The tick-statistics name of a <see cref="WorldTick"/> handler: "WorldTick:" and its class (the method name for a static one).</summary>
    internal static string WorldTickName(Action<uint> handler)
        => handler.Method.DeclaringType is null ? "WorldTick:" + handler.Method.Name : "WorldTick:" + TickWorkNames.Of(handler);

    /// <summary>
    /// The handlers of <paramref name="handlers"/> in invocation order with their names, split once per delegate instance and kept in
    /// <paramref name="table"/> until the event's delegate changes.
    /// </summary>
    private static (Action<uint> Handler, string Name)[] HandlersOf(ref HandlerTable table, Action<uint> handlers, Func<Action<uint>, string> name)
    {
        if (!ReferenceEquals(table.Source, handlers))
        {
            table = new HandlerTable(handlers, [.. handlers.GetInvocationList().Cast<Action<uint>>().Select(h => (h, name(h)))]);
        }

        return table.Handlers;
    }

    private sealed record HandlerTable(Action<uint>? Source, (Action<uint> Handler, string Name)[] Handlers)
    {
        public static readonly HandlerTable None = new(null, []);
    }

    private void Raise<T>(Action<T>? handlers, T subject, string name)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action<T> handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                handler(subject);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("world.event-handler", ex);
                object? description = subject is Player player ? player.Name : subject;
                _logger.LogFault("event.handler", ex, "{Event} handler {Handler} failed for {Subject}", name, TickWorkNames.Of(handler), description);
            }
        }
    }
}
