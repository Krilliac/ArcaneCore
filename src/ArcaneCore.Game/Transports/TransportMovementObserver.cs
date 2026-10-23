using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.GameObjects;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Transports;

/// <summary>
/// Boarding and leaving ships from client movement (vmangos <c>WorldSession::HandleMoverRelocation</c>,
/// MovementHandler.cpp:1075-1102, re-implemented):
/// <list type="bullet">
/// <item>Aboard already: the world position is recomputed from the offset the client sends and the ship's own position,
/// before the block is stored (the client's world coordinates are not trusted), and the "just boarded" mark is cleared.</item>
/// <item>Not aboard, ONTRANSPORT set: the ship named by the block is looked up on the player's map and boarded, keeping the
/// client's offset; the player is marked "just boarded", and a CMSG_MOVE_TIME_SKIPPED that comes before its next movement
/// aboard sends the ship again (a 1.12 client quirk). An unknown GUID boards nothing, as in the reference.</item>
/// <item>Aboard, ONTRANSPORT cleared: the player leaves the ship.</item>
/// </list>
/// Does nothing while no <see cref="TransportSystem"/> is registered for the world (transports disabled).
/// </summary>
[MovementObserver(Order = -100)]
internal sealed class TransportMovementObserver : IClientMovementObserver
{
    public const float PassengerDesyncThresholdYards = 25.0f;

    public void BeforeApply(MovementObserverContext context, in MovementInfo previous, ref MovementInfo incoming)
    {
        Player player = context.Player;
        LocomotionState state = player.Locomotion;

        if (player.Transport is { } ship && incoming.HasFlag(MovementFlags.OnTransport))
        {
            float tx = incoming.TransportX, ty = incoming.TransportY, tz = incoming.TransportZ, to = incoming.TransportOrientation;
            float expectedX = tx, expectedY = ty, expectedZ = tz, expectedO = to;
            ship.CalculatePassengerPosition(ref expectedX, ref expectedY, ref expectedZ, ref expectedO);

            if (!state.SuppressAnomalies)
            {
                if (!TransportSystem.IsOffsetWithinBounds(tx, ty, tz))
                {
                    if (TransportAnomalies.TryLatch(state, TransportAnomalyLatch.PassengerLeftBehind))
                    {
                        TransportAnomalies.Report(
                            "passenger-left-behind",
                            player,
                            $"Passenger offset ({tx:F2}, {ty:F2}, {tz:F2}) on transport entry {ship.Entry} exceeds max passenger offset {TransportSystem.MaxPassengerOffset:F0}yd on map {player.MapId}.");
                    }
                }
                else
                {
                    state.PassengerLeftBehindReportedInEpisode = false;
                    float dx = incoming.X - expectedX;
                    float dy = incoming.Y - expectedY;
                    float dz = incoming.Z - expectedZ;
                    float distSq = (dx * dx) + (dy * dy) + (dz * dz);
                    if (!float.IsFinite(distSq) || distSq > PassengerDesyncThresholdYards * PassengerDesyncThresholdYards)
                    {
                        if (TransportAnomalies.TryLatch(state, TransportAnomalyLatch.TransportDesync))
                        {
                            float dist = float.IsFinite(distSq) ? MathF.Sqrt(distSq) : float.PositiveInfinity;
                            TransportAnomalies.Report(
                                "passenger-desync",
                                player,
                                $"Client world position ({incoming.X:F2}, {incoming.Y:F2}, {incoming.Z:F2}) diverged by {dist:F2}yd from transport entry {ship.Entry} transformed offset ({expectedX:F2}, {expectedY:F2}, {expectedZ:F2}) on map {player.MapId}.");
                        }
                    }
                    else
                    {
                        state.TransportDesyncReportedInEpisode = false;
                    }
                }
            }

            incoming.X = expectedX;
            incoming.Y = expectedY;
            incoming.Z = expectedZ;
            incoming.Orientation = expectedO;
            return;
        }

        if (player.Transport is null
            && incoming.HasFlag(MovementFlags.OnTransport)
            && !state.SuppressAnomalies
            && player.Map is { } map)
        {
            var transportGuid = new ObjectGuid(incoming.TransportGuid);
            if (FindElevatorTransport(map, transportGuid) is { } elevator)
            {
                float tx = incoming.TransportX, ty = incoming.TransportY, tz = incoming.TransportZ;
                if (!TransportSystem.IsOffsetWithinBounds(tx, ty, tz))
                {
                    if (TransportAnomalies.TryLatch(state, TransportAnomalyLatch.PassengerLeftBehind))
                    {
                        TransportAnomalies.Report(
                            "passenger-left-behind",
                            player,
                            $"Passenger offset ({tx:F2}, {ty:F2}, {tz:F2}) on elevator transport entry {elevator.Entry} exceeds max passenger offset {TransportSystem.MaxPassengerOffset:F0}yd on map {player.MapId}.");
                    }
                }
                else
                {
                    state.PassengerLeftBehindReportedInEpisode = false;
                    if (map.FindUpdater<GameObjectMapSystem>()?.ElevatorPosition(elevator) is { } elevPos)
                    {
                        float offsetLen = MathF.Sqrt((tx * tx) + (ty * ty) + (tz * tz));
                        float worldDx = incoming.X - elevPos.X;
                        float worldDy = incoming.Y - elevPos.Y;
                        float worldDz = incoming.Z - elevPos.Z;
                        float worldDist = MathF.Sqrt((worldDx * worldDx) + (worldDy * worldDy) + (worldDz * worldDz));
                        float diff = MathF.Abs(worldDist - offsetLen);
                        if (!float.IsFinite(diff) || diff > PassengerDesyncThresholdYards)
                        {
                            if (TransportAnomalies.TryLatch(state, TransportAnomalyLatch.TransportDesync))
                            {
                                TransportAnomalies.Report(
                                    "passenger-desync",
                                    player,
                                    $"Client world position ({incoming.X:F2}, {incoming.Y:F2}, {incoming.Z:F2}) diverged by {diff:F2}yd from elevator transport entry {elevator.Entry} position ({elevPos.X:F2}, {elevPos.Y:F2}, {elevPos.Z:F2}) on map {player.MapId}.");
                            }
                        }
                        else
                        {
                            state.TransportDesyncReportedInEpisode = false;
                        }
                    }
                }
            }
        }
    }

