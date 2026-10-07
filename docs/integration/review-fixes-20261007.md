# Review fixes 1-126: lane integration (2026-10-07)

Base: `claude/integrate-20261007` at `dd158e71`. Twelve lanes worked through review findings 1-126 in parallel worktrees. Each lane
branch was merged here with `git merge --no-ff`, one at a time, in the order below. Every lane had a result; none was skipped. The
vmangos reference used by the lanes and their reviewers is `D:/refs/vmangos`.

Status meanings: **fixed** means behaviour changed, with a test that failed before the change (RED) and passes now (GREEN), unless the
row says otherwise. **already_fixed** means the base already had the fix (the commit named is in the base). **not_a_bug** means the
finding contradicts vmangos or cannot happen. A guard test is named where one was added. **deferred** means the fix needs a
subsystem that does not exist yet.

Totals: 91 fixed, 9 already_fixed, 24 not_a_bug, 2 deferred (126 findings). Four fixed rows are partial or carry a deliberate
limit: 32, 64, 84 and 96.

## Findings

Commits are lane commits, reachable from the merge commit of their lane (listed under "Branches merged").

| # | Status | Lane | Commit | Tests |
|---|---|---|---|---|
| 1 | already_fixed | combat-melee | 2aa76cb6 (in base) | CombatSkillsTests.OffhandHitTableUsesLearnedSkillEvenWithTheStatsSource |
| 2 | already_fixed | combat-melee | 2aa76cb6 (in base) | CombatMeleeAbsorbTests: WhiteSwingHitTableReadsTheAttackersHitAura, WhiteSwingAbsorbsAfterBlockAndReportsTheRemainingDamage, BlockValueIsRemovedBeforeTheAbsorbShield |
| 3 | already_fixed | combat-melee | in base (MagicHitChance.Finish) | none dedicated; Game suite green |
| 4 | already_fixed | spell-auras | be3e7aba (in base) | InvisibilityTests.AnInvisiblePetOrCharmedUnit_IsAlwaysVisibleToItsOwnerOrCharmer_ButHiddenFromEveryoneElse |
| 5 | already_fixed | spell-auras | 51a15d8f (in base) | AuraInterruptEngineTests: DamageBreak_WithoutAProcEngine_SparesWyvernStingAndProwl_ButStillBreaksOtherProcFlagAuras, DamageBreakExemptSpells_ListEveryRankOfWyvernStingAndProwl |
| 6 | already_fixed | gameobjects-items | 6ddb2139 (in base) | World ItemWorldTests.InvalidInventoryDestination_ReportsSlotError_WithoutMovingItems |
| 7 | already_fixed | loot-group | 20b1a61e (in base); a3513075 refreshes two stale texts | LootServiceTests: FirstDamageTagsCreature_AndASecondPlayersKillingBlowDoesNotStealItsLoot, EvadingCreature_ClearsItsLootTap, DisbandedTapGroup_FallsBackToTheOriginalTagger, MoneySplit_* |
| 8 | not_a_bug | social-bans | none | none (vmangos exempts addon chat; the opt-in ChatOptions.AddonMuteAndFloodControl already exists) |
| 9 | not_a_bug | quests-npc-taxi | none | existing QuestIntegrationTests.ExpiredTimedQuest_FailsDuringLoad_AndSavesClearedTimer, QuestShareTests.ASharedTimedQuest_TakesTheSharersRemainingTime |
| 10 | fixed | quests-npc-taxi | c8f6ef37 | NpcTravelServiceTests: Flight_DeathOnALaterHopReturnsToThatHopsDepartureNode_NotTheOrigin, Flight_RefusedLaterLegFare_PutsThePlayerDownAtTheNodeJustReached_NotInTheAir |
| 11 | not_a_bug | misc-systems | none | none (vmangos Group.cpp:2337 truncates the same way) |
| 12 | not_a_bug | spell-cast | none | none (vmangos Spell.cpp:7015-7023 has the same divisor and clamp) |
| 13 | fixed | loot-group | ea80b639 | GroupManagerTests.LootMethod_AThresholdOutsideUncommonToArtifact_IsRefused (5 cases) |
| 14 | fixed | economy | e012f082 | AuctionFormulaTests: Proceeds_ACutAboveBidPlusDepositPaysNothingInsteadOfWrapping, Proceeds_AreBidPlusDepositMinusCut_CappedAtTheMoneyLimit |
| 15 | fixed | instances-death | a62bdf1a, 6f46df49 | InstanceResetSafetyTests: SoloReset_OfAnInstanceSomeoneElseIsInside_IsRefused_AndKeepsTheSave, GroupJoin_OfAPlayerBoundToAnOccupiedInstance_DropsOnlyItsOwnBind (refusing an occupied solo reset is a deliberate deviation from vmangos) |
| 16 | fixed | instances-death | a62bdf1a | InstanceResetSafetyTests.GlobalRaidReset_WithAPlayerStillInside_DeletesTheSaveOnlyWhenTheMapUnloads; updated GlobalRaidReset_WarnsThenUnbindsEveryone_AndSendsPlayersHome |
| 17 | not_a_bug | misc-systems | none | existing BattlegroundManagerTests.GroupErrors (faction case) |
| 18 | fixed | spell-cast | 47ceec60 | ChannelUpdateTests: Channel_EndsWhenItsTargetDies_AndTheObjectStillExists, Channel_ThatNeedsNoTarget_KeepsRunning_WhenTheTargetDies |
| 19 | not_a_bug | spell-cast | none | none (both range boundaries match vmangos) |
| 20 | not_a_bug | quests-npc-taxi | none | none (vmangos sends the same two's-complement bytes) |
| 21 | not_a_bug | spell-auras | 3a502bc4 (tests only); adapted in bb2ff9f7 | ChannelTriggerTests: AChannelTargetThatDied_ReceivesNoFurtherTriggeredSpells, ATriggeredSpellThatMayTargetTheDead_StillFiresAtADeadChannelTarget |
| 22 | not_a_bug | creature-ai | d9226b3d (guard test) | LeashTests.TheHardLeashCheck_IsNeverSkipped_WhateverTheTickLength (8 tick lengths) |
| 23 | not_a_bug | misc-systems | none | none (vmangos IsWithinDist uses a strict less-than) |
| 24 | fixed | pets-totems | cd07033a | PetLifecycleFidelityTests.PlayerDeath_UnsummonsItsPetAndMiniPetAtOnce_EvenInCombat_ButAGuardianInCombatStays |
| 25 | not_a_bug | pets-totems | none | none (vmangos keeps creature-owned totems on purpose) |
| 26 | not_a_bug | misc-systems | none | none (vmangos RewardHonorOnDeath forfeits an absent attacker's share) |
| 27 | fixed | pets-totems | ed09a708 | PetLifecycleFidelityTests.Guardian_TakesItsOwnersPlayerControlledAndPvpFlags |
| 28 | fixed | pets-totems | 810d26c8 | PetLifecycleFidelityTests.ADeadSummonedPetOrGuardian_IsUnsummonedAfterFifteenSeconds_AHunterPetAfterAnHour |
| 29 | fixed | pets-totems | 335c6ae5 | PetLifecycleFidelityTests.APetReturningOnAFollowOrder_IsNotArrivedWhileItCannotMove |
| 30 | fixed | pets-totems | ed09a708 | PetLifecycleFidelityTests.AMiniPet_LearnsItsCreateSpells |
| 31 | fixed | pets-totems | 77e64caa | PetLifecycleFidelityTests.APacifiedOwner_CannotSendItsPetToAttack |
| 32 | fixed (partial) | pets-totems | cd07033a | PetLifecycleFidelityTests: ThePetLeash_DoesNotApplyToAPetItsOwnerIsPossessing, TheDismissCommand_LeavesAHunterPet_WhichGoesThroughTheDismissPetSpell. The Uncharm branch of dismiss and abandon is deferred: no charm subsystem exists |
| 33 | fixed | spell-cast | 569a36db | TriggeredPowerTests: TriggeredCast_PassesThePowerCheck_WithoutTheMana, Creature_WithoutCreateMana_CastsAManaSpell, Creature_CastsASpellOfAPowerItCannotHave; guards NonTriggeredCast_StillNeedsTheMana, Creature_WithCreateMana_StillNeedsTheMana |
| 34 | fixed | spell-cast | 569a36db | TriggeredPowerTests: AuraTriggeredCast_TakesNoMana_AndDoesNotStartTheFiveSecondRule, NonAuraTriggeredCast_PaysTheMana_AndStartsTheFiveSecondRule; ManaSpendRuleTests.TriggeredCast_NotMadeByAnAura_StartsTheTimer (flipped from the old assertion) |
| 35 | fixed | spell-cast | 47ceec60 | ChannelUpdateTests: HostileChannel_BreaksPastMaxRangeTimes133, FriendlyChannel_BreaksPastMaxRangePlusTheCasterLeeway; guard Channel_WithNoDistanceLimit_IgnoresRange |
| 36 | fixed | spell-cast | 47ceec60 | ChannelUpdateTests: PlayerChannel_CancelsOnAJump_WithoutMoving, TurningChannel_CancelsWhenTheCasterTurns; guard ChannelWithoutTheTurningFlag_SurvivesATurn |
| 37 | fixed | spell-auras | 13687d81 | PeriodicActivationTimeModTests (3 tests) |
| 38 | fixed | spell-cast | f7ca364c | SpellSourceAreaTests: EnemyAoeAtSource_CentresOnTheClientSource, FriendAoeAtSource_CentresOnTheClientSource, ObjectCast_CentresTheCasterSourceArea_OnTheObject; 3 guards |
| 39 | fixed | spell-auras | 59f8acf2 | PeriodicDamageRampTests (3 tests) |
| 40 | fixed | spell-auras | 832a619c | ImmunityTests (6 new cases: HoT and energize under school immunity) |
| 41 | not_a_bug | combat-melee | none | none (vmangos SpellCaster.cpp:555-559 keys on the victim being a player) |
| 42 | fixed | combat-melee | 1ce7238d | MeleeCritVersusTests (2 tests) |
| 43 | fixed | combat-melee | df67e6a8 | MeleeVictimDefenseTests (4 tests) |
| 44 | fixed | combat-melee | 7104fe5f | CreatureMeleeFlagsTests (8 cases) |
| 45 | fixed | combat-melee | efba406e, 883b8856 | MeleePlayerControlledTests (4 tests); MeleeDefenseSkillUpTests.APlayersPet_GivesNoDefenseSkill |
| 46 | fixed | combat-melee | 46a49b40 | AutoAttackTargetValidationTests (3 tests); six fixtures made faithful (opposite race plus PvP) |
| 47 | not_a_bug | combat-melee | none | none (vmangos Unit::DealDamage ends the duel the same way for any dealer) |
| 48 | fixed | creature-ai | 920aa206 | CharmedCreatureEvadeTests.CharmedCreature_Evading_IsNotLeftInEvadeMode_AndCanFightAgainOnceTheCharmEnds |
| 49 | fixed | economy | 55e1d1a4 | MailTimingRulesTests.CodPaymentLetter_CannotBeReturned_LikeOnExpiry |
| 50 | not_a_bug | economy | none | none (vmangos delivers the COD payment letter at once) |
| 51 | fixed | economy | 09fe4af9 | NpcVendorServiceTests: SellItem_SpentExpendableCharges_ScaleThePrice, SellItem_LostDurability_SubtractsTheUndiscountedRepairCost, SellItem_RepairCostAboveThePrice_SellsForOneCopper, SellItem_DamagedItemWithoutARepairCostRow_IsRefused |
| 52 | fixed | economy | 09fe4af9 | NpcVendorServiceTests.SellItem_NearTheMoneyCap_BuybackCostsWhatWasActuallyPaid |
| 53 | fixed | economy | fcecfe44 | PlayerInventoryEconomyTests: Stage_MergesAnArrivingStackIntoRoomOfAnExistingOne_WhenNoSlotIsFree, Stage_FillsAnExistingStackFirst_ThenPlacesTheRestWithItsOwnGuid; MockClient EconomyTradeParityTests.A_traded_stack_merges_into_the_receivers_stack_when_no_slot_is_free; Data EconomyStoreTests: Trade_AStackMergedIntoTheReceiversStack_EndsAsTheGiversConsumedItem, TakeItem_MergedIntoAStack_DeletesTheEscrowedInstance |
| 54 | fixed | gameobjects-items | 68ac8b16 | GameObjectUseLifecycleTests.Chest_OpensItsLidWhileTheWindowIsOpen_AndClosesItOnRelease |
| 55 | fixed | gameobjects-items | 68ac8b16 | GameObjectUseLifecycleTests.PartlyLootedChest_StaysActivated_SharesItsLoot_AndDespawnsFiveMinutesAfterRelease_ThenRespawnsFresh; 5 existing tests updated |
| 56 | fixed | gameobjects-items | 68ac8b16 | GameObjectUseLifecycleTests: Goober_WithoutCustomAnim_SetsItsStateActive_SendsNoAnim_AndResetsAfterItsTimer, Goober_WithACustomAnimFlagAndAnAutoClose_BroadcastsAnimZero_AndKeepsItsState, ConsumableGoober_DespawnsOnlyWhenItsAutoCloseTimerEnds |
| 57 | fixed | gameobjects-items | 02ea9214, 10b8fb73 | FishingCatchTests.AnActivatedHole_ShowsItsLeftovers_ToTheNextCatch_AndCountsTheUseWhenEmptied |
| 58 | fixed | gameobjects-items | 481d661a | GameObjectUseRulesTests: ImmunePlayer_CannotOpenAChest_ByUseOrByOpenLock, MountedPlayer_IsDismounted_UnlessTheObjectAllowsMountedUse |
| 59 | fixed | gameobjects-items | 481d661a | GameObjectUseRulesTests.QuestHerb_OpenedBySpell_NeedsTheQuest_LikeAUse; World GatheringWorldTests.AQuestVein_WithoutItsQuest_StaysClosed_AndGivesNoSkillUp |
| 60 | fixed | gameobjects-items | a7107d66 | ItemMaintenanceBagOrderTests: ZoneLimitedBag_WithALimitedItemInside_DestroysBoth_AndNeverTheItemAtTheSameSlotNumberOfTheBackpackOrEquipment, ExpiredBag_WithAnExpiredItemInside_NeverDestroysTheItemAtTheSameSlotNumberOfTheEquipment |
| 61 | fixed | gameobjects-items | a7107d66 | ItemMaintenanceBagOrderTests.ZoneLimitedItem_InTheBank_IsKeptWhenLeavingTheArea |
| 62 | fixed | gameobjects-items | de5c5d8b | FistWeaponProficiencyTests.FistWeapon_NeedsTheFistWeaponSkill_NotUnarmed |
| 63 | fixed | gameobjects-items | 630a1173 | ItemSetsAndEquipSpellsTests.APieceWornBeforeTheSkillWasReached_IsNotCounted_SoTakingItOffKeepsTheBonusOfTheCountedPieces |
| 64 | fixed (partial, by design) | gameobjects-items | 2e1a5723 | GeneratedLootStackTests: StackHoldingGeneratedLoot_CannotBeSplit, StackHoldingGeneratedLoot_IsNotAMergeTarget. Store, equip and bank are not refused, as in vmangos |
| 65 | not_a_bug | gameobjects-items | none | none (vmangos CanEquipItem refuses the merge onto an occupied equipment slot) |
| 66 | fixed | social-bans | ea5b9ece | ChannelNameNormalizationTests.JoinWithATrailingSpace_AnnouncesTheNameTheChannelIsFoundBy_SoSayAndLeaveReachIt |
| 67 | not_a_bug | social-bans | none | none (the who-list count matches vmangos exactly) |
| 68 | fixed | social-bans | cdc84b69 | GroupMemberRenameTests: RenamedMember_IsUninvitedByItsNewName, RenamedLeader_UpdatesTheLeaderName |
| 69 | fixed | social-bans | 09bb84c4 | PetitionGuildedSignerLoadTests: Load_DropsSignaturesOfCharactersAlreadyInAGuild_AndPersistsTheTrim, GuildedSignatureFromStorage_DoesNotCompleteThePetition |
| 70 | fixed | social-bans | 13dfc1ff | World SocialLoadFailureTests: PersistentReadFailure_DoesNotInstallAnEmptyList_DropsDeferredCommands_AndDisconnects, TransientReadFailure_IsRetried_AndTheStoredListIsInstalled |
| 71 | fixed | social-bans | f9d9edc3 | GuildAdminUninviteLeaderTests.AdminUninviteOfTheLeader_BroadcastsLeftOnce |
| 72 | fixed | creature-ai | fb93c86f | CreatureCrowdControlMovementTests: ConfuseEndingUnderAPushedPoint_RemovesTheBuriedStagger_WithoutStoppingThePoint, ConfuseEndingUnderAFleeForAssistance_KeepsRunningToTheHelper_InsteadOfArrivingWhereItStands |
| 73 | fixed | creature-ai | fb93c86f | CreatureCrowdControlMovementTests: TimedFlight_EndingWhileAFearAuraHoldsTheFlag_KeepsTheFlag_AndTheFearFlightTakesOver, FearAura_OnACreature_RecordsThatTheAuraHoldsTheFleeingFlag |
| 74 | fixed | creature-ai | fb93c86f | CreatureCrowdControlMovementTests: PlainFlight_StunnedOrRooted_StopsWhereItIs_AndRunsAgainWhenTheHoldLifts, TimedFlight_DoesNotRunOutWhileStunned |
| 75 | fixed | creature-ai | fb93c86f | CreatureCrowdControlMovementTests: RootedCreature_DoesNotChase_UntilTheRootLifts, Root_AppliedMidChase_StopsTheChaser |
| 76 | fixed | creature-ai | 01bd3135 | CreatureAssistanceCallTests: VMangosTemplate_WithACallForHelpRangeOfZero_CallsNobody, CMangosTemplate_WithACallForHelpRange_SearchesThatRange, CMangosTemplate_WithNoCallAssist_CallsNobody, CharmedCreature_DoesNotCallForAssistance; guard VMangosTemplate_WithACallForHelpRange_SearchesTheConfiguredAssistanceRadius |
| 77 | fixed | creature-ai | 01bd3135 | CreatureAssistanceCallTests.DelayedAssistance_AttacksTheStoredEnemy_EvenWhenTheCallerSwitchedVictimsMeanwhile |
| 78 | not_a_bug | misc-systems | none | existing RespecCostTests.Quote_ReproducesVmangosRepeatedDecayByDefault (vmangos parity; TalentOptions.IdempotentRespecDecay is the opt-in) |
| 79 | fixed | misc-systems | 79a9b232 | WarsongGulchTests.AfterACapture_TheDespawnedHomeFlagCannotBeTakenUntilBothFlagsRespawn |
| 80 | fixed | misc-systems | 79a9b232 | WarsongGulchTests.ReturningADroppedFlag_ClearsItsTakenWorldState |
| 81 | fixed | misc-systems | 79a9b232 | WarsongGulchTests.AnOfflineCarrier_PutsTheFlagBackWithTheIconAndTakenWorldStatesCleared |
| 82 | fixed | misc-systems | 79a9b232 | WarsongGulchTests.ACaptureEvent_ScoresOnlyForTheCarrier_AndOnlyWithItsOwnFlagOnBase |
| 83 | fixed | misc-systems | 79a9b232 | BattlegroundCoreTests.AJoinerIntoAnEndedMatchIsShownTheFrozenFinalScore_WithThePlayersWhoLeftAfterTheEnd |
| 84 | fixed (partial, by design) | misc-systems | 79a9b232 | WarsongGulchTests.ADropAfterTheEnd_DoesNotLeaveTheFlagCarriedWithoutACarrier. No world states are pushed after the end (vmangos parity) |
| 85 | fixed | spell-auras | 8833473c | DrainAuraTests: DrainLife_RollsThePeriodicResist_AndAVulnerabilityAddsDamage, DrainLife_APositivePeriodicResistStillComesOff, ImprovedDrainMana_RollsThePeriodicResist |
| 86 | fixed | spell-auras | 7f6811e8 | PetPowerTests: LifeTap_FizzlesAtTheCastCheck_WhenHealthEqualsTheCost, LifeTap_PassesTheCheckAndTaps_WhenHealthIsOneAboveTheCost |
| 87 | fixed | instances-death | def5ba92 | BindCreditTests.RaidBossKilledByAPet_WithTheDefaultResolver_CreditsItsOwner |
| 88 | fixed | instances-death | def5ba92 | BindCreditTests: NormalDungeonKill_OfACreatureWithoutARespawnTimer_CountsItsCorpseTime, NormalDungeonKill_MeasuresTheRespawnTimeAgainstTheMapClock |
| 89 | deferred | instances-death | none | none. Needs group persistence first (group, group_member and group_instance tables plus a reload) |
| 90 | fixed | instances-death | 4c5114c1 | InstanceEnterLimiterTests.InstanceZero_IsNeverRecorded_SoItCannotBecomeAFreeReEntry |
| 91 | already_fixed | economy | in base (trade enchant deferral) | existing TradePendingSpellTests, TradeEnchantPlanningTests, TradeItemUsePlanningTests; MockClient TradeEnchantSettlementTests, TradeItemEnchantSettlementTests, TradeEnchantCommitFailureTests |
| 92 | fixed | economy | fcecfe44 | PlayerInventoryEconomyTests.CanBeTraded_RefusesAnItemCarryingAnEnchantmentThatCanSoulbind |
| 93 | fixed | economy | d427a985 | MockClient EconomyTradeParityTests.A_banked_item_offered_in_the_non_traded_slot_cancels_the_trade |
| 94 | fixed | instances-death | 127e1a94, f34237b9, 46b80c5e | DungeonCorpseRestoreTests (5 tests); GhostRestoreTests: Logout_OfAGhostWhoseBodyLiesInADungeonInstance_SavesThatInstance, RestoreGhost_PutsADungeonBodyBackIntoItsOwnInstance_AndTheNextSaveKeepsIt; Data CharacterLifeStoreTests: ADungeonBody_KeepsItsInstance, ADatabaseFromBeforeTheCorpseInstance_GainsTheColumn_KeepingItsBodies, TheCorpseInstanceModule_AddsOneColumnToTheCorpseTable. Adds characters schema v34 |
| 95 | fixed | instances-death | 663d7e18 | CorpseReclaimBattlegroundTests: Reclaim_InABattleground_RestoresFullHealthAndMana, Reclaim_OnAContinent_StillRestoresHalf |
| 96 | fixed (inert in production) | instances-death | 663d7e18 | CorpseReclaimBattlegroundTests.Reclaim_IsRefusedUntilTheMatchIsInProgress; BattlegroundManagerTests.MatchStatusOf_IsTheBoundMatchsStatus_AndNullOutsideAnyMatch. Nothing registers an IBattlegroundPresence until the battleground wiring lands |
| 97 | not_a_bug | instances-death | 24454d73 (comment only) | none |
| 98 | already_fixed | instances-death | 31145302 (regression test) | SelfResurrectionTests.AnAnkhGoneAfterTheDeath_RefusesTheReincarnation_AndEmptiesTheField |
| 99 | fixed | social-bans | a9599b47 | Data BanStoreEffectiveStatusTests.TemporaryBanOnTopOfAPermanentOne_PublishesTheEffectivePermanentStatus |
| 100 | fixed | social-bans | a9599b47 | Data BanStoreEffectiveStatusTests: UnbanWhoseAuditInsertFails_RollsTheDeactivationBack_AndPublishesNothing; control Unban_CommitsDeactivationAndAudit_ThenPublishesActive |
| 101 | fixed | social-bans | 2cb9b984 | World BanAddressNormalizationTests.Recheck_StoredRowInANonCanonicalForm_StillKicksTheSession |
| 102 | fixed | social-bans | 2cb9b984 | World BanAddressNormalizationTests.LiveKick_EventAddressInANonCanonicalForm_StillKicksTheSession (2 cases); control LiveKick_AnotherAddress_KeepsTheSession |
| 103 | not_a_bug | misc-systems | 836a26ac (guard test); 57ff3848 fixes a neighbouring charges bug | EnchantSpellTests.AHeldItemEnchantment_RefreshedWithTheSameId_DoesNotStackItsStats (guard); EnchantSpellTests.AHeldItemEnchantment_TakesItsChargesFromSpellEnchantCharges (RED then GREEN) |
| 104 | fixed | misc-systems | 836a26ac | EnchantSpellTests.MinorAgilityCloak_NeverFitsANonCloak_EvenWhenTheWeaponRowMasksWouldLetAWeaponThrough |
| 105 | not_a_bug | misc-systems | none | existing EnchantSpellTests.AGameMasterCaster_IsRefused_WhenGmAllowTradesIsOff_ButASkillUpStillCountedFirst |
| 106 | fixed | misc-systems | 836a26ac | World EnchantingFeatureTests.CraftingDisabled_StillGuardsTheEnchantEffects_SoACastKeepsItsReagents |
| 107 | fixed | quests-npc-taxi | 09dd43db | NpcTrainerServiceTests: TradeSkillTrainer_WithoutItsTrainerSpell_RefusesWithGossip11031, Trainer_OfAnUnknownType_TeachesNothing |
| 108 | deferred | quests-npc-taxi | none | none. Needs the battleground area spirit-healer subsystem (resurrection-wave channel, queue, CMSG_AREA_SPIRIT_HEALER_QUERY/QUEUE handlers) |
| 109 | fixed | quests-npc-taxi | c8f6ef37 | NpcTravelServiceTests.GossipTaxiVendorRow_LearnsTheNodeOnlyForACreature_NotForAGameObjectMenu |
| 110 | not_a_bug | quests-npc-taxi | none | existing NpcTravelServiceTests express-route tests (vmangos always flies a later leg) |
| 111 | fixed | misc-systems | 66aef237 | NearTeleportArrivalTests.TheAck_KeepsTheClientsMovementState_OnlyThePositionChanges |
| 112 | fixed | misc-systems | 66aef237 | NearTeleportArrivalTests: TheAck_RunsTheZoneUpdateAtOnce_WhenTheZoneChanged, TheAck_RunsOnlyTheAreaUpdate_WhenTheZoneIsTheSame |
| 113 | not_a_bug | misc-systems | none | none (vmangos overwrites the destination and only logs the counter mismatch) |
| 114 | fixed | loot-group | d946faba | ReputationKillCreditTests: TheTappersGroup_GetsTheReputation_NotTheGroupOfWhoeverLandsTheKillingBlow, ATapperWhoLeftTheGroupAfterTheTap_StillGainsWithTheGroupOfTheTap, ASoloTapper_IsTheOnlyRecipient_AndADisbandedTapGroupFallsBackToTheTappersGroupNow, WithoutATap_TheKillerAndHisGroupGain_AndAnOfflineTapperIsReplacedByTheKiller |
| 115 | fixed | loot-group | 8e343432 | GroupLootRollTests: NeedBeforeGreed_OnlyMembersWhoCanUseTheItemRoll_AndALoneUserAutoNeedsIt (rewritten), ALoneEligibleMember_WhoCannotStoreTheItem_KeepsTheOnlyClaim |
| 116 | fixed | loot-group | 8e343432 | GroupLootRollTests: AFirstOpenerWhoLeftTheGroup_DoesNotSpendTheRolls_TheGroupStillRolls, AnOpenWhileTheMethodIsNoLongerGroupLoot_DoesNotSpendTheRolls |
| 117 | fixed | loot-group | 8e343432 | GroupLootRollTests: AMemberWhoLeavesTheGroup_DropsOutOfTheRoll_AndHisNeedNoLongerCounts, ALeaverWhoHadNotVoted_NoLongerHoldsTheRollUp_AndCannotVoteAnyMore, ADisbandedGroup_ResolvesItsRollsAtOnce_WithTheVotesCast |
| 118 | fixed | loot-group | 8e343432 | GameObjectTests.GroupRulesChest_UnderRollsOrMasterLoot_IsNotWidenedToAPasserby (3 cases); guard GroupRulesChest_UnderFreeForAll_StillLetsALateOpenerShare |
| 119 | fixed | quests-npc-taxi | 9c5422d2 | QuestRewardTests: TalkingCreditsASpeakToObjective_OnAQuestWithoutTheExplorationFlag, TalkingDoesNotCreditAnExplorationQuest_ThatAlsoNamesTheCreature; TurnInRequestsCannotCreditMissingKills (renamed; reverses the rule b884d830 put in) |
| 120 | not_a_bug | quests-npc-taxi | none | existing QuestIntegrationTests.ItemRemoval_UsesRemainingInventory_AndMoneyCanRevertCompletion |
| 121 | fixed | quests-npc-taxi | 81c91176 | QuestShareTests.PushToParty_AMemberInAnotherInstanceOfTheSameMap_IsTooFar |
| 122 | fixed | quests-npc-taxi | 81c91176 | QuestShareTests.APartyAcceptQuest_IsNotOfferedToAMemberInAnotherInstanceOfTheSameMap |
| 123 | fixed | combat-melee | 883b8856 | MeleeDefenseSkillUpTests.TheKillingSwing_StillRollsTheVictimsDefense; updated CombatSkillsTests.NoSkillFromPlayersOrWhileShapeshifted_AndADeadVictimDoesNotRollDefense |
| 124 | fixed | combat-melee | 883b8856 | MeleeDefenseSkillUpTests.AWorldBoss_CountsAsThePlayersLevelPlusThree_ForTheDefenseChance |
| 125 | fixed | combat-melee | 89b5fac2 | MeleeSpellMissSkillTests (4 cases) |
| 126 | fixed (latent, no RED possible) | combat-melee | 85f8826a | none dedicated; PlayerStatSystemTests and RangedDamageTests green |

## Integration summary

### Branches merged

| Order | Branch | Lane tip | Merge commit | Conflicts |
|---|---|---|---|---|
| 1 | claude/fix-combat-melee | 85f8826a | a5271b7c | none |
| 2 | claude/fix-spell-cast | f7ca364c | c5d5750a | none |
| 3 | claude/fix-spell-auras | 7f6811e8 | 1316d6f3 | none (SpellSystem.Auras.cs auto-merged with spell-cast) |
| 4 | claude/fix-pets-totems | 1e7d0b58 | 3b6ad8e3 | none |
| 5 | claude/fix-creature-ai | d9226b3d | d4cf6562 | CombatMovement.cs: kept the pets-totems `OnSplineFinalized` hook and the creature-ai root gate in `CannotMove` |
| 6 | claude/fix-loot-group | a3513075 | a1019ec4 | none (CreatureMapSystem.Evade.cs auto-merged with creature-ai) |
| 7 | claude/fix-instances-death | 46b80c5e | bf47bcc8 | none |
| 8 | claude/fix-quests-npc-taxi | 81c91176 | e0fd4e87 | none |
| 9 | claude/fix-economy | d427a985 | e4bd4350 | none |
| 10 | claude/fix-gameobjects-items | 10b8fb73 | 6bf0c27b | none (LootService.cs and GameObjectTests.cs auto-merged with loot-group) |
| 11 | claude/fix-social-bans | 2cb9b984 | c44203f5 | none (Group.cs and GroupManager.cs auto-merged with loot-group) |
| 12 | claude/fix-misc-systems | 57ff3848 | af341751 | none |

Lanes without a result: none.

The instances-death review returned `fix_first` against the lane's first tip. The rework (6f46df49, 46b80c5e) answers both points:
`MapCombat.ResolveCorpseMap` no longer creates an unmanaged instance map for a corpse, and pre-v34 rows (instance 0) go into the
ghost's own instance. The rework adds tests for both. The merged tip is the reworked one.

Shared files touched by more than one lane, checked after the merge: `LootService.ShowChest` and `Release` carry both the loot-group
recipient gate (finding 118) and the gameobjects chest lid state (finding 54). `CombatMovement.cs` carries both the follow inform
(finding 29) and the root gate (finding 75). `CcState.cs` (creature-ai) and `Ranged/TrapSystem.cs` (spell-cast) were touched by
one lane each. `PlayerEnchantments.cs` and `EconomySettlements.cs` (economy) were also touched by one lane only, because no crafting
lane was in this set.

Integration-only commit bb2ff9f7 fixes two tests that combining the lanes broke:

- `ChannelTriggerTests.ATriggeredSpellThatMayTargetTheDead_StillFiresAtADeadChannelTarget` (spell-auras, finding 21) failed with
  0 corpse hits instead of 2. Spell-cast's finding 18 now ends a channel whose aura target died, as vmangos
  `HasValidUnitPresentInTargetList` does, so the control channel stopped before its corpse ticks. The control channel now carries
  ALLOW_DEAD_TARGET. The finding-21 test now uses a new dead-target channel that fires the plain Missile. It asserts that the channel
  is still running when the refused ticks come due, so the test still isolates the triggered spell's own alive-state check and does
  not pass just because the channel ended.
- `ManagedPlayerbotStoreTests.Sqlite_UpgradeFromThePreviousCharactersVersion_CreatesManagedPlayerbotTable` expected the upgrade to end
  at 33. It now asserts `CharacterDbContext.Schema.CurrentVersion`, as the other module upgrade tests do.

### Schema renumbering

None needed. The only schema change is the instances-death lane's `CharacterCorpseInstanceDataModule` (characters v34, adds
`character_corpse.InstanceId`). No other lane took a characters version, and the existing v34 `CreatureDisplayScaleDataModule`
belongs to the world schema. `docs/reference/schema.md` was regenerated by the lane, and regenerating it again here changed nothing.

### Build and tests (exact counts)

- `dotnet build ArcaneCore.slnx -c Release -m:2`: 0 warnings, 0 errors (TreatWarningsAsErrors, EnforceCodeStyleInBuild), at
  af341751 and again at bb2ff9f7.
- Docs regeneration (`ARCANECORE_UPDATE_DOCS=1`, World.Tests `--filter Docs`): 76 passed, 0 failed. No generated doc changed.
- First full run, at af341751: Game.Tests 6428 passed and 1 failed, out of 6429. Data.Tests 1127 passed, 1 failed and 9 skipped, out
  of 1137. Both failures are the two tests above.
- Final runs, after bb2ff9f7:

| Project | Passed | Failed | Skipped | Total |
|---|---|---|---|---|
| ArcaneCore.Kernel.Tests | 26 | 0 | 0 | 26 |
| ArcaneCore.Cryptography.Tests | 8019 | 0 | 0 | 8019 |
| ArcaneCore.Realm.Tests | 261 | 0 | 0 | 261 |
| ArcaneCore.Data.Tests (SQLite) | 1128 | 0 | 9 | 1137 |
| ArcaneCore.Game.Tests | 6429 | 0 | 0 | 6429 |
| ArcaneCore.World.Tests | 2302 | 0 | 1 | 2303 |
| ArcaneCore.MockClient.Tests | 344 | 0 | 0 | 344 |

  Kernel, Cryptography and Realm ran on the af341751 build. bb2ff9f7 changes only two files in Game.Tests and Data.Tests, so those
  three projects' binaries are the same.
- MockClient self-test (`dotnet run --project tools/ArcaneCore.MockClient -c Release --no-build -- self-test`): exit 0, outcome
  `passed`, 59 of 59 checks passed, 148 frames received.

### Not run

- Data.Tests against MariaDB and PostgreSQL: their connection variables were unset, so only SQLite ran.
- The 9 skipped Data tests need data that is not on this machine: the real classic DB dump, the developer's client DBC files and the
  economy deletion race fixture. The World skip had already been skipped before these merges.
- A real 1.12.1 client acceptance run. The two ArcaneCore servers left running from earlier verification were not touched.
- RED proofs were not repeated at integration level. Each lane's RED and GREEN logs were checked by its reviewer.
- Nothing was pushed.

### Open follow-ups from the lane reviews (non-blocking)

- combat-melee: `IsTotem()` also counts creature type TOTEM for dodge and block. The defense skill-up uses the constant
  `WorldBossLevelDiff`, not the configured one. Finding 126 has no RED test.
- spell-cast: finding 18 tracks only the explicit or magnet target, not each effect's target list. The scripts' periodic triggers and
  the Furor proc still cast through the public `CastSpell` (finding 34). Finding 38 always adds the source location to trap casts.
  Finding 36 compares world orientation on transports.
- spell-auras: restored `character_aura` auras keep the raw amplitude, which `docs/areas/spell-mods.md` does not mention. Nothing
  tests Life Tap's effect-time fizzle branch. The energize immunity check runs after the zero-amount return.
- pets-totems: `docs/integration/pets.md` line 117 does not mention the hunter-pet dismiss exception. A dead owner's pet is saved
  through `QueueCurrentPetSave` and its reagents are not returned.
- creature-ai: a vmangos-dialect dump that lacks `call_for_help_range` imports `CallForHelp` as 0, where vmangos defaults to 5.
- loot-group: `ReputationKillCredit` looks up the tapper on the victim's map only. An NPC's kill of a player-tapped creature now
  awards reputation without vmangos' damage-origin check, which `docs/areas/reputation.md` should list as a deviation.
- instances-death: the reclaim gate of finding 96 does nothing until the battleground daemon wiring lands. Group-bind persistence
  (finding 89) is deferred.
- quests-npc-taxi: the World relog-resume flight charge refuses a fare the player cannot afford, where vmangos clamps it.
- economy: the vmangos line citations for finding 51 are off. `docs/areas/vendors-trainers.md:12` and
  `docs/integration/economy-fidelity.md:33,68` are out of date.
- gameobjects-items: `docs/areas/skills.md:141-142` and the `UseChair` summary comment in `GameObjectMapSystem.cs` are out of date.
- misc-systems: `ZoneAreaUpdater.OnRelocated` rethrows a listener exception partway through the near-teleport ack.
- social-bans: only SQLite ran the new `UnbanAccountAsync` transaction. Finding 101's RED depends on the test fake's collation model.
