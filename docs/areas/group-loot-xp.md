# Group loot and group experience

Lane `group-loot-xp` (branch `claude/vw2-group-loot-xp`). Standing directive: vanilla 1.12.1 behaviour
as read from the references (vmangos primary, `D:\refs`), deviations only behind a config option.

## Delivered: looter-selection-fix

The group's round-robin looter pointer now follows vmangos instead of a one-shot "next member after
the last looter" pick, and master loot no longer corrupts the master looter.

| Behaviour | Where | Reference |
|---|---|---|
| Pure pointer rule: keep the current looter if still in reach (`ifNeeded`), else next eligible member after him in group order, wrapping, current looter last, nobody eligible clears the pointer; a looter who is not a member searches from the start | `Groups/GroupLooterSelection.Next` | `vmangos Group/Group.cpp:2474-2543` (UpdateLooterGuid) |
| Master loot and free-for-all never move the pointer | `GroupLooterSelection.Next` | `Group.cpp:2476-2481` |
| Two calls per kill: first with `ifNeeded` (who loots THIS kill, the leader on the first kill because `Group::Create` sets the looter to the leader), then without (advance for the next kill) | `LootService.PlanLooter` | `Objects/Unit.cpp:1037` and `:1078`, `Group.cpp:134`, `GroupManager.cs:135` |
| Looter changed: SMSG_GROUP_LIST is resent (`ILootGroups.LooterChanged`, wired to `GroupManager.SendUpdate`) | `LootService.CommitLooter`, `GameObjectLootFeature.SocialGroups` | `Group.cpp:2530-2542` |
| Offline master looter: first online leader/assistant other than the master takes over, otherwise the group switches to group loot with threshold uncommon (`ILootGroups.IsMemberOnline`, default true) | `GroupLooterSelection.MasterLooterFallback`, `LootService.EnsureMasterLooterAvailable` | `Unit.cpp:1041-1063` |
| Game objects use the same plan ONLY for a chest with `chest.groupLootRules` (data15); herb/ore nodes, fishing nodes and ordinary chests leave the pointer and the owner alone. A durable chest only moves the pointer when its generation committed | `LootService.UsesGroupLootRules`, `OpenGameObject`, `OpenDurableGameObject`/`FinishGeneration` | `Objects/Player.cpp:7680-7698`, `GameObjectDefines.h:277` |

Behaviour changes that existing tests pinned (updated in the same commit):
the first group kill goes to the leader (it used to go to the member after him), and the pointer
afterwards advances; `GameObjectTestKit.FakeGroups.Create` now sets `LooterGuid` to the first member
exactly like `GroupManager`.

A durable dungeon chest whose initial generation is refused sends `SMSG_LOOT_RELEASE_RESPONSE` and stays ready;
the capacity/refusal regression is in `DurableChestTests` ([GM and loot gap lane](../integration/gm-and-loot-gaps-20261008.md)).

Tests: `tests/ArcaneCore.Game.Tests/Social/GroupLooterSelectionTests.cs` (pure rule, hand-simulated),
`GameObjects/LootServiceTests.cs` (leader first then rotation, out-of-reach loses the turn, master
looter untouched, offline master fallback and group-loot downgrade), `GameObjects/DurableChestTests.cs`,
`World.Tests/GameObjects/InstanceChestDurabilityTests.cs` (owner roles swapped to the leader-first order).

## Delivered: group-reward-range

`Groups/GroupRewardRange` ports vmangos `WorldObject::IsWithinLootXPDist` and
`Player::IsAtGroupRewardDistance`: same map, 2D (horizontal) distance, strict `<`, limit widened by both
bounding radii; a raid map is unlimited; a world boss victim adds 150 yd; a dead player counts through
his corpse (a living one never does). `CorpseRaid` is a hook for `CREATURE_STATIC_FLAG_CORPSE_RAID`
(Object.cpp:1485-1486): no creature data sets it (0 rows in the classic-db dump), so it is null by default.

References: `D:\refs\vmangos\src\game\Objects\Object.cpp:1478-1499` and `:1738-1752`,
`Objects\Player.cpp:20034-20050`.

Adopted by `LootService.RecipientsFor` (loot recipients, and through them the round-robin eligibility).
Config (section `Loot`): `GroupLootDistance=74`, `BossRewardDistanceBonus=150`,
`RaidMapsUnlimitedRewardDistance=true`. Setting the bonus to 0 and the raid switch to false removes those
two retail rules.

Tests: `Social/GroupRewardRangeTests.cs` (7 cases: 2D, strict, radii, world boss, raid, other map,
ghost corpse, hook) and `LootServiceTests.Recipients_UseTheRetailRewardDistance_*`.

## Delivered: loot-types-and-packets (groundwork, not yet sent by any handler)

