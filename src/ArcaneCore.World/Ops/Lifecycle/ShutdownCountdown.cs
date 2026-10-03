using ArcaneCore.Kernel.Ops;

namespace ArcaneCore.World.Ops.Lifecycle;

/// <summary>SMSG_SERVER_MESSAGE types (vmangos World.h:64-71 ServerMessageType; wow_messages smsg_server_message.wowm, 1.12 enum).</summary>
public enum ServerMessageType : uint
{
    ShutdownTime = 1,
    RestartTime = 2,
    Custom = 3,
    ShutdownCancelled = 4,
    RestartCancelled = 5,
}

/// <summary>vmangos World.h:73-77 ShutdownMask.</summary>
[Flags]
public enum ShutdownMask : byte
{
    None = 0,
    Restart = 1,
    Idle = 2,
}

/// <summary>A server message to broadcast to every in-world player.</summary>
public readonly record struct ServerMessage(ServerMessageType Type, string Text);

/// <summary>
/// The shutdown/restart timer as a pure state machine (no clock, no threads): vmangos
/// World::ShutdownServ / _UpdateGameTime / ShutdownMsg / ShutdownCancel
/// (World.cpp:2665-2767). The caller advances it by whole elapsed seconds; a stalled world may
/// advance by more than one, which skips intermediate announcement values exactly like vmangos.
/// </summary>
public sealed class ShutdownCountdown
{
    private uint _timer;
    private ShutdownMask _mask;
    private byte _exitCode;

    /// <summary>True once the timer expired and the process must stop; further requests are ignored (vmangos m_stopEvent).</summary>
    public bool StopRequested { get; private set; }

    /// <summary>A countdown is running (vmangos m_ShutdownTimer != 0).</summary>
    public bool IsPending => !StopRequested && _timer > 0;

    public uint SecondsRemaining => _timer;

    public ShutdownMask Mask => _mask;

    /// <summary>The exit code the pending (or expired) request asked for.</summary>
    public byte ExitCode => _exitCode;

    /// <summary>
    /// Start a countdown (vmangos ShutdownServ, World.cpp:2697-2721). Returns the announcement
    /// to broadcast, if any. A zero delay stops at once unless the request is idle and sessions
    /// remain, in which case the timer is pinned at 1 and re-evaluated on the next advance.
    /// </summary>
    public ServerMessage? Request(uint seconds, ShutdownMask options, byte exitCode, int activeSessions)
    {
        if (exitCode > ExitCodes.MaxRequested)
        {
            throw new ArgumentOutOfRangeException(nameof(exitCode), exitCode, "exit codes above 125 are reserved for shells");
        }

        if (StopRequested)
        {
            return null;
        }

        _mask = options;
        _exitCode = exitCode;
        if (seconds == 0)
        {
            if (!options.HasFlag(ShutdownMask.Idle) || activeSessions == 0)
            {
                StopRequested = true;
            }
            else
            {
                _timer = 1;
            }

            return null;
        }

        _timer = seconds;
        return Announcement(show: true);
    }

    /// <summary>
    /// Advance by <paramref name="elapsedSeconds"/> whole seconds (vmangos _UpdateGameTime,
    /// World.cpp:2665-2692). Returns the countdown announcement due at the new value, if any.
    /// </summary>
    public ServerMessage? Advance(uint elapsedSeconds, int activeSessions)
    {
        if (StopRequested || _timer == 0 || elapsedSeconds == 0)
        {
            return null;
        }

        if (_timer <= elapsedSeconds)
        {
            if (!_mask.HasFlag(ShutdownMask.Idle) || activeSessions == 0)
            {
                StopRequested = true;
            }
            else
            {
                _timer = 1; // minimum timer value to wait idle state
            }

            return null;
        }

        _timer -= elapsedSeconds;
        return Announcement(show: false);
    }

    /// <summary>
    /// Cancel a pending countdown (vmangos ShutdownCancel, World.cpp:2753-2767). The cancelled
    /// message type follows the mask as it was before being cleared. Null when nothing is
    /// pending or the stop is already due.
    /// </summary>
    public ServerMessage? Cancel()
    {
        if (_timer == 0 || StopRequested)
        {
            return null;
        }

        ServerMessageType type = _mask.HasFlag(ShutdownMask.Restart) ? ServerMessageType.RestartCancelled : ServerMessageType.ShutdownCancelled;
        _mask = ShutdownMask.None;
        _timer = 0;
        _exitCode = 0;
        return new ServerMessage(type, string.Empty);
    }

    // vmangos ShutdownMsg (World.cpp:2724-2750): never for idle mode; otherwise on the initial
    // request, below 10 s every second, then every 5 s below 30 s, every minute below 5 min,
    // every 5 min below 30 min, every hour below 12 h, every 12 h above 12 h (exactly 12 h is
    // covered by neither of the last two rules, as in vmangos).
    private ServerMessage? Announcement(bool show)
    {
        if (_mask.HasFlag(ShutdownMask.Idle))
        {
            return null;
        }

        const uint minute = 60;
        const uint hour = 3600;
        uint t = _timer;
        bool due = show
            || t < 10
            || (t < 30 && t % 5 == 0)
            || (t < 5 * minute && t % minute == 0)
            || (t < 30 * minute && t % (5 * minute) == 0)
            || (t < 12 * hour && t % hour == 0)
            || (t > 12 * hour && t % (12 * hour) == 0);
        if (!due)
        {
            return null;
        }

        ServerMessageType type = _mask.HasFlag(ShutdownMask.Restart) ? ServerMessageType.RestartTime : ServerMessageType.ShutdownTime;
        return new ServerMessage(type, ServerTimeText.Format(t));
    }
}
