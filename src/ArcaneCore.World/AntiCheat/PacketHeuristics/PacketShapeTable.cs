using ArcaneCore.Protocol;

namespace ArcaneCore.World.AntiCheat.PacketHeuristics;

/// <summary>
/// The exact payload length of the client messages the vendored wire oracle (tests/ThirdParty/WowWorldMessages, vanilla 1.12.1) declares
/// with a fixed layout AND the server handler agrees with: a row is kept only when the handler reads or enforces exactly that many bytes (or
/// documents the unused remainder), so a client the server accepts is never flagged. Where the server deliberately differs from the oracle
/// (the reputation messages: u32 list index, docs/integration/reputation.md) the row is the server length. Messages with a variable layout
/// (strings, arrays, packed GUIDs, movement blocks, optional parts) are not listed, so they are never shape-checked.
/// <para>
/// Why a table and not the oracle itself: the oracle project lives under tests/ThirdParty (a test dependency of this repository, 899 generated
/// source files, an async stream decoder that allocates a message object per packet), and the world assembly must not reference it. The table
/// is the fixed-size subset, generated from the oracle and pinned to it by PacketShapeTableTests (every row equals the oracle's header size minus
/// the 4 header bytes except the commented overrides and removals, and the server's own exact parsers accept the row length), so it cannot drift. Lookup is one array read.
/// </para>
/// <para>Thread affinity: immutable after type initialisation, any thread. No allocation.</para>
/// </summary>
public static class PacketShapeTable
{
    /// <summary>Body length of a message with no fixed layout.</summary>
    public const int NotFixed = -1;

    /// <summary>The fixed body length of <paramref name="opcode"/> in bytes, or <see cref="NotFixed"/> when its layout is variable or unknown.</summary>
    public static int FixedLength(WorldOpcode opcode)
    {
        uint code = (uint)opcode;
        return code < PacketHeuristicsOptions.OpcodeSpace ? Lengths[code] : NotFixed;
    }

    /// <summary>Every row of the table (tests).</summary>
    public static IReadOnlyList<(WorldOpcode Opcode, int Length)> Rows()
    {
        var rows = new List<(WorldOpcode, int)>();
        for (int i = 0; i < Lengths.Length; i++)
        {
            if (Lengths[i] != NotFixed)
            {
                rows.Add(((WorldOpcode)i, Lengths[i]));
            }
        }

        return rows;
    }

    private static short[] Build()
    {
        var lengths = new short[PacketHeuristicsOptions.OpcodeSpace];
        Array.Fill(lengths, (short)NotFixed);
        foreach ((WorldOpcode opcode, short length) in Source)
        {
            lengths[(ushort)opcode] = length;
        }

        return lengths;
    }

