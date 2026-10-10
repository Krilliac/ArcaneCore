# Content gaps (wave 17, 2026-10-10)

Branch `grok/content-gaps`, base `integrate/wave17` (a68bcd0). No schema change. Details and tables: docs/areas/content-import.md,
"Content gaps".

## What changed
* `GameEventDumpImporter` drops `game_event_creature` / `game_event_gameobject` rows whose guid is not in the same dump's `creature` /
  `gameobject` table, counts them (`OrphanCreatureRows`, `OrphanGameObjectRows`; CLI `skipped.game_event_*_orphans`) and warns once.
  Mirrors cmangos GameEventMgr's load-time skip and classic-db `Updates/4498_backport_errors.sql`, which deletes the same 33 + 1126 rows.
* Relay commands 37 MOVE_DYNAMIC, 39 SET_HOVER, 42 SET_EQUIPMENT_SLOTS, 52 SET_GOSSIP_MENU. `CreatureAiServices.ItemTemplateOf` (bound to
  `ItemsFeature`) feeds 42; `Creature.ScriptGossipMenuId` / `DefaultGossipMenuId` carries 52 and replaces `Template.GossipMenuId` in the
  four creature gossip-menu readers.

## Counts (z2815)
* Game-event gameobjects without a spawn: 1126 -> 0 (creatures 33 -> 0). These guids have no position data anywhere in z2815, so they are
  removed, not spawned; Noblegarden eggs spawn from the existing pooled event objects.
* Unsupported relay steps: 11 -> 1 (FORMATION, relay 1162501), excluding the 4 random-movement-with-expiry rows owned by
  `grok/relay-random-movement`.

## Shared files touched
`CreatureMapSystem.RelayScripts.cs` (four switch cases; the random-movement branch edits `RelayMovement` in the same file, so expect a
trivial merge), `CreatureAiServices.cs`, `CreatureAiServicesBinder.cs`, `CreatureQuestLookup.cs`, `CreatureMapSystem.RelayConditions.cs`,
`ReceiveEmoteEvent.cs`, `ScriptLinkEvents.cs`, `ContentImporterCli(.Refresh).cs`.

## Tests
* `GameEventOrphanImportTests` (Data): cmangos and vmangos layouts, events-only and split files, write on every available provider.
* `GameEventImporterTests` / `GameEventSpawnAuditTests` real-dump assertions updated (3186 / 11148 rows, orphans 0, empty-content audit 3115).
* `RelayScriptExtraCommandTests` (Game): 12 tests incl. adverse cases (player source/target, unknown item, reset without change, combat-free paths).

## Limits
* No replay of classic-db `Updates/`; 4498's new AQ War Effort piles (gameobject 155000-155054) are not added here.
* MOVE_DYNAMIC: no terrain/LoS z adjustment and no in-combat test; SET_HOVER fly-anim flag not modelled; SET_EQUIPMENT reset restores the
  pre-script slots (no `creature_equip_template` in ArcaneCore); FORMATION (51) still skipped.
* Locally only SQLite ran for the provider matrix; MariaDB/PostgreSQL run on hosted CI.
