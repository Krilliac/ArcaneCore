# M13a acceptance — saved quest journal and content queries

Status: implementation candidate; **real-client acceptance has not been run**.
Target client: WoW 1.12.1, build 5875. Keep the M5/M6 acceptance checks working.

This slice restores an existing character's saved quest journal at login, serves
quest and NPC text content queries, and expires timed quests with persistent
failure state. It uses the integrated schemas without assigning another version.

## Setup

Use an authorized build 5875 client and developer-supplied world content. Configure
the existing realm/world daemons and a character with persisted quest progress
for known quests in `quest_template`. Preserve developer databases before changing
fixtures; use a disposable database for this procedure.

Prepare three journal entries through the supported character quest store or
disposable database fixture: an incomplete ordinary quest with objective progress,
a completed ordinary quest, and an incomplete timed quest whose stored absolute
expiry is about one minute in the future. Use valid 1.12.1 quest content and a
timer-enabled template. Record quest IDs, progress counts, expiry, and initial
character identity. Do not create overlapping draft schema lineages.

## Procedure and expected results

1. Log in. Open the quest log. The saved quests and objective counts appear from
   the initial player creation fields, and opening an entry shows its matching
   title, description, requirements, and rewards. No disconnect occurs.
2. Keep the character online until the timed quest expires. The client reports
   failure once; the quest log marks it failed. The failed status and cleared
   persistence timer are written without changing the other quest rows.
3. Log out to character selection and log back in. Ordinary quest progress and
   completion survive; the timed quest remains failed and does not regain time.
4. Repeat with the timed quest deadline already elapsed while the character is
   offline. Login completes in its normal packet order, followed by failure of
   the expired quest and a saved failure state.
5. Shut down the daemons after saves drain, restart, and log in again. Verify the
   same journal status and progress. Repeat the procedure with each configured
   database engine when assessing database acceptance.
6. Repeat login and ordinary movement with a character that has no quest rows.
   Its quest log is empty and the existing login/movement behavior still works.

PASS requires the observed client behavior above, preserved unrelated progress,
and no protocol/session errors. Record client build, content source, database
engine, quest IDs, screenshots/logs, and observed result. CI and simulated
loopback clients are separate implementation evidence.

## Automated protocol checks and remaining scope

Loopback tests exercise `CMSG_QUEST_QUERY` / `SMSG_QUEST_QUERY_RESPONSE` and
`CMSG_NPC_TEXT_QUERY` / `SMSG_NPC_TEXT_UPDATE`, including missing content and
malformed or out-of-state requests. NPC text is query delivery; opening an NPC
gossip window is a later slice and is not an acceptance claim here.

New quest acceptance, quest-giver interaction, abandonment, objective event
adapters, rewards, vendors, trainers, and taxi flight remain future work. NPC
interaction requires actual faction/visibility and creature metadata support.
No client files, DBCs, or game assets are downloaded for this implementation.
