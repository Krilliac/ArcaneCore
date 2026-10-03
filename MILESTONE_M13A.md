# M13a — saved quest journals and content queries

Status: implementation candidate; real-client acceptance is pending
([procedure](docs/M13A_ACCEPTANCE.md)). This is the first bounded daemon slice of
M13, built on the partial quest/NPC implementation incorporated by the
[fleet integration](docs/integration/fleet-20261003.md).

## What was built

| Area | Behaviour |
|---|---|
| Login | Load persisted quest rows before self-create, including objective counters, completed/failed state and absolute timed deadlines. Self visibility includes group-only quest IDs; unrelated observers do not receive them. |
| Content | Load immutable quest and NPC text stores at daemon attachment; a registered store that fails stops startup. Empty stores remain supported for development. |
| Queries | Logged-in `CMSG_QUEST_QUERY` and `CMSG_NPC_TEXT_QUERY` produce vanilla responses. Unknown quests have no reply; unknown NPC text uses the vanilla fallback. Invalid request lengths disconnect, and character-selection sessions cannot use these handlers. |
| Timers | Check timed quests on the world thread after login and on every map, including maps created after attachment. Expiry fails once and persists a cleared deadline. Far transfers retain the journal and resume checks at destination acknowledgement. |
| Persistence | Ordered quest/taxi writes use fresh scopes off the world thread. Login barriers recover retained snapshots after failed writes. Monotonic revisions reject stale asynchronous loads after a reconnect, including after newer writes complete. |
| Shutdown | Discovered world features drain in reverse order after the world stops; quest saves drain before host shutdown succeeds. Failed recovery remains visible. |

The full-suite run also exposed a social presence race: adding a friend can report
them online before their own social rows finish loading. Logout must notify
observers even during that window. A minimal fix and deterministic blocked-load
regression accompany this slice.

## Verified against

Wire and behavioural facts were reimplemented from the following pinned references;
reference source was not copied into ArcaneCore.

| Fact | Reference |
|---|---|
| Quest query fields, hidden rewards, signed game-object objective entries, raw max-level money | [vmangos QuestHandler.cpp](https://github.com/vmangos/core/blob/4b3d241cffe245a1f68da11380bce96c23db48c0/src/game/Handlers/QuestHandler.cpp) |
| NPC text response/fallback | [vmangos QueryHandler.cpp](https://github.com/vmangos/core/blob/4b3d241cffe245a1f68da11380bce96c23db48c0/src/game/Handlers/QueryHandler.cpp) |
| Request fields and logged-in state | vmangos `Server/Packets/Quest.cpp`, `Npc.cpp`, and `Server/Protocol/Opcodes.cpp` at the same revision; [gtker packet definitions](https://github.com/gtker/wow_messages/tree/70abb9deff0bb63440d8aeb4386b820653e8a176/wow_message_parser/wowm/world/queries) cross-check |
| Journal fields and expired timer load/update ordering | [vmangos Player.cpp](https://github.com/vmangos/core/blob/4b3d241cffe245a1f68da11380bce96c23db48c0/src/game/Objects/Player.cpp), `_LoadQuestStatus` and `Update` |
| Group-only fields visible to self | [vmangos Object.cpp](https://github.com/vmangos/core/blob/4b3d241cffe245a1f68da11380bce96c23db48c0/src/game/Objects/Object.cpp), `GetUpdateFieldFlagsForTarget`; [Player.h](https://github.com/vmangos/core/blob/4b3d241cffe245a1f68da11380bce96c23db48c0/src/game/Objects/Player.h), `IsInSameRaidWith` |

## Validation and provenance

Base: integrated commit `43f1e221f582001a1a9b29c05c1eaf935741bb15`;
candidate: `codex/quest-npc-daemon-slice-20261003`. The partial quest/NPC source
is `feat/quests-npc` at `5d26e1f7c08ebc0448f0c5c967c59caffcf27c21`
(PR #2), already incorporated at that base. This follow-up introduces no schema
version: auth remains v2, characters/world v6.

Release builds treat warnings as errors. Automated coverage includes exact packet
layouts, self/observer field visibility, real loopback login/query/expiry/relog,
offline and zero-deadline expiry, persistence failure/recovery, far-map transfer,
ordered saves/shutdown/reconnect revision checks, and provider-backed quest/taxi
round trips and character deletion races. Local runs use disposable SQLite;
hosted CI supplies MariaDB and PostgreSQL through the existing engine matrix.
Local validation passed the Release solution build with zero warnings/errors
and all 8,556 tests: crypto 8,005, SQLite data 45, game 343, realm 3, world 160.
No failures or skips. The actual world entry point also created disposable
auth v2/characters v6/world v6 schemas and started on loopback port zero with
135 opcode handlers. Content was empty and terrain unavailable in that smoke
test. Exact hosted counts and run links are recorded on the draft PR after CI.

## Remaining work

Quest acceptance, abandonment, objective event adapters, rewards, quest-giver
interaction, vendors, trainers, and taxi flight are subsequent M13 slices.
NPC interaction needs actual faction/visibility and creature metadata adapters;
the existing optional dependencies are not replaced with permissive defaults.
Group sharing of quest fields is also outside this self-visibility correction.

No authorized build 5875 client was available for acceptance. NPC text is query
delivery only; this slice does not claim a working gossip window or playable
quest flow. No client files, game assets, or populated developer databases were
downloaded or modified. The supplied Claude session remained inaccessible, so
repository history, charter, seams, and pinned references supplied the context.
