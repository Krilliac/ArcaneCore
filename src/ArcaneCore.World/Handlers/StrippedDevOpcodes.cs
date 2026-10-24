using System.Collections.Frozen;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Handlers;

/// <summary>
/// The stripped developer, GM, cheat and debug client opcodes: the 1.12.1 release client has no UI or Lua path that sends them, and vmangos
/// drops each as <c>INVALID_PACKET(..., UnhandleReason::Unhandled)</c>. They are registered as one explicit table (<see cref="OpcodeTable.OnStripped"/>)
/// so the session recognises them: the payload is never read, nothing is queued for the world thread and nothing is sent back; a player's
/// packet is a <c>packet/dev-opcode</c> finding, a staff member's is audited (<c>WorldSession.DropDevOpcode</c>), and none of them counts as an
/// unhandled opcode. Opcodes that vmangos really handles (CMSG_WORLD_TELEPORT, CMSG_TELEPORT_TO_UNIT, CMSG_WHOIS, MSG_MOVE_TELEPORT_CHEAT,
/// CMSG_MOVE_SET_RAW_POSITION, <see cref="GmClientHandlers"/>) and doubtful or obsolete names stay out: a misclassified real opcode would flag honest players.
/// The list is the lane inventory, each row with its wire value and vmangos <c>Opcodes.cpp</c> line (docs/reference/opcodes.md).
/// </summary>
public sealed class StrippedDevOpcodes : IOpcodeHandlerGroup
{
    private static readonly WorldOpcode[] Opcodes =
    [
        WorldOpcode.CmsgBootme, // CMSG_BOOTME 0x0001, vmangos Opcodes.cpp:72
        WorldOpcode.CmsgDblookup, // CMSG_DBLOOKUP 0x0002, vmangos Opcodes.cpp:73
        WorldOpcode.CmsgQueryObjectPosition, // CMSG_QUERY_OBJECT_POSITION 0x0004, vmangos Opcodes.cpp:75
        WorldOpcode.CmsgQueryObjectRotation, // CMSG_QUERY_OBJECT_ROTATION 0x0006, vmangos Opcodes.cpp:77
        WorldOpcode.CmsgZoneMap, // CMSG_ZONE_MAP 0x000A, vmangos Opcodes.cpp:81
        WorldOpcode.CmsgDebugChangecellzone, // CMSG_DEBUG_CHANGECELLZONE 0x000C, vmangos Opcodes.cpp:83
        WorldOpcode.CmsgRecharge, // CMSG_RECHARGE 0x000F, vmangos Opcodes.cpp:86
        WorldOpcode.CmsgLearnSpell, // CMSG_LEARN_SPELL 0x0010, vmangos Opcodes.cpp:87
        WorldOpcode.CmsgCreatemonster, // CMSG_CREATEMONSTER 0x0011, vmangos Opcodes.cpp:88
        WorldOpcode.CmsgDestroymonster, // CMSG_DESTROYMONSTER 0x0012, vmangos Opcodes.cpp:89
        WorldOpcode.CmsgCreateitem, // CMSG_CREATEITEM 0x0013, vmangos Opcodes.cpp:90
        WorldOpcode.CmsgCreategameobject, // CMSG_CREATEGAMEOBJECT 0x0014, vmangos Opcodes.cpp:91
        WorldOpcode.CmsgMakemonsterattackguid, // CMSG_MAKEMONSTERATTACKGUID 0x0016, vmangos Opcodes.cpp:95
        WorldOpcode.CmsgForceaction, // CMSG_FORCEACTION 0x0018, vmangos Opcodes.cpp:99
        WorldOpcode.CmsgForceactiononother, // CMSG_FORCEACTIONONOTHER 0x0019, vmangos Opcodes.cpp:100
        WorldOpcode.CmsgForceactionshow, // CMSG_FORCEACTIONSHOW 0x001A, vmangos Opcodes.cpp:101
        WorldOpcode.CmsgPetgodmode, // CMSG_PETGODMODE 0x001C, vmangos Opcodes.cpp:104
        WorldOpcode.CmsgWeatherSpeedCheat, // CMSG_WEATHER_SPEED_CHEAT 0x001F, vmangos Opcodes.cpp:109
        WorldOpcode.CmsgUndressplayer, // CMSG_UNDRESSPLAYER 0x0020, vmangos Opcodes.cpp:111
        WorldOpcode.CmsgBeastmaster, // CMSG_BEASTMASTER 0x0021, vmangos Opcodes.cpp:112
        WorldOpcode.CmsgGodmode, // CMSG_GODMODE 0x0022, vmangos Opcodes.cpp:113
        WorldOpcode.CmsgCheatSetmoney, // CMSG_CHEAT_SETMONEY 0x0024, vmangos Opcodes.cpp:115
        WorldOpcode.CmsgLevelCheat, // CMSG_LEVEL_CHEAT 0x0025, vmangos Opcodes.cpp:116
        WorldOpcode.CmsgPetLevelCheat, // CMSG_PET_LEVEL_CHEAT 0x0026, vmangos Opcodes.cpp:117
        WorldOpcode.CmsgSetWorldstate, // CMSG_SET_WORLDSTATE 0x0027, vmangos Opcodes.cpp:119
        WorldOpcode.CmsgCooldownCheat, // CMSG_COOLDOWN_CHEAT 0x0028, vmangos Opcodes.cpp:121
        WorldOpcode.CmsgUseSkillCheat, // CMSG_USE_SKILL_CHEAT 0x0029, vmangos Opcodes.cpp:122
        WorldOpcode.CmsgFlagQuest, // CMSG_FLAG_QUEST 0x002A, vmangos Opcodes.cpp:123
        WorldOpcode.CmsgFlagQuestFinish, // CMSG_FLAG_QUEST_FINISH 0x002B, vmangos Opcodes.cpp:124
        WorldOpcode.CmsgClearQuest, // CMSG_CLEAR_QUEST 0x002C, vmangos Opcodes.cpp:125
        WorldOpcode.CmsgSendEvent, // CMSG_SEND_EVENT 0x002D, vmangos Opcodes.cpp:126
        WorldOpcode.CmsgDebugAistate, // CMSG_DEBUG_AISTATE 0x002E, vmangos Opcodes.cpp:127
        WorldOpcode.CmsgDisablePvpCheat, // CMSG_DISABLE_PVP_CHEAT 0x0030, vmangos Opcodes.cpp:129
        WorldOpcode.CmsgAdvanceSpawnTime, // CMSG_ADVANCE_SPAWN_TIME 0x0031, vmangos Opcodes.cpp:130
        WorldOpcode.CmsgGametimeSet, // CMSG_GAMETIME_SET 0x0044, vmangos Opcodes.cpp:149
        WorldOpcode.CmsgGamespeedSet, // CMSG_GAMESPEED_SET 0x0046, vmangos Opcodes.cpp:151
        WorldOpcode.CmsgServertime, // CMSG_SERVERTIME 0x0048, vmangos Opcodes.cpp:153
        WorldOpcode.MsgMoveToggleLogging, // MSG_MOVE_TOGGLE_LOGGING 0x00C4, vmangos Opcodes.cpp:277
        WorldOpcode.MsgMoveToggleFallLogging, // MSG_MOVE_TOGGLE_FALL_LOGGING 0x00C8, vmangos Opcodes.cpp:281
        WorldOpcode.MsgMoveSetRunSpeedCheat, // MSG_MOVE_SET_RUN_SPEED_CHEAT 0x00CC, vmangos Opcodes.cpp:285
        WorldOpcode.MsgMoveSetRunBackSpeedCheat, // MSG_MOVE_SET_RUN_BACK_SPEED_CHEAT 0x00CE, vmangos Opcodes.cpp:287
        WorldOpcode.MsgMoveSetWalkSpeedCheat, // MSG_MOVE_SET_WALK_SPEED_CHEAT 0x00D0, vmangos Opcodes.cpp:289
        WorldOpcode.MsgMoveSetSwimSpeedCheat, // MSG_MOVE_SET_SWIM_SPEED_CHEAT 0x00D2, vmangos Opcodes.cpp:291
        WorldOpcode.MsgMoveSetSwimBackSpeedCheat, // MSG_MOVE_SET_SWIM_BACK_SPEED_CHEAT 0x00D4, vmangos Opcodes.cpp:293
        WorldOpcode.MsgMoveSetAllSpeedCheat, // MSG_MOVE_SET_ALL_SPEED_CHEAT 0x00D6, vmangos Opcodes.cpp:295
        WorldOpcode.MsgMoveSetTurnRateCheat, // MSG_MOVE_SET_TURN_RATE_CHEAT 0x00D7, vmangos Opcodes.cpp:296
        WorldOpcode.MsgMoveToggleCollisionCheat, // MSG_MOVE_TOGGLE_COLLISION_CHEAT 0x00D9, vmangos Opcodes.cpp:298
        WorldOpcode.CmsgTriggerCinematicCheat, // CMSG_TRIGGER_CINEMATIC_CHEAT 0x00F8, vmangos Opcodes.cpp:329
        WorldOpcode.CmsgSetFactionCheat, // CMSG_SET_FACTION_CHEAT 0x0126, vmangos Opcodes.cpp:375
        WorldOpcode.CmsgSavePlayer, // CMSG_SAVE_PLAYER 0x0153, vmangos Opcodes.cpp:420
        WorldOpcode.CmsgSetdeathbindpoint, // CMSG_SETDEATHBINDPOINT 0x0154, vmangos Opcodes.cpp:421
        WorldOpcode.CmsgTaxiclearallnodes, // CMSG_TAXICLEARALLNODES 0x01A6, vmangos Opcodes.cpp:507
        WorldOpcode.CmsgTaxienableallnodes, // CMSG_TAXIENABLEALLNODES 0x01A7, vmangos Opcodes.cpp:508
        WorldOpcode.CmsgTaxishownodes, // CMSG_TAXISHOWNODES 0x01A8, vmangos Opcodes.cpp:509
        WorldOpcode.CmsgSetSkillCheat, // CMSG_SET_SKILL_CHEAT 0x01D8, vmangos Opcodes.cpp:559
        WorldOpcode.CmsgGhost, // CMSG_GHOST 0x01E5, vmangos Opcodes.cpp:572
        WorldOpcode.CmsgGmInvis, // CMSG_GM_INVIS 0x01E6, vmangos Opcodes.cpp:573
        WorldOpcode.MsgGmBindOther, // MSG_GM_BIND_OTHER 0x01E8, vmangos Opcodes.cpp:575
        WorldOpcode.MsgGmSummon, // MSG_GM_SUMMON 0x01E9, vmangos Opcodes.cpp:576
        WorldOpcode.MsgGmShowlabel, // MSG_GM_SHOWLABEL 0x01EF, vmangos Opcodes.cpp:582
        WorldOpcode.CmsgGmSetSecurityGroup, // CMSG_GM_SET_SECURITY_GROUP 0x01F9, vmangos Opcodes.cpp:596
        WorldOpcode.CmsgGmNuke, // CMSG_GM_NUKE 0x01FA, vmangos Opcodes.cpp:597
        WorldOpcode.CmsgRwhois, // CMSG_RWHOIS 0x01FD, vmangos Opcodes.cpp:600
        WorldOpcode.CmsgUnlearnSpell, // CMSG_UNLEARN_SPELL 0x0201, vmangos Opcodes.cpp:604
        WorldOpcode.CmsgDecharge, // CMSG_DECHARGE 0x0204, vmangos Opcodes.cpp:607
        WorldOpcode.CmsgGmTeach, // CMSG_GM_TEACH 0x020F, vmangos Opcodes.cpp:618
        WorldOpcode.CmsgGmCreateItemTarget, // CMSG_GM_CREATE_ITEM_TARGET 0x0210, vmangos Opcodes.cpp:619
        WorldOpcode.CmsgUnlearnTalents, // CMSG_UNLEARN_TALENTS 0x0213, vmangos Opcodes.cpp:622
        WorldOpcode.CmsgSetStatCheat, // CMSG_SET_STAT_CHEAT 0x021D, vmangos Opcodes.cpp:633
        WorldOpcode.CmsgXpCheat, // CMSG_XP_CHEAT 0x0221, vmangos Opcodes.cpp:637
        WorldOpcode.CmsgCharacterPointCheat, // CMSG_CHARACTER_POINT_CHEAT 0x0223, vmangos Opcodes.cpp:639
        WorldOpcode.CmsgGmVision, // CMSG_GM_VISION 0x0226, vmangos Opcodes.cpp:642
        WorldOpcode.CmsgServerCommand, // CMSG_SERVER_COMMAND 0x0227, vmangos Opcodes.cpp:643
        WorldOpcode.CmsgGmSilence, // CMSG_GM_SILENCE 0x0228, vmangos Opcodes.cpp:644
        WorldOpcode.CmsgGmRevealto, // CMSG_GM_REVEALTO 0x0229, vmangos Opcodes.cpp:645
        WorldOpcode.CmsgGmResurrect, // CMSG_GM_RESURRECT 0x022A, vmangos Opcodes.cpp:646
        WorldOpcode.CmsgGmSummonmob, // CMSG_GM_SUMMONMOB 0x022B, vmangos Opcodes.cpp:647
        WorldOpcode.CmsgGmMovecorpse, // CMSG_GM_MOVECORPSE 0x022C, vmangos Opcodes.cpp:648
        WorldOpcode.CmsgGmFreeze, // CMSG_GM_FREEZE 0x022D, vmangos Opcodes.cpp:649
        WorldOpcode.CmsgGmUberinvis, // CMSG_GM_UBERINVIS 0x022E, vmangos Opcodes.cpp:650
        WorldOpcode.CmsgGmRequestPlayerInfo, // CMSG_GM_REQUEST_PLAYER_INFO 0x022F, vmangos Opcodes.cpp:651
        WorldOpcode.CmsgClearExploration, // CMSG_CLEAR_EXPLORATION 0x0237, vmangos Opcodes.cpp:659
        WorldOpcode.CmsgTaxiclearnode, // CMSG_TAXICLEARNODE 0x0241, vmangos Opcodes.cpp:669
        WorldOpcode.CmsgTaxienablenode, // CMSG_TAXIENABLENODE 0x0242, vmangos Opcodes.cpp:670
        WorldOpcode.CmsgPlayerAiCheat, // CMSG_PLAYER_AI_CHEAT 0x026C, vmangos Opcodes.cpp:712
        WorldOpcode.MsgGmAccountOnline, // MSG_GM_ACCOUNT_ONLINE 0x026E, vmangos Opcodes.cpp:714
        WorldOpcode.CmsgEnableDamageLog, // CMSG_ENABLE_DAMAGE_LOG 0x027D, vmangos Opcodes.cpp:729
        WorldOpcode.CmsgResetFactionCheat, // CMSG_RESET_FACTION_CHEAT 0x0281, vmangos Opcodes.cpp:737
        WorldOpcode.CmsgSetDurabilityCheat, // CMSG_SET_DURABILITY_CHEAT 0x0287, vmangos Opcodes.cpp:743
        WorldOpcode.CmsgSetPvpRankCheat, // CMSG_SET_PVP_RANK_CHEAT 0x0288, vmangos Opcodes.cpp:744
        WorldOpcode.CmsgAddPvpMedalCheat, // CMSG_ADD_PVP_MEDAL_CHEAT 0x0289, vmangos Opcodes.cpp:745
        WorldOpcode.CmsgDelPvpMedalCheat, // CMSG_DEL_PVP_MEDAL_CHEAT 0x028A, vmangos Opcodes.cpp:746
        WorldOpcode.CmsgSetPvpTitle, // CMSG_SET_PVP_TITLE 0x028B, vmangos Opcodes.cpp:747
        WorldOpcode.CmsgMeetingstoneCheat, // CMSG_MEETINGSTONE_CHEAT 0x0294, vmangos Opcodes.cpp:756
        WorldOpcode.CmsgGmticketsystemToggle, // CMSG_GMTICKETSYSTEM_TOGGLE 0x029A, vmangos Opcodes.cpp:762
        WorldOpcode.MsgMoveToggleGravityCheat, // MSG_MOVE_TOGGLE_GRAVITY_CHEAT 0x02AD, vmangos Opcodes.cpp:781
        WorldOpcode.CmsgServerBroadcast, // CMSG_SERVER_BROADCAST 0x02B2, vmangos Opcodes.cpp:786
        WorldOpcode.CmsgRunScript, // CMSG_RUN_SCRIPT 0x02B5, vmangos Opcodes.cpp:789
        WorldOpcode.CmsgSetExploration, // CMSG_SET_EXPLORATION 0x02BE, vmangos Opcodes.cpp:798
        WorldOpcode.CmsgMoveStartSwimCheat, // CMSG_MOVE_START_SWIM_CHEAT 0x02D8, vmangos Opcodes.cpp:825
        WorldOpcode.CmsgMoveStopSwimCheat, // CMSG_MOVE_STOP_SWIM_CHEAT 0x02D9, vmangos Opcodes.cpp:826
        WorldOpcode.CmsgGmUnteach, // CMSG_GM_UNTEACH 0x02E5, vmangos Opcodes.cpp:838
        WorldOpcode.CmsgGmNukeAccount, // CMSG_GM_NUKE_ACCOUNT 0x030F, vmangos Opcodes.cpp:894
        WorldOpcode.MsgGmDestroyCorpse, // MSG_GM_DESTROY_CORPSE 0x0310, vmangos Opcodes.cpp:895
        WorldOpcode.CmsgGmDestroyOnlineCorpse, // CMSG_GM_DESTROY_ONLINE_CORPSE 0x0311, vmangos Opcodes.cpp:896
        WorldOpcode.CmsgDebugActionsStart, // CMSG_DEBUG_ACTIONS_START 0x0315, vmangos Opcodes.cpp:900
        WorldOpcode.CmsgDebugActionsStop, // CMSG_DEBUG_ACTIONS_STOP 0x0316, vmangos Opcodes.cpp:901
        WorldOpcode.CmsgSetExplorationAll, // CMSG_SET_EXPLORATION_ALL 0x031B, vmangos Opcodes.cpp:906
        WorldOpcode.CmsgIgnoreKnockbackCheat, // CMSG_IGNORE_KNOCKBACK_CHEAT 0x032C, vmangos Opcodes.cpp:925
    ];

    /// <summary>Every stripped dev opcode.</summary>
    public static FrozenSet<WorldOpcode> All { get; } = Opcodes.ToFrozenSet();

    public void Register(OpcodeTable table)
    {
        foreach (WorldOpcode opcode in Opcodes)
        {
            table.OnStripped(opcode);
        }
    }
}
