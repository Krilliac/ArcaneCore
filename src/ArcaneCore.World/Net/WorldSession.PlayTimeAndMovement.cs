using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Net;

/// <summary>
/// The two opt-in server flows that send opcodes no retail-default flow sends (both default off):
/// <list type="bullet">
/// <item><c>World:LimitPlayTime</c>: the consecutive-play-time limit, vmangos <c>WorldSession::CheckPlayedTimeLimit</c> run from the session update when
/// the option is on and the player is in the world (WorldSession.cpp:518-520). The play time is measured from the first tick this session ran in the world,
/// on the world clock (<see cref="WorldRuntime.NowMs"/>, wrap-safe for 49 days); vmangos measures from the session's creation, a few seconds earlier.
/// Rules: <see cref="PlayTimeLimit"/>. World thread only (called from <see cref="ProcessWorldPackets"/>).</item>
/// <item><c>World:CompressMovementCount</c>: relays of movement to this client are batched into SMSG_COMPRESSED_MOVES once the session was sent that many
/// within 10 seconds (vmangos <c>WorldSession::SendMovementPacket</c>, WorldSession.cpp:221-266, with the batch flush at WorldSession.cpp:505-513).
/// <see cref="SendMovement"/> is called by the map's observer broadcast (any map thread holding the serial gate or the world thread); a lock guards the
/// batch, as vmangos' <c>m_movementPacketCompressorMutex</c> does. The batch is flushed on this session's next tick and dropped at logout.</item>
/// </list>
/// </summary>
public sealed partial class WorldSession
{
    private const uint MovementIntervalMs = 10_000;

    private bool _playTimeStarted;
    private uint _playTimeStartMs;
    private uint _exhaustionState;
    private Player? _playFlagsPlayer;

    private readonly object _movementLock = new();
    private MovementPacketBatch? _movementBatch;
    private int _movePacketsThisInterval;
    private int _movePacketsLastInterval;
    private bool _moveIntervalStarted;
    private uint _moveIntervalStartMs;

    /// <summary>Consecutive play time in seconds as of <paramref name="nowMs"/> (internal for tests of the limit's thresholds).</summary>
    internal uint PlayedSeconds(uint nowMs) => _playTimeStarted ? unchecked(nowMs - _playTimeStartMs) / 1000 : 0;

    /// <summary>The play-time step this session has reached (0, or one of the <see cref="PlayTimeLimit"/> limits).</summary>
    internal uint ExhaustionState => _exhaustionState;

    /// <summary>The per-tick play-time check (world thread). One bool read while the option is off.</summary>
    private void CheckPlayTime(Player player)
    {
        if (!World.Options.LimitPlayTime)
        {
            return;
        }

        uint now = World.NowMs;
        if (!_playTimeStarted)
        {
            _playTimeStarted = true;
            _playTimeStartMs = now;
        }

        uint played = PlayedSeconds(now);
        if (!ReferenceEquals(_playFlagsPlayer, player))
        {
            // A character that enters with the session already long-played gets the flags as it loads (Player.cpp:14686-14697).
            _playFlagsPlayer = player;
            PlayTimeLimit.ApplyLoadFlags(player, played);
        }

        _exhaustionState = PlayTimeLimit.Check(player, played, _exhaustionState);
    }

    /// <summary>
    /// Relay one movement packet to this client (vmangos <c>SendMovementPacket</c>): sent at once while the session has seen fewer than
    /// <c>World:CompressMovementCount</c> movement packets in this and the last 10 s interval, else added to the batch that becomes one
    /// SMSG_COMPRESSED_MOVES at the next tick. A packet that cannot be batched flushes the batch first, so order holds.
    /// </summary>
    public void SendMovement(WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        int threshold = World.Options.CompressMovementCount;
        if (threshold <= 0 || !MovementPacketBatch.IsBatchable(opcode))
        {
            Send(opcode, payload);
            return;
        }

        lock (_movementLock)
        {
            if (++_movePacketsThisInterval >= threshold || _movePacketsLastInterval >= threshold)
            {
                _movementBatch ??= new MovementPacketBatch();
                if (_movementBatch.CanAdd(payload.Length))
                {
                    _movementBatch.Add(opcode, payload);
                    return;
                }

                FlushMovementBatchLocked();
            }

            Send(opcode, payload);
        }
    }

    /// <summary>Flush the batch and roll the 10 s interval (world thread, each tick; WorldSession.cpp:505-513).</summary>
    private void TickMovementBatch()
    {
        if (World.Options.CompressMovementCount <= 0 && _movementBatch is null)
        {
            return;
        }

        lock (_movementLock)
        {
            FlushMovementBatchLocked();
            uint now = World.NowMs;
            if (!_moveIntervalStarted)
            {
                _moveIntervalStarted = true;
                _moveIntervalStartMs = now;
            }
            else if (unchecked(now - _moveIntervalStartMs) > MovementIntervalMs)
            {
                _moveIntervalStartMs = now;
                _movePacketsLastInterval = _movePacketsThisInterval;
                _movePacketsThisInterval = 0;
            }
        }
    }

    /// <summary>Drop what was batched and the interval counts (the player left the world; vmangos clears the compressor in LogoutPlayer).</summary>
    private void ClearMovementBatch()
    {
        lock (_movementLock)
        {
            _movementBatch?.Clear();
            _movePacketsThisInterval = 0;
            _movePacketsLastInterval = 0;
            _moveIntervalStarted = false;
        }
    }

    private void FlushMovementBatchLocked()
    {
        if (_movementBatch is not { HasData: true } batch)
        {
            return;
        }

        byte[] body = batch.Build();
        batch.Clear();
        Send(WorldOpcode.SmsgCompressedMoves, body);
    }
}
