using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Entities;

/// <summary>
/// The consecutive-play-time limit (the regional anti-addiction rule), a re-expression of vmangos <c>WorldSession::CheckPlayedTimeLimit</c>
/// (WorldSession.cpp:377-420) and the load-time flag rule of Player.cpp:14686-14697, active for build 5875 (its code sits under
/// <c>SUPPORTED_CLIENT_BUILD &gt; CLIENT_BUILD_1_7_1</c>) and gated by <c>World:LimitPlayTime</c> (vmangos CONFIG_BOOL_LIMIT_PLAY_TIME,
/// default false; here too). mangos-classic has no such flow. Pure functions over a <see cref="Player"/> and its session: no state of their own
/// (the session keeps the exhaustion step), world thread only because they write the player's flags.
/// </summary>
public static class PlayTimeLimit
{
    /// <summary>vmangos <c>PLAY_TIME_LIMIT_APPROACHING_PARTIAL</c> (WorldSession.h:139): 2 h 30 min, in seconds.</summary>
    public const uint ApproachingPartialSeconds = 2 * 3600 + 30 * 60;

    /// <summary>vmangos <c>PLAY_TIME_LIMIT_PARTIAL</c> (WorldSession.h:140): 3 h.</summary>
    public const uint PartialSeconds = 3 * 3600;

    /// <summary>vmangos <c>PLAY_TIME_LIMIT_APPROCHING_FULL</c> (WorldSession.h:141): 4 h 30 min.</summary>
    public const uint ApproachingFullSeconds = 4 * 3600 + 30 * 60;

    /// <summary>vmangos <c>PLAY_TIME_LIMIT_FULL</c> (WorldSession.h:142): 5 h.</summary>
    public const uint FullSeconds = 5 * 3600;

    /// <summary>vmangos <c>PTF_APPROACHING_PARTIAL_PLAY_TIME</c> (WorldSession.h:147).</summary>
    public const uint ApproachingPartialFlag = 0x1000;

    /// <summary>vmangos <c>PTF_APPROACHING_NO_PLAY_TIME</c> (WorldSession.h:148).</summary>
    public const uint ApproachingNoPlayTimeFlag = 0x2000;

    /// <summary>vmangos <c>PTF_UNHEALTHY_TIME</c> (WorldSession.h:149).</summary>
    public const uint UnhealthyFlag = 0x80000000;

    /// <summary>The SMSG_PLAY_TIME_WARNING body: u32 flag, i32 seconds left (vmangos Misc.cpp:285-289).</summary>
    public static byte[] WarningBody(uint flag, int secondsLeft)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt32(flag);
        writer.WriteInt32(secondsLeft);
        return writer.ToArray();
    }

    /// <summary>Send the unhealthy-time warning (flag <see cref="UnhealthyFlag"/>, no time left) to <paramref name="player"/>'s client.</summary>
    public static void SendUnhealthyWarning(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        player.Session.Send(WorldOpcode.SmsgPlayTimeWarning, WarningBody(UnhealthyFlag, 0));
    }

    /// <summary>
    /// <c>CheckPlayedTimeLimit</c>: given the consecutive play time and the step already reached (<paramref name="exhaustionState"/>, one of the
    /// four limit values or 0), send the warning and set or clear the player's play-time flags when a higher step is reached. The state only ever
    /// rises. Returns the new state.
    /// </summary>
    public static uint Check(Player player, uint playedSeconds, uint exhaustionState)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (playedSeconds >= FullSeconds)
        {
            if (exhaustionState < FullSeconds)
            {
                SendUnhealthyWarning(player);
                player.Flags = (player.Flags | PlayerFlags.NoPlayTime) & ~PlayerFlags.PartialPlayTime;
                return FullSeconds;
            }
        }
        else if (playedSeconds >= ApproachingFullSeconds)
        {
            if (exhaustionState < ApproachingFullSeconds)
            {
                Approaching(player, ApproachingNoPlayTimeFlag, FullSeconds - playedSeconds);
                return ApproachingFullSeconds;
            }
        }
        else if (playedSeconds >= PartialSeconds)
        {
            if (exhaustionState < PartialSeconds)
            {
                Approaching(player, ApproachingNoPlayTimeFlag, FullSeconds - playedSeconds);
                return PartialSeconds;
            }
        }
        else if (playedSeconds >= ApproachingPartialSeconds && exhaustionState < ApproachingPartialSeconds)
        {
            player.Session.Send(WorldOpcode.SmsgPlayTimeWarning, WarningBody(ApproachingPartialFlag, (int)(PartialSeconds - playedSeconds)));
            return ApproachingPartialSeconds;
        }

        return exhaustionState;
    }

    /// <summary>
    /// The flags a player gets when it enters the world with a session that has already played long (Player.cpp:14690-14697): at 5 h NO_PLAY_TIME,
    /// else from 3 h PARTIAL_PLAY_TIME. Flags only ever get added here, as there.
    /// </summary>
    public static void ApplyLoadFlags(Player player, uint playedSeconds)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (playedSeconds >= FullSeconds)
        {
            player.Flags |= PlayerFlags.NoPlayTime;
        }
        else if (playedSeconds >= PartialSeconds)
        {
            player.Flags |= PlayerFlags.PartialPlayTime;
        }
    }

    /// <summary>
    /// vmangos <c>Player::LootMoney</c> (Player.cpp:21793-21797): a NO_PLAY_TIME player gets no looted money, a PARTIAL_PLAY_TIME player half (integer
    /// division); the flags exist only under <c>World:LimitPlayTime</c>.
    /// </summary>
    public static uint ApplyToLootedMoney(Player player, uint copper)
    {
        ArgumentNullException.ThrowIfNull(player);
        if ((player.Flags & PlayerFlags.NoPlayTime) != 0)
        {
            return 0;
        }

        return (player.Flags & PlayerFlags.PartialPlayTime) != 0 ? copper / 2 : copper;
    }

    private static void Approaching(Player player, uint flag, uint secondsLeft)
    {
        player.Session.Send(WorldOpcode.SmsgPlayTimeWarning, WarningBody(flag, (int)secondsLeft));
        player.Flags = (player.Flags | PlayerFlags.PartialPlayTime) & ~PlayerFlags.NoPlayTime;
    }
}
