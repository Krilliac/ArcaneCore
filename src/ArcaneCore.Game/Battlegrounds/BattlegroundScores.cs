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

    // Broadcast texts of Arathi Basin and Alterac Valley (vmangos BattleGroundDefines.h:50-84).
    public const uint AvAllianceWins = 7335;
    public const uint AvHordeWins = 7336;
    public const uint AvStartOneMinute = 10638;
    public const uint AvStartHalfMinute = 10639;
    public const uint AvHasBegun = 10640;
    public const uint AbAllianceWins = 10633;
    public const uint AbHordeWins = 10634;
    public const uint AbStartOneMinute = 10477;
    public const uint AbStartHalfMinute = 10478;
    public const uint AbHasBegun = 10479;
    public const uint AbAllianceNearVictory = 10598;
    public const uint AbHordeNearVictory = 10599;

    // mangos_string ids (vmangos Language.h:689-700, 759-793): the node texts and their arguments.
    public const uint LangBgAlliance = 650;
    public const uint LangBgHorde = 651;
    public const uint LangAbNodeStables = 652;
    public const uint LangAbNodeBlacksmith = 653;
    public const uint LangAbNodeFarm = 654;
    public const uint LangAbNodeLumberMill = 655;
    public const uint LangAbNodeGoldMine = 656;
    public const uint LangAbNodeTaken = 657;
    public const uint LangAbNodeDefended = 658;
    public const uint LangAbNodeAssaulted = 659;
    public const uint LangAbNodeClaimed = 660;
    public const uint LangAvTowerTaken = 759;
    public const uint LangAvTowerAssaulted = 760;
    public const uint LangAvTowerDefended = 761;
    public const uint LangAvGraveTaken = 762;
    public const uint LangAvGraveDefended = 763;
    public const uint LangAvGraveAssaulted = 764;
    public const uint LangAvMineTaken = 765;
    public const uint LangAvMineNorth = 766;
    public const uint LangAvMineSouth = 767;
    public const uint LangAvNodeGraveStormAid = 768;
    public const uint LangAvNodeTowerDunSouth = 769;
    public const uint LangAvNodeTowerDunNorth = 770;
    public const uint LangAvNodeGraveStormpike = 771;
    public const uint LangAvNodeTowerIcewing = 772;
    public const uint LangAvNodeGraveStone = 773;
    public const uint LangAvNodeTowerStone = 774;
    public const uint LangAvNodeGraveSnow = 775;
    public const uint LangAvNodeTowerIce = 776;
    public const uint LangAvNodeGraveIce = 777;
    public const uint LangAvNodeTowerPoint = 778;
    public const uint LangAvNodeGraveFrost = 779;
    public const uint LangAvNodeTowerFrostEast = 780;
    public const uint LangAvNodeTowerFrostWest = 781;
    public const uint LangAvNodeGraveFrostHut = 782;
    public const uint LangAvHordeGeneralDead = 789;
    public const uint LangAvAllianceGeneralDead = 790;
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

/// <summary>The Arathi Basin row (vmangos <c>BattleGroundABScore</c>, BattleGroundAB.h:184-191; BattleGroundMgr.cpp:1113-1119): bases assaulted and defended.</summary>
public sealed class AbScore : BattlegroundScore
{
    public uint BasesAssaulted { get; set; }

    public uint BasesDefended { get; set; }

    public override IReadOnlyList<uint> ExtraFields => [BasesAssaulted, BasesDefended];
}

/// <summary>
/// The Alterac Valley row (vmangos <c>BattleGroundAVScore</c>, BattleGroundAV.h:519-531; BattleGroundMgr.cpp:1093-1103): seven columns, the
/// last two (lieutenants, secondary NPCs) are never counted by vmangos either.
/// </summary>
public sealed class AvScore : BattlegroundScore
{
    public uint GraveyardsAssaulted { get; set; }

    public uint GraveyardsDefended { get; set; }

    public uint TowersAssaulted { get; set; }

    public uint TowersDefended { get; set; }

    public uint SecondaryObjectives { get; set; }

    public uint LieutenantCount { get; set; }

    public uint SecondaryNpc { get; set; }

    public override IReadOnlyList<uint> ExtraFields => [GraveyardsAssaulted, GraveyardsDefended, TowersAssaulted, TowersDefended, SecondaryObjectives, LieutenantCount, SecondaryNpc];
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
