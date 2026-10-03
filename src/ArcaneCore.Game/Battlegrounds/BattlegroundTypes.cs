using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Battlegrounds;

/// <summary>
/// Battleground type ids as the client and the database know them (vmangos <c>BattleGroundTypeId</c>: AV 1, WS 2, AB 3;
/// wow_messages <c>BattlegroundType</c> for 1.12 uses the same values for the queue lists).
/// </summary>
public enum BattlegroundType : byte
{
    None = 0,
    AlteracValley = 1,
    WarsongGulch = 2,
    ArathiBasin = 3,
}

/// <summary>
/// The queue a battleground type is queued in (vmangos <c>BattleGroundQueueTypeId</c>, BattleGroundDefines.h:162-168; the
/// numbering equals the type id for the three vanilla battlegrounds).
/// </summary>
public enum BattlegroundQueueType : byte
{
    None = 0,
    AlteracValley = 1,
    WarsongGulch = 2,
    ArathiBasin = 3,
}

/// <summary>Battleground lifecycle status (vmangos <c>BattleGroundStatus</c>, BattleGroundDefines.h:147-154; the wire value of SMSG_BATTLEFIELD_STATUS).</summary>
public enum BattlegroundStatus : byte
{
    /// <summary>First status, means the battleground is not an instance (and the value of an empty queue slot).</summary>
    None = 0,

    /// <summary>The battleground is empty and waiting for the queue.</summary>
    WaitQueue = 1,

    /// <summary>The battleground has been created and waits for players (the start countdown runs in this status).</summary>
    WaitJoin = 2,

    /// <summary>The match is running.</summary>
    InProgress = 3,

    /// <summary>A side has won (or the match ended); players are removed after <see cref="BattlegroundConstants.TimeToAutoRemoveMs"/>.</summary>
    WaitLeave = 4,
}

/// <summary>The winner byte of MSG_PVP_LOG_DATA (vmangos <c>BattleGroundWinner</c>, BattleGroundDefines.h:200-205; note Horde is 0 here).</summary>
public enum BattlegroundWinner : byte
{
    Horde = 0,
    Alliance = 1,
    None = 2,
}

/// <summary>Score kinds a battleground adds to a player's scoreboard row (vmangos <c>ScoreType</c>, BattleGroundDefines.h:180-198).</summary>
public enum BattlegroundScoreType : byte
{
    KillingBlows = 1,
    Deaths = 2,
    HonorableKills = 3,
    BonusHonor = 4,
    FlagCaptures = 7,
    FlagReturns = 8,
}

/// <summary>The chat style of a battleground announcement (the <c>CHAT_MSG_BG_SYSTEM_*</c> value vmangos picks, BattleGroundWS.cpp).</summary>
public enum BattlegroundChatKind : byte
{
    Neutral = 0,
    Alliance = 1,
    Horde = 2,
}

/// <summary>Bit flags of the start sequence a battleground has already run (vmangos <c>BattleGroundStartingEvents</c>, BattleGroundDefines.h:215-223).</summary>
[Flags]
public enum BattlegroundStartEvents : byte
{
    None = 0x00,
    First = 0x01,
    Second = 0x02,
    Third = 0x04,
    Fourth = 0x08,
    DoorsDespawned = 0x10,
}

/// <summary>The group-join result codes (vmangos <c>BattleGroundJoinError</c>, BattleGroundDefines.h:243-255).</summary>
public enum BattlegroundJoinError : byte
{
    Ok = 0,
    OfflineMember = 1,
    GroupTooMany = 2,
    MixedFaction = 3,
    MixedLevels = 4,
    GroupMemberAlreadyInQueue = 6,
    GroupDeserter = 7,
    AllQueuesUsed = 8,
    GroupNotEnough = 9,
}

/// <summary>Constants of the battleground framework; every value is cited to vmangos.</summary>
public static class BattlegroundConstants
{
    /// <summary>Number of level brackets a type has (vmangos <c>MAX_BATTLEGROUND_BRACKETS</c>, BattleGroundDefines.h:177).</summary>
    public const int BracketCount = 6;

    /// <summary>The highest bracket id (vmangos <c>BG_BRACKET_ID_LAST</c>, BattleGroundDefines.h:173).</summary>
    public const int LastBracket = 5;

    /// <summary>Delay of the first start event: two minutes (vmangos <c>BG_START_DELAY_2M</c>).</summary>
    public const int StartDelay2MinMs = 120_000;

    /// <summary>Delay of the second start event: one minute (vmangos <c>BG_START_DELAY_1M</c>).</summary>
    public const int StartDelay1MinMs = 60_000;

