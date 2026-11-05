using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Net;

/// <summary>
/// The two opt-in server flows that send opcodes no retail-default flow sends (both default off):
/// <list type="bullet">
/// <item><c>World:LimitPlayTime</c>: the consecutive-play-time limit, vmangos <c>WorldSession::CheckPlayedTimeLimit</c> run from the session update when
/// the option is on and the player is in the world (WorldSession.cpp:518-520). The play time is the account's consecutive time: the seconds carried in when
/// this session registered (from the session it replaced, or from the account's last logout when that was under five hours ago, see <see cref="AccountPlayHistory"/>;
/// vmangos World.cpp:285-300) plus the game time since it registered (vmangos <c>GetConsecutivePlayTime</c>, WorldSession.h:267; the non-wrapping
/// <see cref="WorldClock.ElapsedMs"/>), so reconnecting does not reset it. If the option was off at registration the clock starts at the first tick.
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
    private long _playTimeStartMs;          // game ElapsedMs when the clock started (registration, or the first in-world tick)
    private uint _previousPlayedSeconds;    // carried in from an earlier session of the account
    private uint _exhaustionState;
    private Player? _playFlagsPlayer;

    private readonly object _movementLock = new();
    private MovementPacketBatch? _movementBatch;
    private int _movePacketsThisInterval;
    private int _movePacketsLastInterval;
    private bool _moveIntervalStarted;
    private uint _moveIntervalStartMs;

    /// <summary>
    /// Consecutive play time in seconds as of <paramref name="elapsedMs"/> (<see cref="WorldClock.ElapsedMs"/>): the carried seconds plus the time since the
    /// clock started; 0 while it has not started. Saturates at <see cref="uint.MaxValue"/>. Internal for the tests of the limit's thresholds.
    /// </summary>
    internal uint PlayedSeconds(long elapsedMs)
    {
        if (!_playTimeStarted)
        {
            return 0;
        }

        long seconds = _previousPlayedSeconds + Math.Max(0L, elapsedMs - _playTimeStartMs) / 1000;
        return (uint)Math.Min(seconds, uint.MaxValue);
    }

    /// <summary>
    /// Called by <see cref="SessionRegistry.Register"/> on the registering thread before the session is usable: start the play clock and take the carried
    /// seconds from <paramref name="replaced"/> (a double login: vmangos World.cpp:285) or from <paramref name="history"/> (World.cpp:293-300). Does nothing
    /// with <c>World:LimitPlayTime</c> off.
    /// </summary>
    internal void InheritPlayTime(WorldSession? replaced, AccountPlayHistory history)
    {
        if (!World.Options.LimitPlayTime)
        {
            return;
        }

        long now = World.Clock.ElapsedMs;
        _previousPlayedSeconds = replaced is not null ? replaced.PlayedSeconds(now) : history.TakeCarry(AccountId, now);
        _playTimeStartMs = now;
        _playTimeStarted = true;
    }

    /// <summary>Called by <see cref="SessionRegistry.Unregister"/> for the session that still held its account: remember the total for the next login.</summary>
    internal void RecordPlayTime(AccountPlayHistory history)
    {
        if (!_playTimeStarted)
        {
            return;
        }

        long now = World.Clock.ElapsedMs;
        history.Record(AccountId, PlayedSeconds(now), now);
    }

    /// <summary>The play-time step this session has reached (0, or one of the <see cref="PlayTimeLimit"/> limits).</summary>
    internal uint ExhaustionState => _exhaustionState;

    /// <summary>The per-tick play-time check (world thread). One bool read while the option is off.</summary>
    private void CheckPlayTime(Player player)
    {
        if (!World.Options.LimitPlayTime)
        {
            return;
        }

        long now = World.Clock.ElapsedMs;
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
