# Area: raid groups (WoW 1.12.1, build 5875)

This lane completes the party/raid controls already owned by `Game/Groups/GroupManager` and
`World/Social/GroupHandlers`, and fills the aura and pet fields of party member stats. All
references below are read-only; no GPL code or data was copied into ArcaneCore.

## Group rules and wire format

| Behavior | ArcaneCore | 1.12 reference |
|---|---|---|
| Invite, accept, decline, ignore, faction, online target, already grouped/invited, leader/assistant permission | `GroupManager.Invite`, `Accept`, `Decline` | `D:\refs\vmangos\src\game\Handlers\GroupHandler.cpp:56-196`; `D:\refs\wow_messages\wow_message_parser\wowm\world\social\cmsg_group_invite.wowm:1-7` |
| An ungrouped player inviting himself gets no pending group or success packet | `GroupManager.Invite` | `D:\refs\vmangos\src\game\Handlers\GroupHandler.cpp:120-150`, `D:\refs\vmangos\src\game\Group\Group.cpp:259-286` (`AddLeaderInvite` makes the inviter invited, then `AddInvite(self)` fails) |
| 5 member party; raid conversion by leader; 40 members in 8 subgroups of 5 | `Group`, `GroupManager.ConvertToRaid` | `D:\refs\vmangos\src\game\Group\Group.h:49-51`, `D:\refs\vmangos\src\game\Handlers\GroupHandler.cpp:460-505`; `D:\refs\wow_messages\wow_message_parser\wowm\world\social\cmsg_group_raid_convert.wowm:1-3`, `D:\refs\wow_messages\wow_message_parser\wowm\world\social\cmsg_group_change_sub_group.wowm:1-6` |
| Swap subgroups; appoint an online assistant; leader change only to an online member; leaving a two member group disbands it | `GroupManager.SwapSubGroup`, `SetAssistant`, `SetLeader`, `RemoveFromGroup` | `D:\refs\vmangos\src\game\Handlers\GroupHandler.cpp:298-315,508-559`, `D:\refs\vmangos\src\game\Group\Group.cpp:430-540`; `D:\refs\wow_messages\wow_message_parser\wowm\world\social\cmsg_group_swap_sub_group.wowm:1-6`, `D:\refs\wow_messages\wow_message_parser\wowm\world\social\cmsg_group_assistant_leader.wowm:1-6`, `D:\refs\wow_messages\wow_message_parser\wowm\world\social\cmsg_group_set_leader.wowm:1-5` |
| An offline leader yields after 300 seconds by default, preferring an online raid assistant; no online replacement keeps the old leader. `World:Social:OfflineLeaderDelaySeconds=0` disables it | `GroupManager.UpdateOfflineLeaders`, called by the social stats tick | `D:\refs\vmangos\src\game\World.cpp:811,2063-2072`, `D:\refs\vmangos\src\game\Group\Group.cpp:1448-1471,1654-1696` |
| Group loot method 0-4, master looter member check, per-group quality threshold | `GroupManager.SetLootMethod` and `Group.LootThreshold` | `D:\refs\vmangos\src\game\Handlers\GroupHandler.cpp:336-367`, `D:\refs\vmangos\src\game\Group\Group.cpp:121-134`; `D:\refs\wow_messages\wow_message_parser\wowm\world\social\cmsg_loot_method.wowm:1-7` |
| `SMSG_GROUP_LIST`: own type/flags, other members with status/subgroup/assistant, leader, loot method, master looter, threshold, trailing difficulty zero | `GroupPackets.BuildGroupList` | `D:\refs\vmangos\src\game\Server\Packets\Group.cpp:212-262`, `D:\refs\vmangos\src\game\Group\Group.cpp:1364-1400`, `D:\refs\mangos-classic\src\game\Groups\Group.cpp:668-708`; `D:\refs\wow_messages\wow_message_parser\wowm\world\social\smsg_group_list.wowm:8-33` (wow_messages omits the final difficulty byte) |
| Ready check request is an empty `MSG_RAID_READY_CHECK`; reply is `u8 state` from the client and `u64 member + u8 state` to the leader | `GroupManager.ReadyCheck`, `GroupPackets.BuildReadyCheckResponse` | `D:\refs\vmangos\src\game\Handlers\GroupHandler.cpp:562-596`, `D:\refs\vmangos\src\game\Server\Packets\Group.cpp:84-101,157-168`; `D:\refs\wow_messages\wow_message_parser\wowm\world\raid\msg_raid_ready_check.wowm:1-14` |
| Target icons use `MSG_RAID_TARGET_UPDATE`: request `0xFF`, delta `0 + icon + guid`, sparse full list `1 + (icon + guid)` for each set icon | `GroupManager.TargetIcon`, `GroupPackets.BuildTargetIconDelta/List` | `D:\refs\vmangos\src\game\Handlers\GroupHandler.cpp:434-457`, `D:\refs\vmangos\src\game\Group\Group.cpp:1247-1265,1318-1338`, `D:\refs\vmangos\src\game\Server\Packets\Group.cpp:170-199`; `D:\refs\wow_messages\wow_message_parser\wowm\world\raid\raid_target.wowm:21-40` (wow_messages models eight full-list entries; vmangos sends only occupied entries) |
| `SMSG_PARTY_MEMBER_STATS` and `_FULL`: packed GUID, u32 mask, then health, power, level, zone, signed 16-bit X/Y, positive/negative aura masks with u16 spell IDs, pet GUID/name/model/health/power/auras | `GroupMemberStatsSnapshot`, `GroupPackets.BuildPartyMemberStats` | `D:\refs\vmangos\src\game\Handlers\GroupHandler.cpp:599-805`, `D:\refs\vmangos\src\game\Group\Group.h:124-148`; `D:\refs\wow_messages\wow_message_parser\wowm\world\social\smsg_party_member_stats.wowm:1-70`, `D:\refs\wow_messages\wow_message_parser\wowm\world\social\smsg_party_member_stats_full.wowm:1-67` (wow_messages omits negative aura fields present in vmangos for build 5875) |