    public void AfterApply(MovementObserverContext context, in MovementInfo previous)
    {
        Player player = context.Player;
        LocomotionState state = player.Locomotion;
        if (player.Movement.HasFlag(MovementFlags.OnTransport))
        {
            if (player.Transport is null)
            {
                if (player.Map is { } map)
                {
                    var transportGuid = new ObjectGuid(player.Movement.TransportGuid);
                    TransportSystem? system = TransportSystem.Of(context.World);
                    if (system?.Find(map, transportGuid) is { } ship)
                    {
                        float tx = player.Movement.TransportX;
                        float ty = player.Movement.TransportY;
                        float tz = player.Movement.TransportZ;
                        if (!TransportSystem.IsOffsetWithinBounds(tx, ty, tz))
                        {
                            if (!state.SuppressAnomalies && TransportAnomalies.TryLatch(state, TransportAnomalyLatch.PassengerLeftBehind))
                            {
                                TransportAnomalies.Report(
                                    "passenger-left-behind",
                                    player,
                                    $"Client attempted to board transport entry {ship.Entry} with out-of-bounds offset ({tx:F2}, {ty:F2}, {tz:F2}) on map {map.MapId}.");
                            }
                        }
                        else
                        {
                            state.LastUnknownTransportGuid = 0;
                            state.PassengerLeftBehindReportedInEpisode = false;
                            system.BoardFromClient(ship, player);
                        }
                    }
                    else if (FindElevatorTransport(map, transportGuid) is not null)
                    {
                        state.LastUnknownTransportGuid = 0;
                    }
                    else if (!state.SuppressAnomalies)
                    {
                        ulong rawGuid = player.Movement.TransportGuid;
                        // LastUnknownTransportGuid 0 means "no claim in progress", so an empty guid is remembered as MaxValue.
                        ulong claim = rawGuid == 0 ? ulong.MaxValue : rawGuid;
                        if (state.LastUnknownTransportGuid != claim)
                        {
                            state.LastUnknownTransportGuid = claim;
                            TransportAnomalies.Report(
                                "move",
                                "client-transport-unknown",
                                player,
                                $"Client claimed OnTransport for unknown transport guid 0x{rawGuid:X} ({transportGuid}) on map {map.MapId} at ({player.X:F2}, {player.Y:F2}, {player.Z:F2}).");
                        }
                    }
                }
            }
            else
            {
                state.LastUnknownTransportGuid = 0;
                // vmangos HandleMoverRelocation (MovementHandler.cpp:1087-1089): moving aboard ends "just boarded".
                TransportSystem.Of(context.World)?.ClearJustBoarded(player);
            }
        }
        else
        {
            state.LastUnknownTransportGuid = 0;
            state.TransportDesyncReportedInEpisode = false;
            state.PassengerLeftBehindReportedInEpisode = false;
            if (player.Transport is { } ship)
            {
                ship.RemovePassenger(player);
            }
        }
    }

    private static GameObject? FindElevatorTransport(Map map, ObjectGuid guid)
    {
        if (guid.IsEmpty)
        {
            return null;
        }

        if (map.FindObject(guid) is GameObject { Type: GameObjectType.Transport } go)
        {
            return go;
        }

        if (map.FindUpdater<GameObjectMapSystem>()?.Find(guid) is { Type: GameObjectType.Transport } sysGo)
        {
            return sysGo;
        }

        return null;
    }
}
