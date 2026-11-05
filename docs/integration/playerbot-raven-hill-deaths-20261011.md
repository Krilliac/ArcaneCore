# Raven Hill playerbot death loops (#140)

## Cause

The under-level placement fix was present, but level alone does not make a walk through Raven Hill safe. A target pull is weighed
against its pack in `PlayerbotRisk.Facts`; an ordinary quest, errand or exploration route was guarded only against a death, retreat,
remembered killer or lethal-spell hazard, so it could run through the aggro reach of two otherwise ordinary creatures that the bot had
not chosen to fight. `PlayerbotRisk.TooStrongWithin` also judged each hostile at a revival point separately, so the same pair was
survivable one at a time. Its full-health strength gate also discarded a fight that is fatal at the healer's half-health landing when
the bot could win it after resting.

A live log showed two bots refusing unsafe bodies at the cemetery and then dying again within minutes on the next living walk, ending
in three-death loops with attackers 202, 531 and 949 in the area. Refusing unsafe bodies alone did not keep the next walk out of it.

| Entry | Name | Levels | Faction | Rank | Spawn time in this area |
|---|---|---:|---:|---:|---:|
| 202 | Skeletal Horror | 23-24 | 21 | 0 | 300 seconds |
| 531 | Skeletal Fiend | 24-25 | 21 | 0 | 300 seconds |
| 569 | Green Recluse | 21-22 | 22 | 0 | 300 seconds |
| 949 | Carrion Recluse | 25-26 | 22 | 0 | 300 seconds |

All four templates have `CallForHelp=0` and the vmangos extra-flags dialect, and no creature linking row covers the local spawns. The
overlap is independent on-sight aggro, including creatures of different factions, not an assistance call. The fidelity reference is
vmangos `BasicAI::MoveInLineOfSight` (`src/game/AI/BasicAI.cpp:49`) for aggro and `Creature::CallAssistance`
(`src/game/Objects/Creature.cpp:2520`) for the separate assistance mechanism. Resurrection sickness and broken durability make a
fight worse, but a bot with neither also looped there: the shared failure is entering the hostile area again.

## Change and proof

The on-sight and fight-strength rules decide whether an ordinary route may cross a visible pack: `PlayerbotRisk.ScanHazards` marks
creatures whose aggro reaches overlap as strong hazards, and `Blocking` rejects or detours a route through them. `TooStrongWithin`
weighs every hostile reaching the exact body or healer landing together, while keeping its individual checks across the reclaim
disc, and an exact landing rejects a fight predicted to consume all of the bot's health at resurrection health.

`PlayerbotOverlappingThreatTests` uses the manual world clock and creatures with assistance disabled:

- `TwoIndependentHostilesAtSpiritHealerLanding_AreJudgedTogether`
- `CreatureAtHealerLanding_UnsafeAtResurrectionHealth_EvenWhenSafeAtFullHealth`
- `TwoCreaturesWhoseReachesOverlapBetweenThem_AreMarkedStrongByTheScan`
- `AutonomousWalk_DoesNotCrossAnUnchosenLethalPack`: one survivable creature leaves an ordinary route open, a second whose reach
  overlaps the first makes the planned route avoid every point inside both reaches.

This is local decision coverage, not a live Raven Hill acceptance run. Newly unseen patrols and respawns are learned only when
visible, and the bot damage estimate may need real fights to converge after a gear or sickness change.
