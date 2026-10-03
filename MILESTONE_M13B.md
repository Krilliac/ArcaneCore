# M13b — Live creature quest journal interactions

**Source status:** serialized Release build passed with zero warnings/errors; focused native tests passed (9 data, 23 Game, 14 World). Full combined/provider CI remains a coordinator integration gate.
**Real-client status:** pending developer acceptance on 1.12.1 build 5875. Synthetic mocks do not establish real-client acceptance.

This tranche connects the persisted M13a journal to real creatures for status, details, acceptance,
and abandonment. Requests run on the authoritative map thread. A creature must exist in the
player's current map and visible GUID set. Interactions require the live questgiver flag,
liveness, selectability, no charmer, no creature combat, a known non-hostile faction reaction,
and strict 3D distance below five yards after both bounding radii are included.

`FactionTemplateCatalog` is immutable and injectable for synthetic fixtures. The optional
`Quests:FactionTemplateDbcPath` setting loads a developer-supplied WDBC file through the existing
parser. No assets are downloaded. A configured unreadable or malformed file stops attachment.
Absent data denies NPC interaction. Nonzero NPC faction IDs and contested-guard templates are
denied until Faction.dbc, reputation, and contested-PvP state can establish their reaction.
Matching template IDs do not override hostile masks.

Acceptance creates a quest-log slot and saves its journal delta. Repeated acceptance leaves the
journal unchanged. Timed acceptance records a fresh deadline; abandonment clears the slot and
deadline, removes timed tracking, and saves status NONE. Relog restores the persisted result.
Only non-repeatable ordinary Type 0 quests without source items/spells, deliver/source-item objectives,
reputation objectives, party confirmation, or automatic rewards can be accepted or abandoned by
this tranche. Content-dependent eligibility checks retain class, race, skill, condition,
prerequisite, exclusive-group, chain, and timer restrictions.

Four handlers are registered: status query, details query, accept, and remove-from-log. Gossip,
trainers, vendors, completion/reward packets, item grants/removal, and kill/cast/exploration event
adapters are outside this tranche. Missing gossip/trainer metadata is never used to enable those
daemon features. There is no schema change; the existing creature v2 model stays intact.

Details are read-only: the pinned QueryQuest checks the starter/involved relation and sends
content without a CanTakeQuest check. This tranche additionally requires a nearby live
interactable questgiver; acceptance independently enforces eligibility. Status uses visible
lookup and known hostility, preserving the reference's lack of distance/liveness interaction
checks. Visible dead or distant known-faction creatures may therefore return status. Unknown
reactions receive no reply under the requested conservative policy. Repeatable journal mutation
is denied until completion/expiry can account for rewarded history; that history remains readable.

| Fact | Pinned primary reference |
|---|---|
| FactionTemplate.dbc fourteen fields, enemy/friend precedence and masks | [vmangos DBCStructure.h](https://github.com/vmangos/core/blob/4b3d241cffe245a1f68da11380bce96c23db48c0/src/game/Database/DBCStructure.h#L315-L369) |
| Contested guard bit 0x1000 | [vmangos DBCEnums.h](https://github.com/vmangos/core/blob/4b3d241cffe245a1f68da11380bce96c23db48c0/src/game/Database/DBCEnums.h#L61-L76) |
| Reaction state dependencies | [vmangos Object.cpp](https://github.com/vmangos/core/blob/4b3d241cffe245a1f68da11380bce96c23db48c0/src/game/Objects/Object.cpp#L3354-L3476) |
| NPC guards and strict distance | [vmangos Player.cpp](https://github.com/vmangos/core/blob/4b3d241cffe245a1f68da11380bce96c23db48c0/src/game/Objects/Player.cpp#L2280-L2334), [Object.cpp distance](https://github.com/vmangos/core/blob/4b3d241cffe245a1f68da11380bce96c23db48c0/src/game/Objects/Object.cpp#L1630-L1642) |
| Request bodies and u32 status response | [vmangos Quest.cpp](https://github.com/vmangos/core/blob/4b3d241cffe245a1f68da11380bce96c23db48c0/src/game/Server/Packets/Quest.cpp), [gtker status](https://github.com/gtker/wow_messages/blob/70abb9deff0bb63440d8aeb4386b820653e8a176/wow_message_parser/wowm/world/quest/smsg_questgiver_status.wowm) |
| Vanilla acceptance has no later-build suffix | [gtker accept](https://github.com/gtker/wow_messages/blob/70abb9deff0bb63440d8aeb4386b820653e8a176/wow_message_parser/wowm/world/quest/cmsg_questgiver_accept_quest.wowm) |
| Details query and slot-only abandonment | [gtker details query](https://github.com/gtker/wow_messages/blob/70abb9deff0bb63440d8aeb4386b820653e8a176/wow_message_parser/wowm/world/queries/cmsg_questgiver_query_quest.wowm), [gtker abandon](https://github.com/gtker/wow_messages/blob/70abb9deff0bb63440d8aeb4386b820653e8a176/wow_message_parser/wowm/world/quest/cmsg_questlog_remove_quest.wowm) |
| Quest slot/status/timer changes | [vmangos AddQuest/RemoveQuestAtSlot](https://github.com/vmangos/core/blob/4b3d241cffe245a1f68da11380bce96c23db48c0/src/game/Objects/Player.cpp#L11827-L12065) |

Verification targets are `FactionTemplateDbcTests`, `QuestCreatureAdapterTests`,
`QuestInteractionWorldTests`, and the three-provider `QuestStoreTests` matrix. The procedure
is [docs/M13B_ACCEPTANCE.md](docs/M13B_ACCEPTANCE.md). No GPL implementation was copied.
