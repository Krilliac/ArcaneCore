# Zone scripts batch 11: Maws, the Arcanite Buoy, the Blackwood stores and Sentry Point (2026-10-17)

| Script | Source (mangos-classic ScriptDev2) | Port |
| --- | --- | --- |
| `boss_maws` (15571) | kalimdor/azshara.cpp:400-497 | `MawsAI` |
| `event_arcanite_buoy` (event 9542) | kalimdor/azshara.cpp:336-366 | `KalimdorWorldMapData.OnSpellEvent` |
| `go_furbolg_food` (GOs 175329-175331) | kalimdor/darkshore.cpp:870-890 | `KalimdorWorldMapData.OnGameObjectUse` |
| `at_sentry_point` (area trigger 302) | kalimdor/dustwallow_marsh.cpp:772-793 | `AreaTriggerScriptsFeature` |

## Maws

- On reset: casts Thrash (8876) unless it already has the aura, and restarts the 150-minute life timer.
- Frenzy (19812): after 25 s, with the generic frenzy emote. It repeats every 25 s, or every 15 s when Maws is below 20% health.
- Rampage (25744) on the victim: after a random 20-120 s, then again after 20-120 s, or after 12 s below 20% health.
- Dark Water (25743): every 15 s while below 20% health.
- On death, or when the 150 minutes run out (he then despawns), the nearest Theatric Lightning (183356) within 200 yd despawns and the
  -1010000 emote plays ("The beast returns from whence it came...").

## Arcanite Buoy

- When a player's buoy fires event 9542, Maws is summoned at (3561.73, -6647.2, -7.5, 1.6). He is flagged to run and starts his entry
  path 0 (`creature_movement_template`).
- If a despawned Theatric Lightning is within 20 yd of the player, it respawns.
- If a living Maws is already within 200 yd, the event is left unhandled, as in cmangos.
- Deviation: cmangos also runs Maws along that path at a run. ArcaneCore's waypoint generator only runs for an AlwaysRun template, or for a
  node with the Run column set when HonorWaypointRunColumn is on, so here he walks it.
- Not ported: `go_lightning`'s random custom animation, which is visual only.

## Blackwood stores

- Using any of the three stores makes the nearest living Blackwood Warrior (2168) within 20 yd attack the player, if it is out of combat
  and able to.
- With no warrior in range, a new one appears at a random point within 20 yd and attacks. It has a two-minute out-of-combat despawn timer.
- The script takes the use: a goober casts no spell.
- Not yet ported: `npc_corrupted_furbolg` and `event_purify_food` (3938). The purification sequence is still only gated in
  `ScriptedEvents`.

## Sentry Point

- Trigger 302 brings Archmage Tervosh (4967) for 60 s at (-3476.51, -4105.94, 17.1). This happens only for a living player who has The
  Missing Diplomat (1265) in the log without having completed it, and only when Tervosh is not already within 100 yd.
- Tervosh casts the teleport visual (799) on himself.
- The nearest Sentry Point Guard (5085) within 15 yd of Tervosh turns to face him and salutes.

Tests:

- tests/ArcaneCore.Game.Tests/CreatureAi/ZoneScriptBatch11Tests.cs (11).
- tests/ArcaneCore.World.Tests/Creatures/AreaTriggerScriptsFeatureTests.cs, the two SentryPoint tests.