`GroupMemberStatsSnapshot` reads the 48 visible `UNIT_FIELD_AURA` slots for each player and
pet, as the spell system writes them. Full packets include occupied slots; changed packets
include changed slots, including a removed aura with spell ID zero. A new or removed pet
causes every pet field to be refreshed. The group manager sends changed stats to members
who cannot see the player, and `_FULL` in response to a member-stats request. Tests use
packet readers to check field order, aura removal, pet fields, group limits and roles.

## Version boundaries and limits

- `MSG_PARTY_ASSIGNMENT` (main tank/main assist) is tagged only for 2.4.3 and 3.x in
  `D:\refs\wow_messages\wow_message_parser\wowm\world\social\msg_party_assignment.wowm:1-14`.
  There is no 1.12 client handler to add. vmangos keeps internal main-tank and
  main-assistant database columns (`D:\refs\vmangos\src\game\Group\Group.cpp:149-152`),
  but its 1.12 group handler has no party-assignment request. `CMSG_RAID_ICON` is likewise
  absent from the 1.12 reference; 1.12 target marking uses `MSG_RAID_TARGET_UPDATE` above.
- Separate `MSG_RAID_READY_CHECK_CONFIRM` is tagged only for 2.4.3 and 3.x in
  `D:\refs\wow_messages\wow_message_parser\wowm\world\raid\msg_raid_ready_check_confirm.wowm:1-14`.
  1.12 confirms through `MSG_RAID_READY_CHECK` itself. vmangos's offline response pass
  is commented out (`D:\refs\vmangos\src\game\Group\Group.cpp:1506-1521`); ArcaneCore
  follows that behavior.
- Group membership, loot settings, assistant flags, subgroups and icons are in memory
  only. They do not survive a world-server restart; vmangos persists `groups` and
  `group_member` (`D:\refs\vmangos\src\game\Group\Group.cpp:136-157`). This lane adds
  no schema module. The pet name currently comes from the creature template; persistent
  custom pet naming is outside the pet system's current data model.
- XP and loot reward range and round-robin looter selection are covered by
  [group-loot-xp.md](group-loot-xp.md); roll/master-give work remains there.
- Real 5875 client acceptance and MariaDB/PostgreSQL hosted CI are still pending.

## Notes

- World:Social:OfflineLeaderDelaySeconds (default 300, 0 disables) is a live-reloadable key registered in `WorldConfigKeys`; negative values are rejected.
- Party stats HP/power/pet fields are saturated to 16 bits (as before this lane); vmangos casts and would wrap above 65535.
- Verification results for this lane are recorded in the integration notes, not here.