    /// <summary>Delay of the third start event: 30 seconds (vmangos <c>BG_START_DELAY_30S</c>).</summary>
    public const int StartDelay30SecMs = 30_000;

    /// <summary>Delay of the fourth start event: the doors open (vmangos <c>BG_START_DELAY_NONE</c>).</summary>
    public const int StartDelayNoneMs = 0;

    /// <summary>Players are removed this long after the match ended (vmangos <c>TIME_TO_AUTOREMOVE</c>, BattleGroundDefines.h:122).</summary>
    public const int TimeToAutoRemoveMs = 120_000;

    /// <summary>A queue invitation is valid for this long (vmangos <c>INVITE_ACCEPT_WAIT_TIME</c>, BattleGroundDefines.h:121).</summary>
    public const uint InviteAcceptWaitTimeMs = 80_000;

    /// <summary>A reminder status is sent this long after the invitation (vmangos <c>INVITATION_REMIND_TIME</c>, BattleGroundDefines.h:120).</summary>
    public const uint InvitationRemindTimeMs = 60_000;

    /// <summary>The doors are despawned once the start time exceeds this (vmangos BattleGround.cpp:433, "2 minutes preparation + 1 minute").</summary>
    public const uint DoorsDespawnStartTimeMs = 180_000;

    /// <summary>A loser only gets a mark when the match lasted longer than this (vmangos BattleGround.cpp:708, client patch 1.8.4).</summary>
    public const uint LoserMarkMinStartTimeMs = 10 * 60 * 1000;

    /// <summary>The scoreboard shows at most this many players (vmangos BuildPvpLogDataPacket, BattleGroundMgr.cpp:1068).</summary>
    public const int PvpLogMaxPlayers = 80;

    /// <summary>The honor rank sent in the log for a player who is not online (vmangos BuildPvpLogDataPacket, BattleGroundMgr.cpp:1085).</summary>
    public const uint DefaultPvpLogRank = 4;

    /// <summary>Sound id: Horde wins (vmangos <c>SOUND_HORDE_WINS</c>).</summary>
    public const uint SoundHordeWins = 8454;

    /// <summary>Sound id: Alliance wins (vmangos <c>SOUND_ALLIANCE_WINS</c>).</summary>
    public const uint SoundAllianceWins = 8455;

    /// <summary>Sound id: the battleground begins (vmangos <c>SOUND_BG_START</c>).</summary>
    public const uint SoundStart = 3439;

    /// <summary>Event number of the ghost gates (vmangos <c>BG_EVENT_GHOST_GATE</c>).</summary>
    public const byte EventGhostGate = 253;

    /// <summary>Event number of the doors (vmangos <c>BG_EVENT_DOOR</c>).</summary>
    public const byte EventDoor = 254;

    /// <summary>"No event" (vmangos <c>BG_EVENT_NONE</c>).</summary>
    public const byte EventNone = 255;

    /// <summary>The Deserter aura (vmangos Player::LeaveBattleground, Player.cpp:18693).</summary>
    public const uint SpellDeserter = 26013;

    /// <summary>The Waiting to Resurrect aura removed when leaving (Player.cpp:18681).</summary>
    public const uint SpellWaitingToResurrect = 2584;

    /// <summary>Spirit of Redemption, whose death does not count as a death (BattleGround.cpp:1784).</summary>
    public const uint SpellSpiritOfRedemption = 27827;

    /// <summary>
    /// Bracket of a level (vmangos <c>Player::GetBattleGroundBracketIdFromLevel</c>, Player.cpp:19455-19468):
    /// <c>(level - minLevel) / 10</c>, or -1 below the minimum level. vmangos tests <c>bracket &gt; MAX</c> (6) before
    /// returning the last bracket, which leaves id 6 for levels 70-79 (out of range); this clamps to
    /// <see cref="LastBracket"/> from 5 on, which is identical for every level a battleground accepts.
    /// </summary>
    public static int BracketOfLevel(uint level, uint minLevel)
    {
        if (level < minLevel)
        {
            return -1;
        }

        uint bracket = (level - minLevel) / 10;
        return bracket > LastBracket ? LastBracket : (int)bracket;
    }

    /// <summary>The team index used by the arrays of a battleground (vmangos <c>BG_TEAM_ALLIANCE</c> 0, <c>BG_TEAM_HORDE</c> 1).</summary>
    public static int TeamIndex(Team team) => team == Team.Alliance ? 0 : 1;

    /// <summary>The other team (vmangos <c>GetOtherTeam</c>).</summary>
    public static Team OtherTeam(Team team) => team == Team.Alliance ? Team.Horde : Team.Alliance;
}
