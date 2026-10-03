using ArcaneCore.Protocol;
using ArcaneCore.World.Gm.Args;

namespace ArcaneCore.World.Gm.Server;

/// <summary>SMSG_SERVER_MESSAGE type (vmangos World.h:64-69; wow_messages smsg_server_message.wowm).</summary>
public enum ServerMessageType : uint
{
    ShutdownTime = 1,
    RestartTime = 2,
    Custom = 3,
    ShutdownCancelled = 4,
    RestartCancelled = 5,
}

/// <summary>vmangos ShutdownMask (World.h:71-75).</summary>
[Flags]
public enum ShutdownMask : uint
{
    None = 0,
    Restart = 1,
    Idle = 2,
}

/// <summary>SMSG_SERVER_MESSAGE: u32 message type, CString text.</summary>
public static class ServerMessagePackets
{
    public static byte[] Build(ServerMessageType type, string text)
    {
        var writer = new PacketWriter(8 + text.Length);
        writer.WriteUInt32((uint)type);
        writer.WriteCString(text);
        return writer.ToArray();
    }
}

/// <summary>
/// The server shutdown/restart state machine of vmangos World (D:\refs\vmangos\src\game\World.cpp:
/// <c>_UpdateGameTime</c> 2667-2693, <c>ShutdownServ</c> 2697-2720, <c>ShutdownMsg</c> 2723-2750,
/// <c>ShutdownCancel</c> 2753-2768). No clock and no threads: <see cref="Tick"/> is told how many
/// whole seconds passed, and the world thread owns every call.
/// </summary>
/// <param name="activeSessions">Current session count (idle shutdowns wait for zero).</param>
/// <param name="broadcast">Sends an SMSG_SERVER_MESSAGE to every player.</param>
public sealed class ShutdownScheduler(Func<int> activeSessions, Action<ServerMessageType, string> broadcast)
{
    /// <summary>vmangos SHUTDOWN_EXIT_CODE.</summary>
    public const byte ShutdownExitCode = 0;

    /// <summary>vmangos RESTART_EXIT_CODE.</summary>
    public const byte RestartExitCode = 2;

    /// <summary>The server stops now (vmangos <c>m_stopEvent</c>).</summary>
    public bool StopRequested { get; private set; }

    /// <summary>Seconds left before the stop (0 when none is scheduled).</summary>
    public uint Timer { get; private set; }

    public ShutdownMask Mask { get; private set; }

    public byte ExitCode { get; private set; } = ShutdownExitCode;

    /// <summary>
    /// Schedule a stop after <paramref name="time"/> seconds. Ignored once a stop is under way
    /// (World.cpp:2700). A delay of 0 stops at once, except an idle stop with players online,
    /// which arms a one second timer so the session count is re-evaluated.
    /// </summary>
    public void Request(uint time, ShutdownMask mask, byte exitCode)
    {
        if (StopRequested)
        {
            return;
        }

        Mask = mask;
        ExitCode = exitCode;
        if (time == 0)
        {
            if (!mask.HasFlag(ShutdownMask.Idle) || activeSessions() == 0)
            {
                StopRequested = true;
            }
            else
            {
                Timer = 1;
            }

            return;
        }

        Timer = time;
        Announce(show: true);
    }

    /// <summary>Advance by <paramref name="elapsedSeconds"/> whole seconds.</summary>
    public void Tick(uint elapsedSeconds)
    {
        if (StopRequested || Timer == 0 || elapsedSeconds == 0)
        {
            return;
        }

        if (Timer <= elapsedSeconds)
        {
            if (!Mask.HasFlag(ShutdownMask.Idle) || activeSessions() == 0)
            {
                StopRequested = true;
            }
            else
            {
                Timer = 1;   // minimum timer value to wait for the idle state
            }
        }
        else
        {
            Timer -= elapsedSeconds;
            Announce(show: false);
        }
    }

    /// <summary>Cancel a pending stop (World.cpp:2753): silent when none is pending or it is too late.</summary>
    public void Cancel()
    {
        if (Timer == 0 || StopRequested)
        {
            return;
        }

        ServerMessageType type = Mask.HasFlag(ShutdownMask.Restart) ? ServerMessageType.RestartCancelled : ServerMessageType.ShutdownCancelled;
        Mask = ShutdownMask.None;
        Timer = 0;
        ExitCode = ShutdownExitCode;
        broadcast(type, string.Empty);
    }

    // World.cpp:2723-2750: not for idle stops; announce on request, every second below 10 s, every
    // 5 s below 30 s, every minute below 5 min, every 5 min below 30 min, hourly below 12 h and
    // every 12 h above.
    private void Announce(bool show)
    {
        if (Mask.HasFlag(ShutdownMask.Idle))
        {
            return;
        }

        uint t = Timer;
        const uint minute = 60;
        const uint hour = 3600;
        bool due = show
            || t < 10
            || (t < 30 && t % 5 == 0)
            || (t < 5 * minute && t % minute == 0)
            || (t < 30 * minute && t % (5 * minute) == 0)
            || (t < 12 * hour && t % hour == 0)
            || (t > 12 * hour && t % (12 * hour) == 0);
        if (!due)
        {
            return;
        }

        ServerMessageType type = Mask.HasFlag(ShutdownMask.Restart) ? ServerMessageType.RestartTime : ServerMessageType.ShutdownTime;
        broadcast(type, GmDuration.SecsToTimeString(t));
    }
}
