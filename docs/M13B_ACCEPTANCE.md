# M13b acceptance — visible creature quest journal

Automated synthetic acceptance is distinct from later developer acceptance on an actual
build-5875 client. No client binaries, extracted DBCs, or third-party server traffic are required
for the automated path.

## Synthetic fixture

Provide a non-repeatable quest 900001 with Method 2, Type 0, MinLevel 1, QuestLevel 1, and an optional 30-second
LimitTime. A creature objective keeps it incomplete. Leave source item/spell, deliver/source
item IDs, reputation objective, party-accept flag, and auto-reward flag clear. Add the actual
creature starter relation for the questgiver's entry.

Place a real CreatureSpawn at the synthetic player's start on map 0 within the interaction
radius. Its CreatureTemplate carries NpcFlags 2 and a resolvable faction-template ID. Inject
`FactionTemplateCatalog`, or configure `Quests:FactionTemplateDbcPath` with a synthetic
fourteen-field WDBC file. Use a player record `(1, 1, 0, 1, 0, 0)` and an NPC record whose faction
ID and hostile mask are zero. Supply separate hostile, unknown, and contested-guard cases.
All records and IDs are synthetic, independent of developer content.

## Native mock flow

1. Complete realm SRP and encrypted world authentication; create and log in the synthetic player.
2. Wait for the actual questgiver create/visibility event.
3. Send CMSG_QUESTGIVER_STATUS_QUERY (0x0182), u64 GUID. Check SMSG_QUESTGIVER_STATUS (0x0183)
   echoes the GUID and a u32 dialog status.
4. Send CMSG_QUESTGIVER_QUERY_QUEST (0x0186), u64 GUID and u32 quest ID. Check
   SMSG_QUESTGIVER_QUEST_DETAILS (0x0188) begins with those values and correct text.
5. Send CMSG_QUESTGIVER_ACCEPT_QUEST (0x0189), the same twelve-byte body. Check empty
   SMSG_GOSSIP_COMPLETE, an occupied quest-log slot, and persisted INCOMPLETE status.
6. Repeat acceptance. Check timer, slot count, and persistence revision remain unchanged.
7. Send CMSG_QUESTLOG_REMOVE_QUEST (0x0194), one slot byte. Check all three slot fields clear,
   timed tracking disappears, and status NONE with deadline zero persists.
8. Disconnect and relog. The abandoned quest must not occupy a log slot; reacceptance creates
   a fresh timer and reset objective counters.

Check invisible/cross-map GUIDs, hostile/unknown/reputation-dependent factions, distant or
vertically separated creatures, dead/unselectable/charmed/combat creatures, missing questgiver
flags, and unrelated quest IDs cannot create journal entries. Details require a nearby related
questgiver. Status can work beyond interaction range while the creature remains visible.

Malformed lengths close the session: status requires eight bytes, details/accept twelve, and
abandonment one. A sixteen-byte later-build acceptance payload is rejected.

Details for a related active quest can be read even if the player is ineligible to accept it,
matching the pinned handler's absence of a CanTakeQuest check. Acceptance must still deny it.
Status intentionally uses visibility/hostility rather than interaction guards: visible dead or
distant known-faction creatures can return status, while unknown reactions receive no reply.
Seed a repeatable row with status NONE and Rewarded=true and verify acceptance cannot mutate
that history or start a timer in this tranche. Negative details assertions must inspect packets
through the subsequent ordered GOSSIP_COMPLETE response, so delayed dispatch cannot hide a leak.

Socket tests use WorldTestHost's encrypted sessions. Game tests cover strict distance and map
identity. Data tests decode an independent WDBC vector and round-trip journal deltas through
fresh EF contexts in SQLite, MariaDB, and PostgreSQL (the latter two when the matrix is configured).

## Developer client acceptance

Use a build-5875 client and developer-owned content with a supported neutral questgiver whose
faction record resolves under this tranche. Verify status/details, log acceptance, abandonment,
timed expiry, and relog. Record client build, provider, DBC/content provenance, server revision,
observed packets, and result. Broader reactions and reward/event flows need their real adapters.
