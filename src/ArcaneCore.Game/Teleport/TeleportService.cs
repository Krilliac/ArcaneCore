using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Teleport;

/// <summary>A teleport destination (vmangos <c>WorldLocation m_teleportDest</c>).</summary>
public readonly record struct TeleportDestination(uint MapId, float X, float Y, float Z, float Orientation);

/// <summary>The stage of a pending teleport.</summary>
public enum TeleportStage
{
    /// <summary>Same-map teleport: MSG_MOVE_TELEPORT_ACK sent, waiting for the client's ack (vmangos <c>mSemaphoreTeleport_Near</c>).</summary>
    Near,

    /// <summary>Different-map teleport scheduled for the end of the current map update (vmangos <c>ScheduleFarTeleport</c>).</summary>
    FarScheduled,

    /// <summary>SMSG_NEW_WORLD sent, the player is in no map, waiting for MSG_MOVE_WORLDPORT_ACK (vmangos <c>mSemaphoreTeleport_Far</c>).</summary>
    Far,

    /// <summary>MSG_MOVE_WORLDPORT_ACK received; the player enters the new map at the start of the next tick.</summary>
    Arriving,
}

/// <summary>
/// Teleports (vmangos <c>Player::TeleportTo</c>, <c>ExecuteTeleportFar</c>,
/// <c>ExecuteTeleportNear</c>, <c>WorldSession::HandleMoveWorldportAckOpcode</c>).
/// <para>
/// A same-map teleport sends MSG_MOVE_TELEPORT_ACK and moves the player when the client acks.
/// A different-map teleport, after the current map update, clears the selection, sends
/// SMSG_TRANSFER_PENDING, takes the player out of its map and sends SMSG_NEW_WORLD; the
/// client's MSG_MOVE_WORLDPORT_ACK puts it into the new map with the login packets (the world
/// daemon supplies them through the two callbacks).
/// </para>
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed partial class TeleportService
{
    private readonly WorldRuntime _world;
    private readonly Action<Player> _beforeAddToMap;
    private readonly Action<Player> _afterAddToMap;
    private readonly ILogger _logger;
    private readonly Dictionary<ObjectGuid, Pending> _pending = [];
    private readonly List<Pending> _stallScratch = [];
    private readonly List<Player> _observerScratch = [];
    private bool _observerScratchInUse;

    /// <param name="world">The world whose players this service teleports.</param>
    /// <param name="beforeAddToMap">Sends the packets that precede the player's create block on a new map (vmangos SendInitialPacketsBeforeAddToMap).</param>
    /// <param name="afterAddToMap">Sends the packets that follow it (vmangos SendInitialPacketsAfterAddToMap).</param>
    /// <param name="logger">Diagnostics.</param>
    public TeleportService(WorldRuntime world, Action<Player> beforeAddToMap, Action<Player> afterAddToMap, ILogger? logger = null)
    {
        _world = world;
        _beforeAddToMap = beforeAddToMap;
        _afterAddToMap = afterAddToMap;
        _logger = logger ?? NullLogger.Instance;
        _world.WorldTick += OnWorldTick;
    }

    private void OnWorldTick(uint diffMs)
    {
        _sinceStallSweepMs += diffMs;
        if (_sinceStallSweepMs < StallSweepIntervalMs)
        {
            return;
        }

        _sinceStallSweepMs = 0;
        CheckStalled(_world.NowMs);
    }

    /// <summary>
    /// H1 outcome check: a teleport that sat in one stage past its budget never completed (the client's MSG_MOVE_TELEPORT_ACK or
    /// MSG_MOVE_WORLDPORT_ACK did not arrive, or the scheduled far teleport never ran). One report per teleport
    /// (<c>teleport/ack-timeout</c>, <c>teleport/far-stuck</c>); the entry stays so a late ack still completes, and if it remains
    /// unacknowledged past the post-report recovery grace, Near is re-acknowledged and FarScheduled is cleared. Far and Arriving stalls
    /// are report-only: the anomaly is reported but no recovery runs, so the entry waits for a late ack or for <c>IsOrphaned</c>
    /// (the player disconnects or reaches a map). A playerbot
    /// whose brain has not acknowledged a same-map teleport within <see cref="BotNearAckFallbackMs"/> completes it on the sweep.
    /// Runs once a second from the world tick; cost is a walk over the pending teleports (normally none). World thread.
    /// </summary>
    /// <param name="nowMs">The world clock (<see cref="WorldRuntime.NowMs"/>).</param>
    public void CheckStalled(uint nowMs)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        _stallScratch.Clear();
        foreach ((_, Pending pending) in _pending)
        {
            _stallScratch.Add(pending);
        }

        for (int i = 0; i < _stallScratch.Count; i++)
        {
            Pending pending = _stallScratch[i];
            Player player = pending.Player;
            ObjectGuid guid = player.Guid;
            if (!_pending.TryGetValue(guid, out Pending? current) || !ReferenceEquals(current, pending))
            {
                continue;
            }

            if (IsOrphaned(pending))
            {
                _pending.Remove(guid);
                pending.SourceMap.EndTransit(player);
                continue;
            }

            uint stageMs = unchecked(nowMs - pending.StageSinceMs);
            if (stageMs > int.MaxValue)
            {
                continue;
            }

            if (pending.Stage == TeleportStage.Near && player.Session.IsPlayerbot && stageMs >= BotNearAckFallbackMs)
            {
                HandleTeleportAck(player, guid.Value);
                continue;
            }

            if (pending.StallReported)
            {
                uint sinceReportMs = unchecked(nowMs - pending.StallReportedAtMs);
                if (sinceReportMs <= int.MaxValue)
                {
                    if (pending.Stage == TeleportStage.Near && sinceReportMs >= NearAckRecoveryGraceMs)
                    {
                        HandleTeleportAck(player, guid.Value);
                    }
                    else if (pending.Stage == TeleportStage.FarScheduled && sinceReportMs >= ScheduledRecoveryGraceMs)
                    {
                        _pending.Remove(guid);
                        pending.SourceMap.EndTransit(player);
                    }
                }

                continue;
            }

            uint budget = pending.Stage switch
            {
                TeleportStage.Near => NearAckBudgetMs,
                TeleportStage.Far => FarAckBudgetMs,
                _ => ScheduledBudgetMs,
            };
            if (stageMs < budget)
            {
                continue;
            }

            pending.StallReported = true;
            pending.StallReportedAtMs = nowMs;
            Player? online = _world.FindOnlinePlayer(guid) ?? (ReferenceEquals(player.Map, pending.SourceMap) ? player : null);
            string detail = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"teleport stuck in stage {pending.Stage} for {stageMs} ms (budget {budget} ms) from map {pending.SourceMap.MapId} to map {pending.Destination.MapId} at {pending.Destination.X:F1},{pending.Destination.Y:F1},{pending.Destination.Z:F1}");
            string code = pending.Stage is TeleportStage.Near or TeleportStage.Far ? ServerAnomalies.AckTimeout : ServerAnomalies.FarStuck;
            if (online is not null)
            {
                ServerAnomalies.Report(online, ServerAnomalies.Teleport, code, detail);
            }
            else
            {
                ArcaneCore.Kernel.Diagnostics.AnomalyLedger.Shared.Report(
                    ServerAnomalies.Teleport, code, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"player guid {guid.Value:X}"), detail);
            }
        }

        _stallScratch.Clear();
    }

    private bool IsOrphaned(Pending pending)
    {
        Player player = pending.Player;
        return pending.Stage switch
        {
            TeleportStage.Near or TeleportStage.FarScheduled => !ReferenceEquals(player.Map, pending.SourceMap),
            TeleportStage.Far or TeleportStage.Arriving => player.Map is not null
                || (!pending.SourceMap.IsInTransit(player) && _world.FindOnlinePlayer(player.Guid) is null),
            _ => false,
        };
    }

    /// <summary>Longest a same-map teleport may wait for the client's ack before it is reported (client ack is one round trip).</summary>
    public const uint NearAckBudgetMs = 30_000;

    /// <summary>Longest SMSG_NEW_WORLD may wait for the worldport ack (a loading screen; slow disks take tens of seconds).</summary>
    public const uint FarAckBudgetMs = 90_000;

    /// <summary>Longest a scheduled or arriving far teleport may take (both finish within a tick or two).</summary>
    public const uint ScheduledBudgetMs = 10_000;

    /// <summary>Fallback window after which a think-starved playerbot's pending same-map teleport is acknowledged by the stall sweep.</summary>
    public const uint BotNearAckFallbackMs = 2_000;

    /// <summary>Grace window after <c>teleport/ack-timeout</c> before an unacknowledged near teleport is enforced on a subsequent sweep.</summary>
    public const uint NearAckRecoveryGraceMs = 90_000;

    /// <summary>Grace window after <c>teleport/far-stuck</c> before an unexecuted scheduled far teleport is cleared on a subsequent sweep.</summary>
    public const uint ScheduledRecoveryGraceMs = 30_000;

    private const uint StallSweepIntervalMs = 1_000;
    private uint _sinceStallSweepMs;

    /// <summary>
    /// Raised on the world thread when a teleport of a player has finished: after a same-map teleport was acknowledged, or after the
    /// player entered the new map (vmangos runs delayed operations such as DELAYED_RESURRECT_PLAYER at that point).
    /// </summary>
    public event Action<Player>? TeleportCompleted;

    /// <summary>
    /// Raised on the world thread when a same-map teleport starts, before MSG_MOVE_TELEPORT_ACK goes out (vmangos Player::TeleportTo near
    /// branch: a pet farther from the destination than the grid activation distance is unsummoned temporarily, Player.cpp:1911-1921).
    /// </summary>
    public event Action<Player, TeleportDestination>? NearTeleportStarting;

    /// <summary>
    /// Raised on the world thread after <see cref="TeleportCompleted"/> when the teleport scheduled vmangos' DELAYED_CAST_HONORLESS_TARGET:
    /// every far teleport (Player::ExecuteTeleportFar, Player.cpp:2083) and a same-map teleport without
    /// <see cref="TeleportOptions.NotLeaveCombat"/> (Player.cpp:1923-1927). The flag says which; a far arrival casts only into a PvP-enforced
    /// area (HandleMoveWorldportAckOpcode, MovementHandler.cpp:193-195), which the listener decides.
    /// </summary>
    public event Action<Player, bool>? HonorlessTargetDue;

    /// <summary>
    /// Raised on the world thread when a far teleport is carried out, while the player is still in its old map (vmangos
    /// Player::ExecuteTeleportFar: "remove pet on map change", UnsummonPetTemporaryIfAny, Player.cpp:2045-2048, before the old map's
    /// Remove).
    /// </summary>
    public event Action<Player>? FarTeleportExecuting;

    /// <summary>
    /// Raised on the world thread when a far teleport reaches <see cref="TeleportStage.Far"/> (SMSG_TRANSFER_PENDING and SMSG_NEW_WORLD
    /// were just sent) and <see cref="TeleportStage.Arriving"/> (MSG_MOVE_WORLDPORT_ACK accepted). Diagnostics only: the same-map
    /// world-port spike times the steps with it (docs/design/hot-reload-tiers.md section 5.5).
    /// </summary>
    public event Action<Player, TeleportStage>? FarStageReached;

    /// <summary>Number of players with a teleport in progress.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>vmangos <c>Player::IsBeingTeleported</c>.</summary>
    public bool IsBeingTeleported(Player player) => _pending.ContainsKey(player.Guid);

    /// <summary>vmangos <c>Player::IsBeingTeleportedNear</c>.</summary>
    public bool IsBeingTeleportedNear(Player player) => StageOf(player) == TeleportStage.Near;

    /// <summary>vmangos <c>Player::IsBeingTeleportedFar</c> (scheduled, sent or arriving).</summary>
    public bool IsBeingTeleportedFar(Player player) => StageOf(player) is TeleportStage.FarScheduled or TeleportStage.Far or TeleportStage.Arriving;

    /// <summary>The stage of the player's teleport, or null when none is in progress.</summary>
    public TeleportStage? StageOf(Player player) => _pending.TryGetValue(player.Guid, out Pending? p) ? p.Stage : null;

    /// <summary>The destination of the player's teleport (vmangos <c>GetTeleportDest</c>).</summary>
    public TeleportDestination? DestinationOf(Player player) => _pending.TryGetValue(player.Guid, out Pending? p) ? p.Destination : null;

    /// <summary>
    /// Whether <see cref="TeleportTo"/> would start this teleport right now: the same checks, in the
    /// same order, with nothing changed (quest reward preflight). A later call may still be refused
    /// because the player or the target instance changed in between.
    /// </summary>
    public bool CanTeleportTo(Player player, uint mapId, float x, float y, float z, float orientation)
        => Check(player, mapId, x, y, z, orientation, log: false);

    private bool Check(Player player, uint mapId, float x, float y, float z, float orientation, bool log)
    {
        if (player.IsQuestSettlementPending)
        {
            return false;
        }

        if (!GridDefines.IsValidMapCoord(x, y, z, orientation))
        {
            if (log)
            {
                _logger.LogWarning("teleport of {Player} to invalid coordinates {X} {Y} {Z} {O} on map {MapId}", player.Name, x, y, z, orientation, mapId);
                ServerAnomalies.Report(
                    player,
                    ServerAnomalies.Teleport,
                    "invalid-destination",
                    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"teleport refused: invalid destination {x} {y} {z} {orientation} on map {mapId}"),
                    float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) && float.IsFinite(orientation) ? AnomalySeverity.Warning : AnomalySeverity.Error);
            }

            return false;
        }

        WorldMaps maps = WorldMaps.Of(_world);
        MapTemplate? target = maps.Registry.Find(mapId);
        if (target is null)
        {
            if (log)
            {
                _logger.LogWarning("teleport of {Player} to unknown map {MapId}", player.Name, mapId);
                ServerAnomalies.Report(
                    player,
                    ServerAnomalies.Teleport,
                    "map-not-loaded",
                    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"teleport refused: map {mapId} is not in the map registry (destination {x:F1},{y:F1},{z:F1})"));
            }

            return false;
        }

        // Player::TeleportTo (Player.cpp:1861-1864): "don't let enter battlegrounds without assigned battleground id" — only the
        // battleground system, through BattlegroundEntryAllowed, lets a player onto a battleground map.
        if (target.IsBattleground && !(BattlegroundEntryAllowed?.Invoke(player, mapId) ?? false))
        {
            return false;
        }

        Map? current = player.Map;
        if (current is null || IsBeingTeleportedFar(player))
        {
            return false;
        }

        // vmangos TeleportTo → MapManager::CanPlayerEnter: the instance rules may refuse a far
        // teleport (raid group, full or resetting instance) before anything changes.
        bool allowed = current.MapId == mapId || _world.MapResolver is not { } resolver || resolver.CanEnter(player, mapId);
        if (!allowed && log)
        {
            ServerAnomalies.Report(
                player,
                ServerAnomalies.Teleport,
                "entry-refused",
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"teleport to map {mapId} refused by the instance rules (no instance for the player)"),
                AnomalySeverity.Info);
        }

        return allowed;
    }

    /// <summary>
    /// Whether a player may be teleported onto a battleground map: the battleground feature answers true for the map of the match the player
    /// is bound to (vmangos <c>InBattleGround()</c>). Null (no battleground system) refuses every battleground map. World thread.
    /// </summary>
    public Func<Player, uint, bool>? BattlegroundEntryAllowed { get; set; }

    /// <summary>
    /// Start a teleport (vmangos <c>Player::TeleportTo</c>). Fails — returns false and changes
    /// nothing — for invalid coordinates (<c>MapManager::IsValidMapCoord</c>), a map that is not
    /// in the registry, a battleground map the player is not bound to (<see cref="BattlegroundEntryAllowed"/>),
    /// a player in no map, or a far teleport already under way.
    /// </summary>
    public bool TeleportTo(Player player, uint mapId, float x, float y, float z, float orientation)
        => TeleportTo(player, mapId, x, y, z, orientation, TeleportOptions.None);

    /// <summary>
    /// <see cref="TeleportTo(Player, uint, float, float, float, float)"/> with vmangos <c>TeleportToOptions</c>. With
    /// <see cref="TeleportOptions.NotLeaveTransport"/> a passenger stays on its ship: the teleport is always a far one
    /// (vmangos only teleports near without a transport, Player.cpp:1896), SMSG_TRANSFER_PENDING names the ship and the
    /// old map, and SMSG_NEW_WORLD carries the offset on the ship instead of the world position (Player.cpp:2068-2072, 2113-2118).
    /// </summary>
    public bool TeleportTo(Player player, uint mapId, float x, float y, float z, float orientation, TeleportOptions options)
    {
        if (!Check(player, mapId, x, y, z, orientation, log: true) || player.Map is not { } current)
        {
            return false;
        }

        // vmangos revives a ghost that enters the map its corpse is in (Player.cpp:1953-1966; there before the entry check, here once it passed).
        current.Combat.ReviveForDungeonEntry(player, mapId);
        WorldMaps maps = WorldMaps.Of(_world);

        // vmangos TeleportTo (Player.cpp:1868-1872): leave the ship unless told to stay on it.
        bool staysAboard = (options & TeleportOptions.NotLeaveTransport) != 0 && player.Transport is not null;
        if (!staysAboard)
        {
            player.Transport?.RemovePassenger(player);
        }

        // vmangos TeleportTo: reset the client time stamp and stop movement, leave any transport.
        ResetMovementForTeleport(player, staysAboard);

        var destination = new TeleportDestination(mapId, x, y, z, orientation);

        // The grids the player will load on arrival (Map.AddPlayer, or the relocation of a near teleport's ack) are read on the thread pool
        // from now, while the transfer screen or the ack round trip runs, instead of inside the tick that places the player. Timing only.
        maps.PrefetchArrival(mapId, x, y);
        if (current.MapId == mapId && !staysAboard && (options & TeleportOptions.ForceFar) == 0)
        {
            bool leavesCombat = (options & TeleportOptions.NotLeaveCombat) == 0;
            bool preserveHonorless = _pending.TryGetValue(player.Guid, out Pending? existing)
                && existing.Stage == TeleportStage.Near
                && existing.HonorlessTarget;
            _pending[player.Guid] = new Pending(this, player, destination, TeleportStage.Near, current, default)
            {
                HonorlessTarget = leavesCombat || preserveHonorless,
                StartedMs = _world.NowMs,
                StageSinceMs = _world.NowMs,
            };
            if (leavesCombat)
            {
                // vmangos Player::TeleportTo near branch (Player.cpp:1923-1927): CombatStop() unless TELE_TO_NOT_LEAVE_COMBAT,
                // plus HostileRefManager::deleteReferences so creatures from the old location do not keep the teleported player
                // (or its pet) pinned in combat via ThreatenedBy.
                current.Combat.CombatStop(player);
                HostileRefs.DeleteReferences(player);
                if (player.PetGuid is { IsEmpty: false } petGuid && current.Combat.FindUnit(petGuid) is { } pet)
                {
                    current.Combat.CombatStop(pet);
                    HostileRefs.DeleteReferences(pet);
                }
            }

            NearTeleportStarting?.Invoke(player, destination);
            MovementInfo moved = player.Movement;
            moved.X = x;
            moved.Y = y;
            moved.Z = z;
            moved.Orientation = orientation;
            moved.Time = _world.NowMs;
            player.Session.Send(
                WorldOpcode.MsgMoveTeleportAck,
                TeleportPackets.BuildMoveTeleportAck(player.Guid, player.NextMovementCounter(), moved));
            return true;
        }

        var pending = new Pending(this, player, destination, TeleportStage.FarScheduled, current, new TeleportDestination(player.MapId, player.X, player.Y, player.Z, player.Orientation))
        {
            StartedMs = _world.NowMs,
            StageSinceMs = _world.NowMs,
        };
        _pending[player.Guid] = pending;
        current.RunAfterUpdate(pending.ExecuteFar);
        return true;
    }

    /// <summary>Teleport the player to its hearthstone bind point (vmangos <c>Player::TeleportToHomebind</c>).</summary>
    public bool TeleportToHomebind(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        Combat.MapCombat.UpdatePvpContested(player, false, overriding: true); // vmangos Player.cpp:21048
        return TeleportTo(player, player.Home.MapId, player.Home.X, player.Home.Y, player.Home.Z, player.Orientation);
    }

    /// <summary>
    /// The client acknowledged a same-map teleport (vmangos <c>HandleMoveTeleportAckOpcode</c> →
    /// <c>ExecuteTeleportNear</c>). Ignored unless a near teleport is pending and the GUID is
    /// the player's. Observers around the old and the new position get MSG_MOVE_TELEPORT.
    /// </summary>
    public bool HandleTeleportAck(Player player, ulong moverGuid)
    {
        if (player.IsQuestSettlementPending)
        {
            return false;
        }

        if (!_pending.TryGetValue(player.Guid, out Pending? pending) || pending.Stage != TeleportStage.Near)
        {
            return false;
        }

        if (moverGuid != player.Guid.Value)
        {
            return false;
        }

        _pending.Remove(player.Guid);
        uint elapsedNearMs = _world.NowMs - pending.StartedMs;
        if (player.Session.WantsUpdateObjects && elapsedNearMs >= SlowTeleportAckThresholdMs)
        {
            _logger.LogWarning(
                "slow near-teleport ack from {Player} (guid={Guid}) on map {MapId}: {ElapsedMs} ms to ({X:F1}, {Y:F1}, {Z:F1})",
                player.Name, player.Guid.Low, pending.Destination.MapId, elapsedNearMs, pending.Destination.X, pending.Destination.Y, pending.Destination.Z);
        }

        Map? map = player.Map;
        if (map is null || !ReferenceEquals(map, pending.SourceMap))
        {
            return false;
        }

        TeleportDestination dest = pending.Destination;
        byte[]? packet = null;
        SendTeleportToObservers(map, player, dest, ref packet);
        ArriveNear(player, dest);
        VerifyLanding(player, dest, map.MapId);
        SendTeleportToObservers(map, player, dest, ref packet);
        UpdateZone(map, player);

        // vmangos TeleportPositionRelocation runs the zone (or area) update at once; the 1 s zone timer would leave the world states
        // and the zone listeners a second behind. ZoneAreaUpdater runs every listener and then rethrows the first failure (inside
        // Map.Update a throwing updater is logged); here that failure is logged the same way and the arrival still completes, so
        // one failing listener cannot leave the player without its visibility pass and the completion event.
        try
        {
            map.FindUpdater<ZoneAreaUpdater>()?.OnRelocated(player);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Record("teleport.zone-update", ex);
            _logger.LogError(ex, "map {MapId} zone update of {Player} after a near teleport failed", map.MapId, player.Name);
        }

        player.NeedsVisibilityUpdate = true;
        TeleportCompleted?.Invoke(player);
        if (pending.HonorlessTarget)
        {
            HonorlessTargetDue?.Invoke(player, false);
        }

        return true;
    }

    /// <summary>
    /// The client finished loading the new map (vmangos <c>HandleMoveWorldportAckOpcode</c>).
    /// Ignored unless SMSG_NEW_WORLD was sent; the player enters the new map at the start of
    /// the next world tick (maps cannot be created while the world iterates them).
    /// </summary>
    public bool HandleWorldportAck(Player player)
    {
        if (player.IsQuestSettlementPending)
        {
            return false;
        }

        if (!_pending.TryGetValue(player.Guid, out Pending? pending) || pending.Stage != TeleportStage.Far)
        {
            return false;
        }

        uint elapsedFarMs = _world.NowMs - pending.StartedMs;
        if (player.Session.WantsUpdateObjects && elapsedFarMs >= SlowTeleportAckThresholdMs)
        {
            _logger.LogWarning(
                "slow far-teleport worldport ack from {Player} (guid={Guid}) from map {SourceMapId} to map {DestMapId}: {ElapsedMs} ms",
                player.Name, player.Guid.Low, pending.SourceMap.MapId, pending.Destination.MapId, elapsedFarMs);
        }

        pending.Stage = TeleportStage.Arriving;
        pending.StageSinceMs = _world.NowMs;
        pending.StallReported = false;
        _world.Post(pending.CompleteFar);
        FarStageReached?.Invoke(player, TeleportStage.Arriving);
        return true;
    }

    /// <summary>Forget the player's teleport (logout or disconnect).</summary>
    public void Forget(Player player)
    {
        if (_pending.Remove(player.Guid, out Pending? pending))
        {
            if (player.Session.WantsUpdateObjects)
            {
                uint elapsedMs = _world.NowMs - pending.StartedMs;
                _logger.LogWarning(
                    "player {Player} (guid={Guid}, deathState={DeathState}) left the world while teleport in stage {Stage} from map {SourceMapId} to map {DestMapId} ({X:F1}, {Y:F1}, {Z:F1}) after {ElapsedMs} ms",
                    player.Name, player.Guid.Low, player.Combat.DeathState, pending.Stage, pending.SourceMap.MapId,
                    pending.Destination.MapId, pending.Destination.X, pending.Destination.Y, pending.Destination.Z, elapsedMs);
            }

            pending.SourceMap.EndTransit(player);
        }
    }

    // vmangos Player::ExecuteTeleportFar (run after the source map's update).
    private void ExecuteTeleportFar(Player player, Pending pending)
    {
        if (!_pending.TryGetValue(player.Guid, out Pending? current) || !ReferenceEquals(current, pending))
        {
            return; // logged out or superseded
        }

        if (player.IsQuestSettlementPending)
        {
            return;
        }

        Map source = pending.SourceMap;
        if (!ReferenceEquals(player.Map, source) || !ReferenceEquals(_world.FindOnlinePlayer(player.Guid), player))
        {
            _pending.Remove(player.Guid);
            return;
        }

        // vmangos Player::ExecuteTeleportFar (Player.cpp:2025): accepted far transfer clears
        // pending extra attacks before the player leaves the source map. Failed/superseded
        // transfers return above and preserve the queue.
        player.Combat.ResetExtraAttacks();
        FarTeleportExecuting?.Invoke(player);
        TeleportDestination dest = pending.Destination;
        player.Selection = ObjectGuid.Empty;
        Transports.ShipTransport? ship = player.Transport;
        player.Session.Send(WorldOpcode.SmsgTransferPending, ship is null
            ? TeleportPackets.BuildTransferPending(dest.MapId)
            : TeleportPackets.BuildTransferPending(dest.MapId, ship.Entry, source.MapId));
        source.RemovePlayer(player);
        source.BeginTransit(player);

        // The player is in no map now; a save while in transit stores the destination, as
        // vmangos SaveToDB does with m_teleportDest during a far teleport.
        MovementInfo aboard = player.Movement;
        player.MapId = dest.MapId;
        player.Relocate(dest.X, dest.Y, dest.Z, dest.Orientation, _world.NowMs);
        pending.Stage = TeleportStage.Far;
        pending.StartedMs = _world.NowMs;
        pending.StageSinceMs = pending.StartedMs;
        pending.StallReported = false;
        if (ship is not null)
        {
            // Still aboard: the movement block keeps the ship and the offset, and the client loads the new map at its place on
            // the ship (vmangos SendNewWorld: m_movementInfo.GetTransportPos() with the destination map).
            player.SetTransportData(ship.Guid, aboard.TransportX, aboard.TransportY, aboard.TransportZ, aboard.TransportOrientation);
            player.Session.Send(WorldOpcode.SmsgNewWorld, TeleportPackets.BuildNewWorld(
                dest.MapId, aboard.TransportX, aboard.TransportY, aboard.TransportZ, aboard.TransportOrientation));
            FarStageReached?.Invoke(player, TeleportStage.Far);
            return;
        }

        player.Session.Send(WorldOpcode.SmsgNewWorld, TeleportPackets.BuildNewWorld(dest.MapId, dest.X, dest.Y, dest.Z, dest.Orientation));
        FarStageReached?.Invoke(player, TeleportStage.Far);
    }

    // vmangos HandleMoveWorldportAckOpcode after the semaphore check.
    private void CompleteTeleportFar(Player player, Pending pending)
    {
        if (!_pending.TryGetValue(player.Guid, out Pending? current) || !ReferenceEquals(current, pending))
        {
            return;
        }

        if (player.IsQuestSettlementPending)
        {
            return;
        }

        _pending.Remove(player.Guid);
        pending.SourceMap.EndTransit(player);
        if (!ReferenceEquals(_world.FindOnlinePlayer(player.Guid), player) || player.Map is not null)
        {
            return;
        }

        TeleportDestination dest = pending.Destination;
        WorldMaps maps = WorldMaps.Of(_world);
        if (!maps.Registry.Contains(dest.MapId) || !GridDefines.IsValidMapCoord(dest.X, dest.Y, dest.Z, dest.Orientation))
        {
            // vmangos: an invalid destination sends the player to its bind point.
            _logger.LogError("{Player} far-teleported to invalid destination {Dest}; using the bind point", player.Name, dest);
            ServerAnomalies.Report(
                player,
                ServerAnomalies.Teleport,
                "destination-invalid",
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"far teleport destination map {dest.MapId} {dest.X} {dest.Y} {dest.Z} is invalid or not in the registry; sent to the bind point on map {player.Home.MapId}"));
            dest = new TeleportDestination(player.Home.MapId, player.Home.X, player.Home.Y, player.Home.Z, player.Orientation);
        }

        if (TryEnterMap(player, dest))
        {
            // ArcaneCore behaviour, not vmangos parity: vmangos never clears PLAYER_CHEAT_FLY / PLAYER_CHEAT_FIXED_Z on a teleport (see
            // MovementCheats.cs). A far transfer makes the client enter the world afresh (the point after Map::Add in vmangos
            // HandleMoveWorldportAckOpcode, MovementHandler.cpp:117-124), so a leftover cheat would only keep whitelisting the fly hack's
            // flags (MovementCheats.GrantedFor) for a player who is no longer flying. It is cleared on arrival. A far teleport that never
            // sends SMSG_NEW_WORLD (bailed out in ExecuteTeleportFar, or stalled while scheduled) keeps the cheats, because the client is
            // still flying on the source map and would otherwise be flagged. A map-entry failure (the return-to-origin branch below)
            // also keeps them: SMSG_NEW_WORLD was already sent there, but a stale cheat grants no immunity (MovementCheats.cs), so
            // clearing it would add a second behaviour for no gain.
            player.Cheats &= ~(PlayerCheats.Fly | PlayerCheats.FixedZ);
            VerifyLanding(player, dest, dest.MapId);
            HonorlessTargetDue?.Invoke(player, true);
        }
        else
        {
            ServerAnomalies.Report(
                player,
                ServerAnomalies.Teleport,
                "map-entry-failed",
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"could not enter map {dest.MapId} (not loaded, refused by the resolver or the player is already in it); returning to map {pending.Origin.MapId}"));

            // vmangos HandleReturnOnTeleportFail: back to where the teleport started.
            TeleportDestination origin = pending.Origin;
            if (!TryEnterMap(player, origin))
            {
                // vmangos HandleReturnOnTeleportFail (Player.cpp:2122-2135): "if (!TeleportTo(oldLoc)) TeleportToHomebind();". Neither the
                // destination nor the origin takes the player (the origin match ended while it loaded), so it goes to its bind point.
                // ArcaneCore deviations (docs/integration/gm-commands.md, deviation 7): it kicks when the bind point refuses too (vmangos
                // ignores the result), and the origin above is entered without a new loading screen. A bind point on a map id the client
                // did not load gets a fresh far teleport, as in vmangos; one on the loaded map id is entered directly. A teleport that is
                // itself this fallback is not retried (no loop).
                var home = new TeleportDestination(player.Home.MapId, player.Home.X, player.Home.Y, player.Home.Z, player.Orientation);
                if (!pending.IsHomebindFallback && maps.Registry.Contains(home.MapId) && GridDefines.IsValidMapCoord(home.X, home.Y, home.Z, home.Orientation))
                {
                    bool reissue = home.MapId != dest.MapId;
                    if (reissue ? ReissueFar(player, pending.SourceMap, home) : TryEnterMap(player, home))
                    {
                        _logger.LogWarning("{Player} could not enter map {MapId} nor return to map {Origin}; sent to the bind point on map {HomeMapId}", player.Name, dest.MapId, origin.MapId, home.MapId);
                        ServerAnomalies.Report(
                            player,
                            ServerAnomalies.Teleport,
                            "return-failed",
                            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"could not enter map {dest.MapId} nor return to map {origin.MapId}; sent to the bind point on map {home.MapId}"),
                            AnomalySeverity.Warning);
                        return;
                    }
                }

                _logger.LogError("{Player} could not enter map {MapId} nor return to map {Origin} nor reach the bind point; disconnecting", player.Name, dest.MapId, origin.MapId);
                ServerAnomalies.Report(
                    player,
                    ServerAnomalies.Teleport,
                    "return-failed",
                    string.Create(System.Globalization.CultureInfo.InvariantCulture, $"could not enter map {dest.MapId} nor return to map {origin.MapId} nor the bind point; the session is kicked"),
                    AnomalySeverity.Error);
                player.Session.Kick();
            }
        }
    }

    /// <summary>
    /// A new far teleport for a player that is in no map (the failed-teleport fallback to the bind point, vmangos <c>TeleportToHomebind</c>): the
    /// client gets SMSG_TRANSFER_PENDING and SMSG_NEW_WORLD for the map it must load and the entry waits for its MSG_MOVE_WORLDPORT_ACK, as
    /// the tail of <see cref="ExecuteTeleportFar"/>. World thread.
    /// </summary>
    private bool ReissueFar(Player player, Map sourceMap, TeleportDestination target)
    {
        if (!ReferenceEquals(_world.FindOnlinePlayer(player.Guid), player) || player.Map is not null)
        {
            return false;
        }

        var fallback = new Pending(this, player, target, TeleportStage.Far, sourceMap, target)
        {
            IsHomebindFallback = true,
            StartedMs = _world.NowMs,
            StageSinceMs = _world.NowMs,
        };
        _pending[player.Guid] = fallback;
        sourceMap.BeginTransit(player);
        player.MapId = target.MapId;
        player.Relocate(target.X, target.Y, target.Z, target.Orientation, _world.NowMs);
        player.Session.Send(WorldOpcode.SmsgTransferPending, TeleportPackets.BuildTransferPending(target.MapId));
        player.Session.Send(WorldOpcode.SmsgNewWorld, TeleportPackets.BuildNewWorld(target.MapId, target.X, target.Y, target.Z, target.Orientation));
        FarStageReached?.Invoke(player, TeleportStage.Far);
        return true;
    }

    private bool TryEnterMap(Player player, TeleportDestination dest)
    {
        // The map resolver picks (or creates) the instance and binds the player or its group
        // (vmangos MapManager::CreateMap → DungeonMap::Add); null refuses the entry.
        IMapResolver? resolver = _world.MapResolver;
        Map? map = resolver is null ? _world.GetMap(dest.MapId) : resolver.ResolveEntry(player, dest.MapId);
        if (map is null || map.FindObject(player.Guid) is not null)
        {
            return false;
        }

        player.MapId = dest.MapId;
        if (player.Transport is { } ship)
        {
            MovementInfo aboard = player.Movement;
            if (ReferenceEquals(ship.CurrentMap, map))
            {
                // vmangos HandleMoveWorldportAckOpcode: "Transport position may have changed while loading" - the player
                // enters at its offset from where the ship is now.
                (float x, float y, float z, float o) = (aboard.TransportX, aboard.TransportY, aboard.TransportZ, aboard.TransportOrientation);
                ship.CalculatePassengerPosition(ref x, ref y, ref z, ref o);
                player.Relocate(x, y, z, o, _world.NowMs);
                player.SetTransportData(ship.Guid, aboard.TransportX, aboard.TransportY, aboard.TransportZ, aboard.TransportOrientation);
            }
            else
            {
                // The ship is not where the player arrives (a failed entry sent it back): it is left behind.
                ship.RemovePassenger(player);
                player.Relocate(dest.X, dest.Y, dest.Z, dest.Orientation, _world.NowMs);
            }
        }
        else
        {
            player.Relocate(dest.X, dest.Y, dest.Z, dest.Orientation, _world.NowMs);
        }

        UpdateZone(map, player);
        _beforeAddToMap(player);
        map.AddPlayer(player);
        _afterAddToMap(player);
        resolver?.OnEntered(player, map);
        TeleportCompleted?.Invoke(player);
        return true;
    }

    /// <summary>
    /// H1 outcome check, run when a teleport has put the player at <paramref name="dest"/>: the player must be on the requested map,
    /// at a finite position, not in the void, and within <see cref="LandingToleranceYards"/> of the destination. Free on success (a few
    /// comparisons); formats only on failure. A player aboard a ship lands at its offset, so the distance is not checked there.
    /// </summary>
    private static void VerifyLanding(Player player, TeleportDestination dest, uint expectedMapId)
    {
        float x = player.X;
        float y = player.Y;
        float z = player.Z;
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            ServerAnomalies.Report(player, ServerAnomalies.Teleport, "landed-non-finite", "player position is not finite after the teleport", AnomalySeverity.Error);
            return;
        }

        if (z < LandingVoidHeight)
        {
            ServerAnomalies.Report(
                player,
                ServerAnomalies.Teleport,
                "landed-under-map",
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"player landed at z {z:F1}, below the void height {LandingVoidHeight:F0} (requested z {dest.Z:F1})"),
                AnomalySeverity.Error);
            return;
        }

        if (player.MapId != expectedMapId)
        {
            ServerAnomalies.Report(
                player,
                ServerAnomalies.Teleport,
                "landed-off-destination",
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"player is on map {player.MapId}, the teleport was to map {expectedMapId}"));
            return;
        }

        if (player.Transport is not null)
        {
            return;
        }

        float dx = x - dest.X;
        float dy = y - dest.Y;
        float dz = z - dest.Z;
        if (dx * dx + dy * dy + dz * dz > LandingToleranceYards * LandingToleranceYards)
        {
            ServerAnomalies.Report(
                player,
                ServerAnomalies.Teleport,
                "landed-off-destination",
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"player landed {MathF.Sqrt(dx * dx + dy * dy + dz * dz):F1} yards from the requested {dest.X:F1},{dest.Y:F1},{dest.Z:F1} (tolerance {LandingToleranceYards:F0})"));
        }
    }

    /// <summary>How far from the requested point a teleport may land before it is reported (float noise and a relocation step).</summary>
    public const float LandingToleranceYards = 5f;

    private const float LandingVoidHeight = -500f;

    private static void UpdateZone(Map map, Player player)
    {
        (uint zoneId, _) = map.GetZoneAndAreaId(player.X, player.Y, player.Z);
        if (zoneId != 0)
        {
            player.ZoneId = zoneId;
        }
    }

    // vmangos ExecuteTeleportNear → TeleportPositionRelocation (Unit.cpp:9855-9856): m_movementInfo.ChangePosition only. TeleportTo
    // already stopped the motion; swimming, levitating and the other client state stay. The fall in progress ends (vmangos
    // SetFallInformation(0) in the near branch of TeleportTo, Player.cpp:1932).
    private void ArriveNear(Player player, TeleportDestination dest)
    {
        MovementInfo arrived = player.Movement;
        arrived.X = dest.X;
        arrived.Y = dest.Y;
        arrived.Z = dest.Z;
        arrived.Orientation = dest.Orientation;
        player.ApplyMovement(arrived, _world.NowMs);
        // Unit::TeleportPositionRelocation -> Player::SetPosition(..., teleport = true) (Unit.cpp:9855): positionChanged ends a state emote.
        player.ClearEmoteState();
        if (LocomotionStates.TryGet(player, out LocomotionState state))
        {
            state.ResetFall();
        }
    }

    // vmangos TeleportTo (Player.cpp:1884-1889): the moving and turning flags go; ONTRANSPORT and the ship data go too unless the
    // player stays aboard.
    private void ResetMovementForTeleport(Player player, bool staysAboard)
    {
        MovementInfo movement = player.Movement;
        movement.Flags &= ~(MovementFlags.MaskMoving | MovementFlags.TurnLeft | MovementFlags.TurnRight);
        if (!staysAboard)
        {
            movement.Flags &= ~MovementFlags.OnTransport;
            movement.TransportGuid = 0;
            movement.TransportX = 0;
            movement.TransportY = 0;
            movement.TransportZ = 0;
            movement.TransportOrientation = 0;
        }

        player.ApplyMovement(movement, _world.NowMs);
    }

    // vmangos MovementPacketSender::SendTeleportToObservers → SendObjectMessageToSet: the
    // players within visibility distance of the mover's current position (Map::MessageBroadcast
    // visits the cells within GetVisibilityDistance), except the mover itself. Only clients that
    // have the mover can apply the packet, so the candidates are its observers.
    private void SendTeleportToObservers(Map map, Player player, TeleportDestination dest, ref byte[]? packet)
    {
        IReadOnlyCollection<Player> observers = map.ObserversOf(player);
        if (observers.Count == 0)
        {
            return;
        }

        bool ownsScratch = !_observerScratchInUse;
        List<Player> snapshot = ownsScratch ? _observerScratch : [];
        _observerScratchInUse |= ownsScratch;
        try
        {
            snapshot.Clear();
            snapshot.AddRange(observers);
            for (int i = 0; i < snapshot.Count; i++)
            {
                Player observer = snapshot[i];
                if (!ReferenceEquals(observer, player) && Map.IsWithinVisibilityDistance(observer, player, alreadyVisible: true))
                {
                    if (packet is null)
                    {
                        MovementInfo moved = player.Movement;
                        moved.X = dest.X;
                        moved.Y = dest.Y;
                        moved.Z = dest.Z;
                        moved.Orientation = dest.Orientation;
                        moved.Time = _world.NowMs;
                        packet = TeleportPackets.BuildMoveTeleport(player.Guid, moved);
                    }

                    observer.Session.Send(WorldOpcode.MsgMoveTeleport, packet);
                }
            }
        }
        finally
        {
            if (ownsScratch)
            {
                snapshot.Clear();
                _observerScratchInUse = false;
            }
        }
    }

    private const uint SlowTeleportAckThresholdMs = 10_000;

    private sealed class Pending(
        TeleportService service,
        Player player,
        TeleportDestination destination,
        TeleportStage stage,
        Map sourceMap,
        TeleportDestination origin)
    {
        public Player Player { get; } = player;

        public TeleportDestination Destination { get; } = destination;

        public TeleportStage Stage { get; set; } = stage;

        public Map SourceMap { get; } = sourceMap;

        public TeleportDestination Origin { get; } = origin;

        /// <summary>Whether this teleport is the bind-point fallback of a failed far teleport (it is not retried when it fails too).</summary>
        public bool IsHomebindFallback { get; init; }

        /// <summary>vmangos DELAYED_CAST_HONORLESS_TARGET scheduled by a same-map teleport.</summary>
        public bool HonorlessTarget { get; init; }

        public uint StartedMs { get; set; }

        /// <summary>When the current <see cref="Stage"/> began (the stall budgets count from here).</summary>
        public uint StageSinceMs { get; set; }

        /// <summary>Whether the stall of this teleport was reported (one report per teleport).</summary>
        public bool StallReported { get; set; }

        /// <summary>The world clock when <see cref="StallReported"/> was set (recovery grace counts from here).</summary>
        public uint StallReportedAtMs { get; set; }

        public void ExecuteFar() => service.ExecuteTeleportFar(Player, this);

        public void CompleteFar() => service.CompleteTeleportFar(Player, this);
    }
}
