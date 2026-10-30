using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Updates;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Maps;

public sealed partial class Map
{
    /// <summary>
    /// The world's serial gate for map work (a no-op outside the parallel maps phase), with this map's held-back packets sent first.
    /// </summary>
    internal WorldRuntime.SerialScope EnterSerialAndFlush()
    {
        if (_holdingGate)
        {
            FlushOutbox();
            return default;
        }

        WorldRuntime.SerialScope scope = _world.EnterSerial();
        try
        {
            FlushOutbox();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The caller never receives the scope, so nothing else would release the gate: the map threads would block on it for good.
            scope.Dispose();
            _outbox.Clear(); // a packet that throws would otherwise fail every later flush too
            _logger.LogFault("map.gate-flush", ex, "map {MapId} instance {InstanceId} outbox flush failed; the serial gate was released", MapId, InstanceId);
            throw;
        }

        return scope;
    }

    /// <summary>The serial gate for one stage of this map's update (a no-op scope outside the parallel maps phase).</summary>
    /// <remarks>
    /// Consecutive gated stages of one map update share ONE entry of the gate: it is taken at the first gated stage and kept until an
    /// ungated updater runs or the update ends (<see cref="ReleaseHeldGate"/>), so the scope returned here is a no-op. A map-local stage
    /// between two gated ones (ActiveCells) runs inside the held gate; it is cheap. Live, redeploy #5: about 16 gate entries per map
    /// and tick (1.1M a minute, half of them contended hand-offs between map threads) for no extra overlap.
    /// </remarks>
    private WorldRuntime.SerialScope Gate(MapStage stage)
    {
        if (_world.IsParallelPhase && _world.StageGates.IsGated(stage))
        {
            HoldGate();
        }

        return default;
    }

    private WorldRuntime.SerialScope GateUpdater(IMapUpdater updater)
    {
        if (_world.IsParallelPhase)
        {
            if (_world.StageGates.IsGated(updater)
                || (_players.Count == 0 && _objects.Count == 0 && updater.GetType().Assembly == typeof(Map).Assembly))
            {
                HoldGate();
            }
            else
            {
                ReleaseHeldGate(); // an ungated updater is what the other map threads overlap with
            }
        }

        return default;
    }

    private WorldRuntime.SerialScope _heldGate;
    private bool _holdingGate;

    private void HoldGate()
    {
        if (!_holdingGate)
        {
            _heldGate = EnterSerialAndFlush();
            _holdingGate = true;
        }
    }

    /// <summary>Leave the gate this map update holds for its gated stages (end of the update, or before an ungated updater).</summary>
    private void ReleaseHeldGate()
    {
        if (_holdingGate)
        {
            _holdingGate = false;
            _heldGate.Dispose();
        }
    }
}
