namespace ArcaneCore.Game.Battlegrounds;

/// <summary>
/// The text ids a battleground announces (vmangos <c>BattleGroundTexts</c>, BattleGroundDefines.h:48-84); the daemon resolves
/// them to the localized string.
/// </summary>
public static class BattlegroundTexts
{
    public const uint WsAllianceWins = 9843;
    public const uint WsHordeWins = 9842;
    public const uint WsStartOneMinute = 10015;
    public const uint WsStartHalfMinute = 10016;
    public const uint WsHasBegun = 10014;
    public const uint WsCapturedHordeFlag = 9801;
    public const uint WsCapturedAllianceFlag = 9802;
    public const uint WsDroppedHordeFlag = 9806;
    public const uint WsDroppedAllianceFlag = 9805;
    public const uint WsReturnedAllianceFlag = 9808;
    public const uint WsReturnedHordeFlag = 9809;
    public const uint WsPickedUpHordeFlag = 9807;
    public const uint WsPickedUpAllianceFlag = 9804;
    public const uint WsFlagsPlaced = 9803;
    public const uint WsAllianceFlagRespawned = 10022;
    public const uint WsHordeFlagRespawned = 10023;
}

/// <summary>A player's scoreboard row (vmangos <c>BattleGroundScore</c>, BattleGround.h:48-60).</summary>
public class BattlegroundScore
{
    public uint KillingBlows { get; set; }

    public uint Deaths { get; set; }

    public uint HonorableKills { get; set; }

    public uint BonusHonor { get; set; }

    /// <summary>The battleground-specific columns after the common ones (the client expects 2 for Warsong Gulch).</summary>
    public virtual IReadOnlyList<uint> ExtraFields => [];
}

/// <summary>The Warsong Gulch row (vmangos <c>BattleGroundWGScore</c>, BattleGroundWS.h:93-100): flag captures and flag returns.</summary>
public sealed class WsgScore : BattlegroundScore
{
    public uint FlagCaptures { get; set; }

    public uint FlagReturns { get; set; }

    public override IReadOnlyList<uint> ExtraFields => [FlagCaptures, FlagReturns];
}

/// <summary>One row of MSG_PVP_LOG_DATA (wow_messages <c>BattlegroundPlayer</c>).</summary>
public sealed record PvpLogRow(
    ObjectGuid Player,
    uint Rank,
    uint KillingBlows,
    uint HonorableKills,
    uint Deaths,
    uint BonusHonor,
    IReadOnlyList<uint> ExtraFields);

/// <summary>The scoreboard as MSG_PVP_LOG_DATA carries it (vmangos <c>BuildPvpLogDataPacket</c>, BattleGroundMgr.cpp:1060-1130).</summary>
public sealed record PvpLogSnapshot(bool Ended, BattlegroundWinner Winner, IReadOnlyList<PvpLogRow> Rows);
