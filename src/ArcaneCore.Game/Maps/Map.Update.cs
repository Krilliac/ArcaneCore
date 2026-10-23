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
    private MapUpdateDiagnostics? _diagnostics;

    /// <summary>The map's diagnostics record, cleared for the next update (one per map; world thread).</summary>
    internal MapUpdateDiagnostics ReusableDiagnostics()
    {
        _diagnostics ??= new MapUpdateDiagnostics();
        _diagnostics.Reset();
        return _diagnostics;
    }

    /// <summary>True while <see cref="Update(uint, MapUpdateDiagnostics?)"/> is running with a non-null <see cref="MapUpdateDiagnostics"/>.</summary>
    internal bool IsCollectingDiagnostics { get; private set; }

    /// <summary>One simulation step (world thread).</summary>
    public void Update(uint diffMs)
        => Update(diffMs, diagnostics: null);

    internal void Update(uint diffMs, MapUpdateDiagnostics? diagnostics)
    {
        IsCollectingDiagnostics = diagnostics is not null;
        try
        {
            UpdateCore(diffMs, diagnostics);
        }
        finally
        {
            IsCollectingDiagnostics = false;
            ReleaseHeldGate();
        }
    }

    private void UpdateCore(uint diffMs, MapUpdateDiagnostics? diagnostics)
    {
        diagnostics?.Begin();
        diagnostics?.BeginTiles();
        GridLifecycleCounters gridsBefore = diagnostics is not null ? _grid.Counters : default;
        // While maps update in parallel, every stage that is not classified map-local runs inside the world's serial gate
        // (MapStageGates; docs/research/multithreading-plan-20261017.md, W3). With one map thread the gates are no-ops.
        WorldRuntime.SerialScope gate = Gate(MapStage.Packets);
        try
        {
            UpdatePacketsAndTimers();
        }
        finally
        {
            gate.Dispose();
        }

        diagnostics?.EndPackets();

        gate = Gate(MapStage.Heartbeat);
        try
        {
            UpdateHeartbeats(diffMs, diagnostics);
        }
        finally
        {
            gate.Dispose();
        }

        diagnostics?.EndHeartbeat();

        // vmangos Map::UpdateCells marks the cells around players and active objects after the sessions (UpdateActiveCellsSynch).
        if (UpdatesActiveCellsOnly)
        {
            gate = Gate(MapStage.ActiveCells);
            try
            {
                _grid.MarkActiveCells();
            }
            finally
            {
                gate.Dispose();
            }
        }
        diagnostics?.EndActiveCells();

        // (1c) per-map systems (creatures, …) — see IMapUpdater
        foreach (IMapUpdater updater in _updaters)
        {
            if (_updaterFaults.Count > 0 && _updaterFaults.TryGetValue(updater, out int faults) && faults < 0)
            {
                continue; // isolated by the fault breaker (see ClearUpdaterFaults)
            }

            gate = GateUpdater(updater);
            try
            {
                updater.Update(this, diffMs);
                if (_updaterFaults.Count > 0)
                {
                    _updaterFaults.Remove(updater); // only consecutive failures count
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("map.updater", ex);
                _logger.LogFault("map.updater", ex,
                    "map {MapId} instance {InstanceId} updater {Updater} failed (diff={DiffMs} ms, players={Players}, objects={Objects})",
                    MapId, InstanceId, updater.GetType().Name, diffMs, _players.Count, _objects.Count);
                CountUpdaterFault(updater, ex);
            }
            finally
            {
                gate.Dispose();
            }

            diagnostics?.EndUpdater(updater);
        }

        if (_outbox.Count > 0)
        {
            // Packets an ungated stage held back go out before this map's output stage, as they would have in the serial maps phase.
            using WorldRuntime.SerialScope flush = EnterSerialAndFlush();
        }

        UpdateOutput(diffMs, diagnostics, gridsBefore);
    }

    /// <summary>Stage (1): in-world packets, players in transit, logout timers.</summary>
    private void UpdatePacketsAndTimers()
    {
        // (1) in-world packets, over a snapshot (a handler may add or remove players); the snapshot list is reused
        List<Player> snapshot = _updatePlayers;
        snapshot.AddRange(_players.Values);
        try
        {
            foreach (Player player in snapshot)
            {
                if (player.Map != this)
                {
                    continue; // removed while processing an earlier player
                }

                ProcessPackets(player);
            }
        }
        finally
        {
            snapshot.Clear();
        }

        // (1a) players in transit from this map: only their world-port ack is handled (the
        // session drops everything else while the player is in no map — vmangos STATUS_TRANSFER).
        foreach (Player player in _transit.ToArray())
        {
            if (player.Map is not null || !_world.IsOnline(player.Guid))
            {
                _transit.Remove(player);
                continue;
            }

            ProcessPackets(player);
        }

        // (1b) timers: logouts whose countdown is over (vmangos WorldSession::Update → LogoutPlayer)
        uint now = _world.NowMs;
        snapshot.AddRange(_players.Values);
        try
        {
            foreach (Player player in snapshot)
            {
                if (player.Map == this && player.IsLogoutDue(now, _world.Options.LogoutDelayMs))
                {
                    _world.LogoutPlayer(player);
                }
            }
        }
        finally
        {
            snapshot.Clear();
        }
    }

    /// <summary>Unit heartbeats over the membership snapshot.</summary>
    private void UpdateHeartbeats(uint diffMs, MapUpdateDiagnostics? diagnostics)
    {
        // Snapshot only when membership changes. Heartbeat callbacks can add/remove units: keep this tick's
        // snapshot intact and rebuild next tick, still skipping removed units below. Timers and dictionary order
        // are unchanged (vmangos Object.cpp WorldObject::Update / Unit.cpp Unit::Heartbeat).
        if (_heartbeatSnapshotDirty)
        {
            _heartbeatUnits.Clear();
            foreach (WorldObject obj in _objects.Values)
            {
                HeartbeatSnapshotObjectsVisited++;
                if (obj is Unit unit) _heartbeatUnits.Add(unit);
            }
            _heartbeatSnapshotDirty = false;
        }
        foreach (Unit unit in _heartbeatUnits)
        {
            if (ReferenceEquals(unit.Map, this) && unit.IsInWorld
                && unit is not Player { IsQuestSettlementPending: true })
            {
                // Players (and so bots) are timed one by one for the slow-map top-3; creatures, the bulk, are the remainder of the phase.
                if (diagnostics is not null && unit is Player)
                {
                    long beatStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    unit.UpdateHeartbeat(diffMs);
                    diagnostics.AddPlayerHeartbeat(System.Diagnostics.Stopwatch.GetTimestamp() - beatStart);
                }
                else
                {
                    unit.UpdateHeartbeat(diffMs);
                }
            }
        }
    }

    /// <summary>Stages (2)-(6): visibility, values, flush, grid lifecycle, deferred work.</summary>
    private void UpdateOutput(uint diffMs, MapUpdateDiagnostics? diagnostics, GridLifecycleCounters gridsBefore)
    {
        if (diagnostics is not null)
        {
            diagnostics.Players = _players.Count;
            diagnostics.MovedObjects = _movedObjects.Count;
            diagnostics.NewObjects = _newObjects.Count;
            diagnostics.EndSimulation();
        }

        WorldRuntime.SerialScope gate = Gate(MapStage.Output);
        _inUpdatePhase = true;
        try
        {
            // Objects whose fields changed this tick may have a new bounding radius; the
            // distance queries below must cover it.
            foreach (WorldObject obj in _valuesQueue)
            {
                try
                {
                    _grid.TrackRadius(obj);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("map.visibility", ex);
                    _logger.LogFault("map.visibility", ex,
                        "tracking bounding radius failed for {Guid} (type {Type}) on map {MapId} instance {InstanceId} at ({X:F1},{Y:F1},{Z:F1})",
                        obj.Guid, obj.GetType().Name, MapId, InstanceId, obj.X, obj.Y, obj.Z);
                }
            }

            // (2) visibility: players that moved, then other objects that moved or appeared
            long candidatesBefore = PlayerVisibilityCandidates + ObjectVisibilityCandidates;
            foreach (Player player in _players.Values)
            {
                if (player.NeedsVisibilityUpdate)
                {
                    try
                    {
                        UpdateVisibility(player);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        FaultLedger.Shared.Record("map.visibility", ex);
                        _logger.LogFault("map.visibility", ex,
                            "visibility update failed for player {Player} ({Guid}) on map {MapId} instance {InstanceId} at ({X:F1},{Y:F1},{Z:F1})",
                            player.Name, player.Guid, MapId, InstanceId, player.X, player.Y, player.Z);
                    }
                    finally
                    {
                        player.NeedsVisibilityUpdate = false;
                    }
                }
            }

            if (_movedObjects.Count > 0)
            {
                // A reused snapshot in join order (MapSequence is unique, so the order is the one OrderBy gave).
                _movedScratch.AddRange(_movedObjects);
                _movedScratch.Sort(static (a, b) => a.MapSequence.CompareTo(b.MapSequence));
                try
                {
                    foreach (WorldObject obj in _movedScratch)
                    {
                        if (ReferenceEquals(obj.Map, this))
                        {
                            try
                            {
                                UpdateObjectVisibility(obj);
                            }
                            catch (Exception ex) when (ex is not OutOfMemoryException)
                            {
                                FaultLedger.Shared.Record("map.visibility", ex);
                                _logger.LogFault("map.visibility", ex,
                                    "object visibility update failed for {Guid} (entry {Entry}, type {Type}) on map {MapId} instance {InstanceId} at ({X:F1},{Y:F1},{Z:F1})",
                                    obj.Guid, obj.Guid.Entry, obj.GetType().Name, MapId, InstanceId, obj.X, obj.Y, obj.Z);
                            }
                        }
                    }
                }
                finally
                {
                    _movedScratch.Clear();
                }

                _movedObjects.Clear();
            }

            _newObjects.Clear();

            if (diagnostics is not null)
            {
                diagnostics.VisibilityCandidates = PlayerVisibilityCandidates + ObjectVisibilityCandidates - candidatesBefore;
                diagnostics.EndVisibility();
            }

            // (3) values updates
            if (diagnostics is not null)
            {
                diagnostics.ChangedObjects = _valuesQueue.Count;
            }
            foreach (WorldObject obj in _valuesQueue)
            {
                try
                {
                    SendValuesUpdate(obj);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("map.values", ex);
                    _logger.LogFault("map.values", ex,
                        "values update failed for {Guid} (type {Type}) on map {MapId} instance {InstanceId} at ({X:F1},{Y:F1},{Z:F1})",
                        obj.Guid, obj.GetType().Name, MapId, InstanceId, obj.X, obj.Y, obj.Z);
                }
                finally
                {
                    obj.ClearChangedFields();
                    obj.IsQueuedForUpdate = false;
                }
            }

            _valuesQueue.Clear();
            diagnostics?.EndValues();

            // (4) flush
            foreach (Player player in _players.Values)
            {
                try
                {
                    FlushPlayer(player);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("map.flush", ex);
                    _logger.LogFault("map.flush", ex,
                        "flushing pending updates failed for player {Player} ({Guid}) on map {MapId} instance {InstanceId}",
                        player.Name, player.Guid, MapId, InstanceId);
                }
            }
            diagnostics?.EndFlush();
        }
        finally
        {
            _inUpdatePhase = false;
            gate.Dispose();
        }

        // (5) grid lifecycle (vmangos Map::Update → grid states). The terrain clean-up is the world's, once per tick
        // (TerrainManager.Update): the terrain is shared by every instance of the map id.
        gate = Gate(MapStage.Grid);
        try
        {
            _grid.Update(diffMs);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("map.grid", ex);
            _logger.LogFault("map.grid", ex,
                "grid lifecycle update failed on map {MapId} instance {InstanceId} (diff={DiffMs} ms)",
                MapId, InstanceId, diffMs);
        }
        finally
        {
            gate.Dispose();
        }
        diagnostics?.EndGrid();

        // (6) work scheduled for after the update (vmangos MapManager::ScheduleFarTeleport)
        if (_afterUpdate.Count > 0)
        {
            using WorldRuntime.SerialScope deferredGate = Gate(MapStage.Deferred);
            // Work scheduled while this batch runs goes to the other list and runs after the next update, as before.
            List<Action> pending = _afterUpdate;
            _afterUpdate = _afterUpdateRunning;
            _afterUpdateRunning = pending;
            foreach (Action action in pending)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    FaultLedger.Shared.Record("map.deferred", ex);
                    _logger.LogFault("map.deferred", ex, "deferred work on map {MapId} failed", MapId);
                }
            }

            pending.Clear();
        }

        if (diagnostics is not null)
        {
            diagnostics.EndDeferred();
            diagnostics.Grids = _grid.Counters - gridsBefore;
            diagnostics.Complete();
        }
    }

    internal void QueueValuesUpdate(WorldObject obj)
    {
        if (_world.IsParallelPhase && !ReferenceEquals(TickOwner.CurrentMap, this))
        {
            _world.CheckOwner(this, "values update");
        }

        obj.IsQueuedForUpdate = true;
        _valuesQueue.Add(obj);
    }

    /// <summary>
    /// Raised when a unit's position changed or it joined this map (vmangos <c>Unit::OnRelocated</c>, called from Map.cpp:1407,
    /// 1473 and 1531). The creature AI schedules its proximity-aggro scan from it (docs/areas/creature-ai.md).
    /// </summary>
    internal event Action<WorldObject>? ObjectRelocated;

    /// <summary>Anomaly code: an object on this map has a NaN or infinite coordinate.</summary>
    public const string NonFinitePositionCode = "non-finite-position";

    private HashSet<ObjectGuid>? _nonFiniteReported;

    /// <summary>
    /// Report an object that sits at a non-finite position, once per episode (until it is seen finite again). Free when the position is
    /// finite and nothing was reported: three comparisons.
    /// </summary>
    private void CheckFinitePosition(WorldObject obj)
    {
        if (float.IsFinite(obj.X) && float.IsFinite(obj.Y) && float.IsFinite(obj.Z))
        {
            if (_nonFiniteReported is { Count: > 0 })
            {
                _nonFiniteReported.Remove(obj.Guid);
            }

            return;
        }

        _nonFiniteReported ??= [];
        if (_nonFiniteReported.Count < 256 && _nonFiniteReported.Add(obj.Guid))
        {
            AnomalyLedger.Shared.Report("map", NonFinitePositionCode, $"{obj.GetType().Name} {obj.Guid.Value:X} entry {obj.Guid.Entry}",
                $"object is on map {MapId} instance {InstanceId} at a non-finite position ({obj.X},{obj.Y},{obj.Z})", AnomalySeverity.Error);
        }
    }

    /// <summary>Re-file an object whose X/Y changed; non-player objects get a visibility pass this tick.</summary>
    internal void OnObjectMoved(WorldObject obj)
    {
        if (_world.IsParallelPhase)
        {
            if (!ReferenceEquals(TickOwner.CurrentMap, this))
            {
                _world.CheckOwner(this, "object relocation");
            }

            // A move into another cell can create or load grids (GridLoaded/GridApproaching: spawns, terrain and navmesh tiles, which
            // are shared): that one takes the serial gate when the caller runs outside it (an ungated creature spline pass).
            if (!_world.HoldsSerialGate && _grid.RelocateTouchesGridLifecycle(obj))
            {
                using WorldRuntime.SerialScope serial = _world.EnterSerial();
                OnObjectMovedCore(obj);
                return;
            }
        }

        OnObjectMovedCore(obj);
    }

    private void OnObjectMovedCore(WorldObject obj)
    {
        CheckFinitePosition(obj);
        if (!_grid.Relocate(obj) && !_grid.Contains(obj))
        {
            return;
        }

        // vmangos Unit::OnRelocated: the AI notify is scheduled on every relocation, the visibility pass only past the
        // relocation lower limit (World:Maps:VisibilityRelocationLowerLimit). It is a Unit rule (OnRelocated and
        // m_lastNotifiedPosition are Unit members, Unit.cpp:10151): a game object (a transport, an elevator) keeps a pass on every move.
        ObjectRelocated?.Invoke(obj);

        float limit = _grid.Options.VisibilityRelocationLowerLimit;
        if (obj is Unit && !obj.TakeRelocationNotify(limit > 0 ? limit * limit : 0f))
        {
            RelocationsWithoutVisibility++;
            return;
        }

        if (obj is Player player)
        {
            player.NeedsVisibilityUpdate = true;
        }
        else
        {
            _movedObjects.Add(obj);
        }

        // A player whose camera looks from this unit (possession, Eyes of the Beast) sees from its new place (vmangos Camera::Event_Moved).
        if (UnitControl.ViewerOf(obj) is { } viewer && !ReferenceEquals(viewer, obj))
        {
            viewer.NeedsVisibilityUpdate = true;
        }
    }

    /// <summary>
    /// Whether this map updates creatures only in the active cells (<see cref="MapOptions.UpdateActiveCellsOnly"/>). vmangos uses
    /// the active cells on every map (Map::Update → UpdateCells); this server limits it to continents as a deliberate deviation
    /// (a map without a loaded template counts as one when it is 0 or 1): vmangos activates 170 yards in instances
    /// (Visibility.Distance.Instances) and 180 in battlegrounds, where ArcaneCore has only the one 100-yard
    /// <see cref="MapOptions.GridActivationDistance"/>, so instances and battlegrounds keep updating every creature of their loaded
    /// grids rather than freezing scripted encounters at a radius vmangos does not use there.
    /// </summary>
    internal bool UpdatesActiveCellsOnly
        => _grid.Options.UpdateActiveCellsOnly && (Template is { } template ? !template.Instanceable : MapId is 0 or 1);

    /// <summary>Moves that stayed within the relocation lower limit and so scheduled no visibility pass (diagnostics, tests).</summary>
    public long RelocationsWithoutVisibility { get; private set; }

    private void ProcessPackets(Player player)
    {
        try
        {
            player.Session.ProcessWorldPackets(player);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("map.packet-handling", ex);
            _logger.LogFault("packet.handling", ex,
                "packet handling failed for {Player} ({Guid}) on map {MapId} instance {InstanceId} at ({X:F1},{Y:F1},{Z:F1}); disconnecting",
                player.Name, player.Guid, MapId, InstanceId, player.X, player.Y, player.Z);
            player.Session.Kick();
        }
    }

    private void SendValuesUpdate(WorldObject obj)
    {
        // An item's fields go to its owner only (vmangos Item::BuildUpdateData).
        if (obj is Items.Item item)
        {
            if (item.Inventory?.Player is { } owner && ReferenceEquals(owner.Map, this))
            {
                AppendValues(obj, owner);
            }

            return;
        }

        if (obj is Player self && ReferenceEquals(self.Map, this))
        {
            self.Combat.ResurrectedUnflushed = false;
            AppendValues(obj, self);
        }

        if (_observers.TryGetValue(obj.Guid, out HashSet<Player>? observers))
        {
            foreach (Player viewer in observers)
            {
                if (!ReferenceEquals(viewer, obj))
                {
                    AppendValues(obj, viewer);
                }
            }
        }
    }

    /// <summary>
    /// Flush any pending values update of <paramref name="player"/> (and its pending update buffer) at once,
    /// before a same-tick state reversal (such as dying on the same tick as a resurrection) can overwrite
    /// the intermediate fields in <c>_valuesQueue</c>.
    /// </summary>
    internal void FlushPlayerValuesNow(Player player)
    {
        if (!ReferenceEquals(player.Map, this))
        {
            return;
        }

        if (player.IsQueuedForUpdate)
        {
            _grid.TrackRadius(player);
            SendValuesUpdate(player);
            player.ClearChangedFields();
            player.IsQueuedForUpdate = false;
            _valuesQueue.Remove(player);
        }

        FlushPlayer(player);
        if (_observers.TryGetValue(player.Guid, out HashSet<Player>? observers))
        {
            foreach (Player viewer in observers)
            {
                if (!ReferenceEquals(viewer, player))
                {
                    FlushPlayer(viewer);
                }
            }
        }
    }

    private static void AppendValues(WorldObject obj, Player viewer)
    {
        if (!viewer.Session.WantsUpdateObjects)
        {
            BotLeanStats.SkippedUpdateBlock();
            return;
        }

        PacketWriter block = viewer.PendingUpdates.BeginBlock();
        int before = block.Length;
        if (UpdateBlockWriter.TryWriteValuesBlock(block, obj, viewer))
        {
            viewer.PendingUpdates.EndBlock();
        }
        else if (block.Length == before)
        {
            viewer.PendingUpdates.CancelBlock();
        }
    }

    private void FlushPlayer(Player player)
        => player.PendingUpdates.FlushTo(player.PendingUpdatesSend, _world.Options.UpdateCompressionThreshold);
}
