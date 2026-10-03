# Ordinary creature quest rewards

This slice extends the native build-5875 mock with authoritative creature-kill
progress, guarded NPC turn-in, fixed and selected item rewards, money and durable
reward history. It preserves the original journal and NPC acceptance/abandonment
scenario. The fixture adds quest 900003, two live targets and explicit reward
content; kills arrive through attack packets and the production death path.

## Supported content

`Quests:OrdinaryRewardQuestIds` is an explicit list of operator-reviewed,
ordinary, nonrepeatable creature-kill quests. It defaults to an empty list.
The synthetic host opts in only quest 900003. Its complete content is known:
two kills, one fixed item, two alternative choices and 1,234 copper.

The current template does not represent every upstream reward reputation, mail
or script field. An arbitrary imported quest cannot therefore be classified as
safe solely from its stored template. Each enabled quest must have no omitted
reward effects. Runtime guards also reject source/required items, events, casts,
gameobject objectives, repeatable/timed quests, XP, max-level-money conversion,
reward spells and unsupported reputation requirements. Item/money rewards are
the completed slice; broader quest rewards remain implementation work.

## Settlement and recovery

The map-owned journal receives direct player creature deaths once from the
authoritative combat transition. Client completion requests cannot create credit.
Turn-in checks the current map, visible and interactable ender, server objective
counters, selected zero-based choice, money and capacity for the entire reward
batch. Detached staging preserves existing inventory objects and bag identity.

The local world thread drains earlier quest and character saves and persists a
complete current character snapshot. A fresh scoped serializable transaction
compares the completed quest, money and inventory, then saves reward history,
money and the complete resulting inventory in one commit. Live inventory,
money, journal fields and success packets are published only after durability.
Rewarded history remains COMPLETE, records the chosen item entry, and leaves
the visible journal slot empty. Duplicate requests cannot grant again.

Settlement deliberately holds the local world thread. A five-second cooperative
cancellation budget applies to normal settlement, with another five-second
budget for outcome reconciliation. The commit task is always observed; a
provider that ignores cancellation can exceed the budget. This is a bounded
development slice, with a tick-latency limitation. An asynchronous production
settlement gate across every character mutation is future work.

An ambiguous acknowledgement is reconciled using a fresh scope and exact
before/after quest, money and deep inventory comparisons. Unknown outcomes or
partial live publication quarantine core snapshots and disconnect the session.
Disconnect, autosave and shutdown cannot write the stale player over committed
rewards. A fresh login drains old work, reloads the character record and every
feature, and resumes saves only after successful map insertion. Late callbacks
must match the registered Player instance. Offline becomes visible only after
the old session's final snapshot is queued or suppressed.

The character save queue retains failed snapshots, carries changed optional
action bars/home/inventory into later recovery, and propagates unresolved
shutdown failures. The quest cache adopts the transaction's committed row
without enqueueing another reward write. Ordinary quest writes cannot clear
nonrepeatable rewarded history.

Qualification also exposed a pre-existing social login race: a delayed empty
read could replace a friend added during loading. Parsed friend/ignore commands
now wait for the exact player's social read to finish, then run in order. The
bounded queue is discarded on logout, so old sessions cannot replay commands
into a replacement player. Deterministic blocked-read tests cover this seam.

## Protocol evidence

The implementation follows pinned
[vmangos Quest.cpp](https://github.com/vmangos/core/blob/4b3d241cffe245a1f68da11380bce96c23db48c0/src/game/Server/Packets/Quest.cpp)
and [QuestHandler.cpp](https://github.com/vmangos/core/blob/4b3d241cffe245a1f68da11380bce96c23db48c0/src/game/Handlers/QuestHandler.cpp).
Complete and request-reward requests contain an eight-byte GUID and four-byte
quest ID; choose-reward adds a four-byte choice slot. Exact payload lengths are
12/12/16 bytes. The completion summary has quest ID, constant 3, XP, configured
reward money, and fixed item/count pairs. It excludes the selected item.

Independent literal tests and mock decoders verify offer, completion and
24-byte kill progress layouts. The pinned
[gtker/wow_messages](https://github.com/gtker/wow_messages/tree/70abb9deff0bb63440d8aeb4386b820653e8a176)
reference agrees on widths and completion but names the final offer words
differently. The fixture uses zero spell/flag values; real-client acceptance
of broader offer semantics remains outstanding.

## Qualification

The native serialized Release build passed with zero warnings/errors. All
8,760 tests passed, with zero failures/skips: crypto 8,005; SQLite data 74;
Game 398; mock-client 92; Realm 3; World 188. The standalone executable
passed 41 checks across 122 frames in 5.024 seconds. The 16 adverse reward
cases include unreadable-outcome quarantine, an actual stale autosave attempt,
fresh login recovery, lost acknowledgement, capacity and failure/retry. Four
blocked-read social regressions passed. Hosted provider qualification and
published source accounting are recorded on canonical draft #11.

Run the complete Release solution and all configured data providers, then
`dotnet run --project tools/ArcaneCore.MockClient -c Release --no-build -- self-test`.
The executable retains all 29 original checks and adds 12 reward checks.
Negative authenticated sessions cover exact request lengths, premature requests,
wrong/stale NPCs, invalid choices, capacity, precommit failure/retry, lost commit
acknowledgement, duplicate requests and fresh relogs. Provider tests cover deep
preconditions, ownership, rollback/cancellation after real saves, stale writers,
scope isolation and mutable input ownership.

Native/hosted results and exact source accounting are recorded in
[the integration ledger](integration/fleet-20261003.md). This tranche adds no
schema version: auth remains 2, characters 6 and world 6. No valued database,
client assets, default branch merge, deployment or clustering runtime is involved.
Rendering, UI, real-client combat/content and broader quest effects still need
acceptance and implementation.