`Loot/LootRollTypes.cs` (`RollVote` 0/1/2, `LootError` 0,4,5,6,8..16) and `Loot/GroupLootPackets.cs`:
builders for SMSG_LOOT_START_ROLL (28 bytes), SMSG_LOOT_ROLL (34; vote announcements are the vmangos pairs
pass 128/128, need 0/0, greed 128/2; resolved rolls carry number and vote 1/2), SMSG_LOOT_ROLL_WON (34),
SMSG_LOOT_ALL_PASSED (24, field order differs from START_ROLL), SMSG_LOOT_MASTER_LIST (u8 count + guids) and the
error form of SMSG_LOOT_RESPONSE (10 bytes), plus `TryParseLootRoll` (13 bytes, votes >= 3 rejected) and
`TryParseMasterGive` (17 bytes).

References: `D:\refs\vmangos\src\game\Server\Packets\Loot.cpp:20-215`, `Packets\Group.cpp:264-275`,
`Group\Group.cpp:747-850` and `:957-972`, `Handlers\GroupHandler.cpp:370-391`,
`D:\refs\mangos-classic\src\game\Entities\Player.cpp:20115-20122` (SendLootError),
`D:\refs\wow_messages\wow_message_parser\wowm\world\loot\*.wowm`. wow_messages lists gold and items after the
error code in the error form; vmangos and mangos-classic both stop at the code, which is followed here.

Tests: `GameObjects/GroupLootPacketTests.cs` (literal byte arrays).

## Delivered: group-loot-rolls-master-loot (lane L4)

Group loot, need before greed and master loot now run end to end. Reference: vmangos-family
`Group.cpp` (`GroupLoot`, `NeedBeforeGreed`, `MasterLoot`, `StartLootRoll`, `CountRollVote`, `CountTheRoll`, `EndRoll`),
`LootHandler.cpp` (`HandleLootMasterGiveOpcode`), `LootMgr.cpp` (`GetSlotTypeForSharedLoot`), read from
`/home/user/mangosserver/server`; evidence, not authority.

| Behaviour | Where |
|---|---|
| The round-robin fallback is gone for group loot and need before greed (no `LootBag.Owner`); only round robin still holds the loot for the pointer's looter. The pointer itself still advances as before | `LootService.PlanLooter` |
| The group's threshold decides per item at generation: shared, non-quest, non-party-loot items whose quality is at or above `Group.LootThreshold` leave `LootItem.IsUnderThreshold`; the bag records `LootPermission` Open / Roll / Master and the master looter | `LootService.ConfigureDistribution`, `LootModel.cs` |
| Rolls start once, when the loot is first opened, before the window is sent (like `Player::SendLoot`). They belong to the group whose method set the permission (`LootBag.DistributionGroup`, vmangos `GetGroupLootRecipient`), not the opener's: an open by someone who left that group, or while the method is no longer group loot / need before greed, starts nothing and spends nothing; a disbanded group rolls nothing. Participants: members of that group that are recipients of the loot and within reward distance; need before greed: only members for whom `CanUseItem` is OK. No participant: no roll, the item stays free. One participant: he needs it with 100 (SMSG_LOOT_ROLL_WON to him) and gets it at once, or keeps the only claim when his bags refuse it (vmangos `CountSingleLooterRoll`, `Group.cpp:1090-1121`) | `LootRollManager.Start` |
| SMSG_LOOT_START_ROLL (countdown = `Loot:RollTimeoutMs`, default 60000, `Group.cpp:72`), SMSG_LOOT_ROLL vote announcements (pass 128/128, need 0/0, greed 128/2), per-voter 1..100 rolls, SMSG_LOOT_ROLL_WON, SMSG_LOOT_ALL_PASSED, all to the participants still in the map | `LootRollManager`, `GroupLootPackets` |
| CMSG_LOOT_ROLL: a vote counts once per participant (vmangos would count a repeat again); unknown roll, non-participant, repeat and votes >= 3 are ignored | `GameObjectLootHandlers.LootRoll`, `LootRollManager.Vote` |
| Resolution: when all voted or the timer ends (non-voters pass). Need beats greed beats pass; the highest roll wins, a tie goes to the earlier member in group order; nobody left: all passed, the item is free again | `LootRollManager.Resolve` |
| The winner gets the item through the normal take tail (`LootService.AwardItem`: slot removed for viewers, quest journal told). A winner whose bags are full keeps the only claim (`LootItem.Winner`, vmangos `winner`), is sent the equip error, other viewers get SMSG_LOOT_REMOVED | `LootRollManager.Resolve`, `LootBag.SlotFor` |
| Rolled items show as view-only (`LootSlotType.RollOngoing`) and cannot be taken until resolved | `LootBag.SlotFor` |
| Rolling away the last item while nobody has a window open loots the corpse out / settles the chest | `LootService.SettleUnviewed` |
| A member leaving the map passes on his open rolls; loot that is gone cancels its rolls silently | `LootRollManager.OnPlayerRemoved`, `Vote`/`Resolve` source checks |
| A member who leaves the group or is kicked drops out of its rolls, vote and all, and hears no more of them; the roll resolves once everyone still in it has voted. A disbanded group's rolls resolve at once with the votes cast (vmangos `_removeRolls`, `Group.cpp:1780-1805`; `Disband`, `Group.cpp:605-606`). The roster is checked on each vote, at resolution and on the map update, so the effect lands within one tick | `LootRollManager.Prune` |
| A late opener of a chest is added to its recipients only when the chest is open loot; a chest under rolls or master loot keeps strangers out (they could otherwise take items under the threshold, start the rolls or be named a master loot target) | `LootService.ShowChest` |
| Master loot: no owner; the master looter sees items at or above the threshold as `LootSlotType.Master`, everyone else only items under it (money is shared as before). SMSG_LOOT_MASTER_LIST (the recipients within reward distance, group order) goes to the master when he opens the loot | `LootBag.SlotFor`, `LootService.SendMasterList` |
| CMSG_LOOT_MASTER_GIVE: only the master looter with that window open; target must be a recipient within reward distance; the item goes through `AwardItem`. Failures: not master closes the window (vmangos), unknown target answers the error form of SMSG_LOOT_RESPONSE `PlayerNotFound`, full bags `MasterInventoryFull`, unique/count limit `MasterUniqueItem`, anything else `MasterOther`; the item stays in the window | `LootService.GiveMasterLoot`, `GameObjectLootHandlers.LootMasterGive` |

