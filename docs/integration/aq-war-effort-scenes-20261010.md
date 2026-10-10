# AQ war effort: Cenarion Hold attack and Saurfang scenes — 2026-10-10

> Superseded in part by `aq-war-effort-saurfang-20261010.md`: the vmangos core schedule puts the attack and the final battle 4 h and 8 h into the ten-hour war, not at the end of the transport.

This depends on `aq-war-effort-piles-20261010.md` (PR #39) and `aq-war-effort-phases-20261010.md` (PR #38). It needs no schema change.

## Reference and scheduling

The scripts are vmangos `world_event_wareffort.cpp` `npc_aqwar_ch_attack` and `npc_aqwar_saurfang`. vmangos starts them with its world-database events 59 (`EVENT_WAR_EFFORT_CH_ATTACK`) and 61 (`EVENT_WAR_EFFORT_FINALBATTLE`). Those events' schedule is not in the vmangos core repository, and ClassicDB has neither the events nor the spawner/Saurfang spawns.

`WarEffortSceneSchedule` therefore places the scenes on the saved five-day transport deadline, following the script's own note "Waves for 3h, 1h break before final battle":

- **Cenarion Hold attack:** the last five hours before the deadline, until one hour before it.
- **Final battle:** the last hour.

**This schedule is ArcaneCore's assumption, not reference data.** Every scene is computed from the saved deadline and the clock.

## Behaviour

- **Waves.** The first wave comes 60 s after the attack starts, then one every 15 minutes, eleven in total (the script's `m_waveCount` 1→12). Each wave summons 10 creatures at (-7067.77, 966.62) ±15 yd: a Colossal Anubisath (15743) with 5/6 odds, otherwise a Qiraji Destroyer (15744), as in `urand(0, 5)`. They are given Cenarion Hold (-6959.35, 940.41, 14.55) as home and run there. One of them says a random line from 11536 or 11611-11613. After a restart, only the current wave is spawned again; earlier waves are not replayed.
- **Saurfang.** When the final battle starts, Saurfang (14720) is summoned at his post (-6985.67, 956.06). His script AI is registered only for a temporary Saurfang in the Silithus area, so the Orgrimmar spawn keeps its own AI, as in vmangos `GetAI_npc_aqwar_saurfang`'s zone check. After 120 s he speaks 11620-11631, 11646 and 11647, one line every 10 s. A Saurfang summoned after a restart starts at the current line instead of replaying earlier ones. After the fourteenth line, broadcast 11619 goes to the world once, and he runs the 12-point `saurfangGatePath`, pausing while in combat.

## Not done

- **Saurfang's fight:** his combat spells (Mortal Strike, Cleave, Charge, Terrifying Roar, Saurfang's Rage), aggro and kill lines, faction change, mount, and battle-won line are not scripted.
- **Infantry and riflemen:** vmangos `npc_ironforge_infantry` and `npc_orgrimmar_infantry/rifleman` (formation rotation, following Saurfang, Vengeance on death) need vmangos-only spawns.
- **Transport day events 54-58:** those vmangos events are not modelled. The pile tiers from #39 stand in for the transport days.
- **Restart mid-ride:** Saurfang's ride is not persisted. After a restart he is summoned at his post and rides from the first point, as vmangos's `JustRespawned`.
- **Wave spawn height:** the spawner's Z (4.56) is used without a terrain height lookup.
- Not exercised with an original client or the imported world.

## Tests

`WarEffortSceneTests` has 6 tests: the scene windows, wave timing, speech timing, wave spawning (including the restart that does not replay earlier waves), Saurfang's resumed speech, world broadcast and gate-path steps, and the Orgrimmar spawn keeping its own AI.
