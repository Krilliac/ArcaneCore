# EventAI ID coverage, ClassicDB z2815 (2026-10-08)

Source: read-only `D:/refs/classic-db/Full_DB/ClassicDB_1_12_1_z2815.sql.gz`, table
`creature_ai_scripts`. Run `py -3 tools/analysis/eventai_coverage.py <dump>` for counts of **every**
reference event ID (0–42) and action ID (0–65, including the reference's UNUSED/REUSE markers
6–8 and 49, which have zero uses). The script streams `INSERT` rows and checks the discovered Game handler declarations.
Its parsed row count, 10,843, matches `CreatureBehaviourImportTests.RealClassicDbDump_ImportsAllAiRows`.
Action 0 counts empty slots and needs no handler. A creature key here is a template entry or a
negative `creature_id` spawn-guid key; this is script coverage, not the number of spawned creatures.

| Reference ID | Use count in z2815 |
| --- | ---: |
| event 35 (`DEATH_PREVENTED`) | 0 |
| action 16 (`CAST_EVENT`) | 0 |
| action 26 (`QUEST_EVENT_ALL`) | 0 |
| action 27 (`CAST_EVENT_ALL`) | 0 |
| action 44 (`CHANCED_TEXT`) | 0 |
| action 46 (`SET_THROW_MASK`) | 0 |
| action 52 (`INTERRUPT_SPELL`) | 0 |
| action 60 (`SET_SPELL_SET`) | 0 |
| action 62 (`SET_DESPAWN_AGGREGATION`) | 0 |
| action 63 (`SET_IMMUNITY_SET`) | 0 |

The source definitions are mangos-classic `src/game/AI/EventAI/CreatureEventAI.h`,
`EventAI_Type` and `EventAI_ActionType`; their action behavior is in
`src/game/AI/EventAI/CreatureEventAI.cpp::ProcessAction`. No GPL code or source rows were copied.
The vmangos `src/game/AI/CreatureEventAI.h::EventAI_Type` is a **different dialect**:
its event 35 is `STEALTH_ALERT`, whereas cMaNGOS event 35 is `DEATH_PREVENTED`.
ArcaneCore imports cMaNGOS `creature_ai_scripts` from this dump; its importer separately warns
that vmangos `creature_ai_events` rows are not imported. IDs must not be translated between
these dialects by number alone.
These IDs are still unimplemented because this dump has no rows that execute them. A future
content source using one will appear under **used but unsupported** in the world startup log;
defined IDs absent from loaded content appear under **unused**. The report is computed from
loaded `CreatureAiContent` and the Game handler registry, so it also catches unexpected IDs.

| Coverage measure | Before | After |
| --- | ---: | ---: |
| Script rows | 10,843 | 10,843 |
| Creature keys with scripts | 4,344 | 4,344 |
| Creature keys with all event/action types and parameter conditions supported | 4,344 | 4,344 |
| Used unsupported event IDs | 0 | 0 |
| Used unsupported action IDs | 0 | 0 |

The only handler with a parameter-level unsupported reason is `SpawnedEvent.UnsupportedReason`
for condition values other than 0–2 (mangos-classic `CreatureEventAI.cpp::SpawnedEventConditionsCheck`);
z2815 has zero such rows. This count does **not** prove every script's spell, text, target,
quest or timing behavior in a live client. No new ClassicDB rows are needed for the coverage
reported here; future use of the missing IDs needs a handler and its dependent game systems.

## Local verification

Native intake run (`dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false`: 0 warnings, 0 errors):

- The counts above were re-derived with an independent tuple parser (10,843 rows, 25 fields each,
  4,344 keys of which 19 are spawn-guid keys; SPAWNED condition values 0/1/2 only), matching the script.
- `EventAiCoverageTests`: 3 passed. Each test fails when `Analyze` stops marking unsupported rows, and the
  used/unused separation test fails when used IDs are not excluded from the unused list.
- Full Game: 7,269 passed, 11 skipped.
- `CreatureBehaviourImportTests` with `ARCANECORE_CLASSICDB_DUMP` set: 44 passed, 0 skipped; the real-dump
  ID assertions fail if a used action ID (11) is added to the absent list.
- Full Data with the dump variable set: 1,316 passed, 8 skipped, 0 failed (1,324 total, 4.1 minutes).
- Full World: 2,956 passed, 21 skipped, 1 failed: the known `DocKeyAuditTests` failure on the base
  commit's wave 6 note (line 12), unrelated to this change.

No server or real client was run.