    private static readonly (WorldOpcode Opcode, short Length)[] Source =
    [
        (WorldOpcode.CmsgWorldTeleport, 24),
        (WorldOpcode.CmsgCharEnum, 0),
        (WorldOpcode.CmsgCharDelete, 8),
        (WorldOpcode.CmsgPlayerLogin, 8),
        (WorldOpcode.CmsgPlayerLogout, 0),
        (WorldOpcode.CmsgLogoutRequest, 0),
        (WorldOpcode.CmsgLogoutCancel, 0),
        (WorldOpcode.CmsgNameQuery, 8),
        (WorldOpcode.CmsgPetNameQuery, 12),
        (WorldOpcode.CmsgGuildQuery, 4),
        (WorldOpcode.CmsgItemQuerySingle, 12),
        (WorldOpcode.CmsgPageTextQuery, 4),
        (WorldOpcode.CmsgQuestQuery, 4),
        (WorldOpcode.CmsgGameobjectQuery, 12),
        (WorldOpcode.CmsgCreatureQuery, 12),
        (WorldOpcode.CmsgFriendList, 0),
        (WorldOpcode.CmsgDelFriend, 8),
        (WorldOpcode.CmsgDelIgnore, 8),
        (WorldOpcode.CmsgGroupAccept, 0),
        (WorldOpcode.CmsgGroupDecline, 0),
        (WorldOpcode.CmsgGroupUninviteGuid, 8),
        (WorldOpcode.CmsgGroupSetLeader, 8),
        (WorldOpcode.CmsgLootMethod, 16),
        (WorldOpcode.CmsgGroupDisband, 0),
        (WorldOpcode.CmsgGuildAccept, 0),
        (WorldOpcode.CmsgGuildDecline, 0),
        (WorldOpcode.CmsgGuildInfo, 0),
        (WorldOpcode.CmsgGuildRoster, 0),
        (WorldOpcode.CmsgGuildLeave, 0),
        (WorldOpcode.CmsgGuildDisband, 0),
        (WorldOpcode.CmsgOpenItem, 2),
        (WorldOpcode.CmsgReadItem, 2),
        (WorldOpcode.CmsgGameobjUse, 8),
        (WorldOpcode.CmsgAreatrigger, 4),
        (WorldOpcode.MsgMoveWorldportAck, 0),
        (WorldOpcode.CmsgMoveSetRawPosition, 16),
        (WorldOpcode.CmsgNextCinematicCamera, 0),
        (WorldOpcode.CmsgCompleteCinematic, 0),
        (WorldOpcode.CmsgTutorialFlag, 4),
        (WorldOpcode.CmsgTutorialClear, 0),
        (WorldOpcode.CmsgTutorialReset, 0),
        (WorldOpcode.CmsgStandstatechange, 4),
        (WorldOpcode.CmsgEmote, 4),
        (WorldOpcode.CmsgTextEmote, 16),
        (WorldOpcode.CmsgAutostoreLootItem, 1),
        (WorldOpcode.CmsgAutoequipItem, 2),
        (WorldOpcode.CmsgAutostoreBagItem, 3),
        (WorldOpcode.CmsgSwapItem, 4),
        (WorldOpcode.CmsgSwapInvItem, 2),
        (WorldOpcode.CmsgSplitItem, 5),
        (WorldOpcode.CmsgAutoequipItemSlot, 9),
        (WorldOpcode.CmsgDestroyitem, 6),
        (WorldOpcode.CmsgInspect, 8),
        (WorldOpcode.CmsgInitiateTrade, 8),
        (WorldOpcode.CmsgBeginTrade, 0),
        (WorldOpcode.CmsgBusyTrade, 0),
        (WorldOpcode.CmsgIgnoreTrade, 0),
        (WorldOpcode.CmsgUnacceptTrade, 0),
        (WorldOpcode.CmsgCancelTrade, 0),
        (WorldOpcode.CmsgSetTradeItem, 3),
        (WorldOpcode.CmsgClearTradeItem, 1),
        (WorldOpcode.CmsgSetTradeGold, 4),
        (WorldOpcode.CmsgSetFactionAtwar, 5),
        (WorldOpcode.CmsgSetActionButton, 5),
        (WorldOpcode.CmsgCancelCast, 4),
        (WorldOpcode.CmsgCancelAura, 4),
        (WorldOpcode.CmsgSetSelection, 8),
        (WorldOpcode.CmsgAttackswing, 8),
        (WorldOpcode.CmsgAttackstop, 0),
        (WorldOpcode.CmsgRepopRequest, 0),
        (WorldOpcode.CmsgResurrectResponse, 9),
        (WorldOpcode.CmsgLoot, 8),
        (WorldOpcode.CmsgLootMoney, 0),
        (WorldOpcode.CmsgLootRelease, 8),
        (WorldOpcode.CmsgDuelAccepted, 8),
        (WorldOpcode.CmsgDuelCancelled, 8),
        (WorldOpcode.CmsgMountspecialAnim, 0),
        (WorldOpcode.CmsgPetAction, 20),
        (WorldOpcode.CmsgPetAbandon, 8),
        (WorldOpcode.CmsgGossipHello, 8),
        (WorldOpcode.CmsgNpcTextQuery, 12),
        (WorldOpcode.CmsgQuestgiverStatusQuery, 8),
        (WorldOpcode.CmsgQuestgiverHello, 8),
        (WorldOpcode.CmsgQuestgiverQueryQuest, 12),
        (WorldOpcode.CmsgQuestgiverQuestAutolaunch, 0),
        (WorldOpcode.CmsgQuestgiverAcceptQuest, 12),
        (WorldOpcode.CmsgQuestgiverCompleteQuest, 12),
        (WorldOpcode.CmsgQuestgiverRequestReward, 12),
        (WorldOpcode.CmsgQuestgiverChooseReward, 16),
        (WorldOpcode.CmsgQuestgiverCancel, 0),
        (WorldOpcode.CmsgQuestlogSwapQuest, 2),
        (WorldOpcode.CmsgQuestlogRemoveQuest, 1),
        (WorldOpcode.CmsgQuestConfirmAccept, 4),
        (WorldOpcode.CmsgPushquesttoparty, 4),
        (WorldOpcode.CmsgListInventory, 8),
        (WorldOpcode.CmsgSellItem, 17),
        (WorldOpcode.CmsgBuyItem, 14),
        (WorldOpcode.CmsgBuyItemInSlot, 22),
        (WorldOpcode.CmsgTaxinodeStatusQuery, 8),
        (WorldOpcode.CmsgTaxiqueryavailablenodes, 8),
        (WorldOpcode.CmsgActivatetaxi, 16),
        (WorldOpcode.CmsgTrainerList, 8),
        (WorldOpcode.CmsgTrainerBuySpell, 12),
        (WorldOpcode.CmsgBinderActivate, 8),
        (WorldOpcode.CmsgBankerActivate, 8),
        (WorldOpcode.CmsgBuyBankSlot, 8),
        (WorldOpcode.CmsgPetitionShowlist, 8),
        (WorldOpcode.CmsgPetitionShowSignatures, 8),
        (WorldOpcode.CmsgPetitionSign, 9),
        (WorldOpcode.MsgPetitionDecline, 8),
        (WorldOpcode.CmsgOfferPetition, 16),
        (WorldOpcode.CmsgTurnInPetition, 8),
        (WorldOpcode.CmsgPetitionQuery, 12),
        (WorldOpcode.CmsgPlayedTime, 0),
        (WorldOpcode.CmsgQueryTime, 0),
        (WorldOpcode.CmsgReclaimCorpse, 8),
        (WorldOpcode.CmsgWrapItem, 4),
        (WorldOpcode.MsgMinimapPing, 8),
        (WorldOpcode.CmsgPing, 8),
        (WorldOpcode.CmsgSetsheathed, 4),
        (WorldOpcode.MsgSaveGuildEmblem, 28),
        (WorldOpcode.MsgTabardvendorActivate, 8),
        (WorldOpcode.CmsgZoneupdate, 4),
        (WorldOpcode.MsgRandomRoll, 8),
        (WorldOpcode.CmsgUnlearnSkill, 4),
        (WorldOpcode.CmsgRequestAccountData, 4),
        (WorldOpcode.CmsgGmticketGetticket, 0),
        (WorldOpcode.MsgCorpseQuery, 0),
        (WorldOpcode.CmsgGmticketDeleteticket, 0),
        (WorldOpcode.CmsgGmticketSystemstatus, 0),
        (WorldOpcode.CmsgSpiritHealerActivate, 8),
        (WorldOpcode.CmsgChatIgnored, 8),
        (WorldOpcode.CmsgGuildDelRank, 0),
        (WorldOpcode.CmsgGetMailList, 8),
        (WorldOpcode.CmsgBattlefieldList, 4),
        (WorldOpcode.CmsgBattlefieldJoin, 4),
        (WorldOpcode.CmsgMailTakeMoney, 12),
        (WorldOpcode.CmsgMailTakeItem, 12),
        (WorldOpcode.CmsgMailMarkAsRead, 12),
        (WorldOpcode.CmsgMailReturnToSender, 12),
        (WorldOpcode.CmsgMailDelete, 12),
        (WorldOpcode.CmsgLearnTalent, 8),
        (WorldOpcode.MsgAuctionHello, 8),
        (WorldOpcode.CmsgAuctionSellItem, 28),
        (WorldOpcode.CmsgAuctionRemoveItem, 12),
        (WorldOpcode.CmsgAuctionListOwnerItems, 12),
        (WorldOpcode.CmsgAuctionPlaceBid, 16),
        (WorldOpcode.CmsgSetAmmo, 4),
        (WorldOpcode.CmsgSetActiveMover, 8),
        (WorldOpcode.CmsgPetCancelAura, 12),
        (WorldOpcode.CmsgCancelAutoRepeatSpell, 0),
        (WorldOpcode.MsgListStabledPets, 8),
        (WorldOpcode.CmsgStablePet, 8),
        (WorldOpcode.CmsgUnstablePet, 12),
        (WorldOpcode.CmsgBuyStableSlot, 8),
        (WorldOpcode.CmsgStableSwapPet, 12),
        (WorldOpcode.MsgQuestPushResult, 9),
        (WorldOpcode.CmsgRequestPetInfo, 0),
        (WorldOpcode.CmsgFarSight, 1),
        (WorldOpcode.CmsgRequestPartyMemberStats, 8),
        (WorldOpcode.CmsgAutostoreBankItem, 2),
        (WorldOpcode.CmsgAutobankItem, 2),
        (WorldOpcode.MsgQueryNextMailTime, 0),
        (WorldOpcode.CmsgGroupRaidConvert, 0),
        (WorldOpcode.CmsgGroupAssistantLeader, 9),
        (WorldOpcode.CmsgBuybackItem, 12),
        (WorldOpcode.CmsgMeetingstoneJoin, 8),
        (WorldOpcode.CmsgMeetingstoneLeave, 0),
        (WorldOpcode.CmsgMeetingstoneInfo, 0),
        (WorldOpcode.CmsgCancelGrowthAura, 0),
        (WorldOpcode.CmsgLootRoll, 13),
        (WorldOpcode.CmsgLootMasterGive, 17),
        (WorldOpcode.CmsgRepairItem, 16),
        (WorldOpcode.MsgTalentWipeConfirm, 8),
        (WorldOpcode.CmsgSummonResponse, 8),
        (WorldOpcode.CmsgSelfRes, 0),
        (WorldOpcode.CmsgToggleHelm, 0),
        (WorldOpcode.CmsgToggleCloak, 0),
        (WorldOpcode.CmsgSetActionbarToggles, 1),
        (WorldOpcode.CmsgItemNameQuery, 12),
        (WorldOpcode.CmsgRequestRaidInfo, 0),
        (WorldOpcode.CmsgMoveTimeSkipped, 12),
        (WorldOpcode.CmsgBattlefieldStatus, 0),
        (WorldOpcode.CmsgBattlefieldPort, 5),
        (WorldOpcode.MsgInspectHonorStats, 8),
        (WorldOpcode.CmsgBattlemasterHello, 8),
        (WorldOpcode.MsgPvpLogData, 0),
        (WorldOpcode.CmsgLeaveBattlefield, 4),
        (WorldOpcode.CmsgAreaSpiritHealerQuery, 8),
        (WorldOpcode.CmsgAreaSpiritHealerQueue, 8),
        (WorldOpcode.MsgBattlegroundPlayerPositions, 0),
        (WorldOpcode.CmsgPetStopAttack, 8),
        (WorldOpcode.CmsgBattlemasterJoin, 17),
        (WorldOpcode.CmsgPetUnlearn, 8),
        (WorldOpcode.CmsgPetSpellAutocast, 13),
        (WorldOpcode.CmsgSetFactionInactive, 5),
        (WorldOpcode.CmsgSetWatchedFaction, 4),
        (WorldOpcode.CmsgResetInstances, 0),
    ];

    private static readonly short[] Lengths = Build();
}
