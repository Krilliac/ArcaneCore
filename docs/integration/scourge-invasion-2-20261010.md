# Scourge invasion choreography, part 2 — 2026-10-10

Builds on `codex/scourge-camp-20261009` (a68bcd0, `docs/integration/scourge-invasion-20261009.md`). That slice already owns the
pieces the wave-17 brief listed: per-zone Necropolis counts and deaths (characters 45), battles won and the 50/100/150 milestone
events 96-99, zone rotation (45-60 minute reattack, never the last-defeated zone, at most one other zone active), the six
type-40 `CONDITION_WORLD_SCRIPT` fields 2259-2264 (answered by `ScourgeInvasionFeature.WorldScriptCondition`, so they no longer
fail closed while a store is registered), the 13 HUD world states, and restart persistence. This slice does not duplicate those.

## Added

**City attacks (mangos-classic `StartNewCityAttackIfTime` / `SummonPallid`).** While the invasion state is enabled, Undercity
(1497) and Stormwind (1519) each get a Pallid Horror (16394) or Patchwork Terror (16382), picked at random, at one of the
reference's two `m_attackPoints` positions. Each capital's next-attack time (45-60 minutes, `CITY_ATTACK_TIMER_MIN/MAX`) is saved
in the new characters table `world_scourge_invasion_city` (**characters schema 47** (allocated 46, renumbered at wave-17 integration because AQ gong took 46), `ScourgeInvasionCityDataModule`).
`ClaimCityAttackAsync` advances the timer inside a serializable transaction, so two callers cannot both summon; a new attack
replaces the previous capital attacker rather than doubling it. Starting the invasion clears the timers so both capitals are
attacked at once, as the reference's empty `TimePoint` does; stopping it despawns the attackers and clears the timers. As in the
reference, city attacks keep running after 150 victories while the state stays enabled. If the capital map or its creature
system is not loaded, the timer is not spent and the attack is retried on the next 5-second refresh.

**Mouth of Kel'Thuzad (16995, `SummonMouth` / `OnDisable`).** Each attacked zone (remaining > 0, fewer than 150 victories) has
one Mouth at the reference's `InvasionZone::mouth` point; it is despawned when the zone is defeated, the milestone is reached or
the invasion stops, and resummoned after a restart for every zone that is still attacked (the reference's `ResumeInvasion`).

## Tests

- `ScourgeInvasionStoreTests.CityAttackTimersAreClaimedOnceAndSurviveARestart` (SQLite and every available provider): no claim
  while disabled, single claim per due window, timers reload from a fresh context, adverse zone/timer arguments throw, stop and
  start reset.
- `ScourgeInvasionChoreographyTests` (3): six Mouths on maps 0/1 at the reference coordinates; defeat removes only that zone's
  Mouth; capital attackers summoned once, replaced (not doubled) when due; a restarted feature resummons the five live Mouths
  and does not attack the capitals before their saved timers; stop clears everything; unloaded maps do not spend the timer;
  150 victories removes Mouths but keeps the city timer.
- `IntegratedSchemaTests` updated for characters 46 (now 47).

Locally: World.Tests Scourge/condition/game-event filter 57 passed (7 imported-content tests skipped without the world DB);
Data.Tests Scourge + schema filter 244 passed. Release build, zero warnings.

## Not done (honest limits)

- Pallid/Patchwork waypoint paths (`MoveWaypoint(pathId, PATH_FROM_ENTRY)` paths 0-3), their yells, guard-damage spell and
  Flameshocker summons: the creature system has no entry-path API yet. They stand at the spawn point and use their template AI.
- Mouth of Kel'Thuzad zone-start/zone-stop/periodic yells (`BCT_*` broadcast texts) and the reference's zone-wide weather.
- Cultist engineers, shields and traps, Necropolis visuals, and the Argent Dawn camp NPCs' gossip/quest-gating behaviour.
- **"Scourge-killed counters driving Argent Dawn rewards"**: mangos-classic and vmangos have no such counter. Argent Dawn
  rewards come from Necrotic Rune turn-ins (quest/reputation data), and the only realm counter is battles won (2219), which already
  drives events 96-99. Nothing was invented here.
- Normal-player combat against the city attackers and original-client acceptance remain outstanding; no live profile was touched.

## Next slice

Entry waypoint paths for the city attackers plus their scripted AI (yells, Flameshocker summons), then the Mouth broadcast texts.