Options: `Loot:RollTimeoutMs` (the config catalog page is regenerated by the orchestrator).
State and threading: the roll manager is per map (`LootService.Rolls`, attached with `Map.AddUpdater` in
`GameObjectLootFeature.OnMapCreated`), world thread only; a tick with no roll costs one loop over an empty list.

Deliberate differences from the reference cores:

* The master list is sent to the master looter only (the cores send it to every member in range).
* Under master loot, non-master members do not see items above the threshold at all (the cores show them as normal slots
  and refuse the take later); this follows the retail note previously recorded here (`LootMgr.cpp:829-981`).
* A refused master give leaves the item in the window and reports to the master; the cores stamp the target as winner.
* A roll winner's claim hides the item from the others (their client gets SMSG_LOOT_REMOVED).
* Master give validates the slot (the cores allow `slot == items.size()`).

UNVERIFIED (no client available): that the 1.12.1 client turns a view-only slot into a takeable one on SMSG_LOOT_ALL_PASSED
without a fresh SMSG_LOOT_RESPONSE; that it drops a slot on SMSG_LOOT_REMOVED for a claim it does not own; that only the
master's client reacts to SMSG_LOOT_MASTER_LIST; the exact wording shown for the roll error codes.

Tests: `Game.Tests/GameObjects/GroupLootRollTests.cs` (start, vote, need > greed > pass, tie and highest roll, all passed,
timeout into pass, vote edge cases, full-bag winner, need before greed participation, threshold and party-loot items,
leaving the map, leaving the group, disband, the lone participant, an opener outside the group, corpse looted out, master
list/slot types, give, give refusals, round robin unchanged), `GameObjectTests.GroupRulesChest_*` (no late opener under
rolls or master loot),
`LootServiceTests.MasterLoot_KeepsTheMasterAsLooter_AndDoesNotRotateIt` (updated: no owner), and
`World.Tests/GameObjects/GroupLootWorldTests.cs` (CMSG_LOOT_ROLL and CMSG_LOOT_MASTER_GIVE through the real host, timer
through the map update). `ConfigReferenceTests.Production_ConfigurationReference_MatchesTheCommittedPage` fails locally
until the orchestrator regenerates `docs/reference/configuration.md` (new key `Loot:RollTimeoutMs`).

## Limits (recorded, not delivered)

* Chests of dungeon instances stored with the instance save (`DurableKey`) keep the old behaviour: the round-robin owner for
  group loot and master loot, no rolls, no master give. Their record has no place for roll state or a master claim.
* Roll state is memory-only: a roll running when the map unloads or the world stops is dropped with its loot.
* Quest-item sharing, personal (non-groupRules) chest loot, money split rules and open range are
  unchanged and still differ from retail (see `docs/integration/gameobjects-loot.md`).
* Corpse loot and kill reputation follow the first-damage tap (`LootService.OnCreatureDamaged`, cleared on evade and
  respawn; docs/areas/loot-templates.md) and the corpse money splits among the looter's current group within
  `IsWithinLootXPDist` of the looter. Experience and quest kill credit still go to the killer's group, and
  `Progression/KillRewards.Recipients` (stats-combat-formulas lane's file) still uses the 3D `<=` rule, so XP/quest credit
  and loot disagree at the edges until it adopts `GroupRewardRange.IsAtGroupRewardDistance` (one-line
  change, handed to that lane).
* Unlike vmangos, SMSG_GROUP_LIST is only resent when the pointer value actually changes (vmangos also
  resends when it is already cleared).
* Group state stays memory-only (docs/areas/social.md).

Schema: none.
