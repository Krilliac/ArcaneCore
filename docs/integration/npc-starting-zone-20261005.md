# Direct NPC service metadata and starting-zone probe

World schema 26 adds `npc_template_service_metadata`, keyed by creature entry. The column-driven importer reads direct gossip menu and trainer type/class/race/spell fields from `creature_template`; it validates vanilla numeric domains and keeps malformed source text out of bounded diagnostics. The CLI writes this companion table inside its existing import transaction and clears it with `--replace`. Specification version 4 reports these mapped fields and companion-row counts.

NPC services load the optional scoped metadata source once. Imported rows supply the base view, and explicitly configured `NpcServices:NpcTemplates` entries override an entire entry. Existing creature identity, NPC flags, direct vendor/trainer rows, and class/race/skill/cost/condition checks remain authoritative. Missing metadata retains the existing lookup behavior. Vendor/trainer template inheritance and random spawn candidates are separate pending contracts.

The loopback-only `arcane-mock starting-zone` command authenticates with an environment password, selects or creates an exact Human Warrior, observes and queries the direct Northshire creature, casts spell 2457, moves at a paced rate, and queries/accepts quest 7. It validates the actual caster/spell/self-hit header, stance, and player quest-journal fields instead of accepting arbitrary updates. Searches have packet/byte limits and the session has a 120-second ceiling. Kill credit and quest rewards require separate proof.

Primary sources are the pinned CMaNGOS/vmangos loaders, ClassicDB numeric columns, and original-build packet/update-field contracts already documented in the repository. Client assets and populated databases remain private.

Local source qualification: 14,800 passed, six existing skips, zero failures; focus145/native59/clean Release. Frozen-source and export checks verified. Live starting-zone acceptance and runtime terrain height checks remain separate from source qualification.
