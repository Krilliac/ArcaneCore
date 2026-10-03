# Rogue lane: creature detection of stealth (RG-03)

Branch `claude/vw2-class-rogue`. Area doc: [docs/areas/rogue.md](../areas/rogue.md). No schema change. Builds on
[rogue-stealth-core.md](rogue-stealth-core.md).

## What landed

`Game/Stealth/StealthServices.cs`: per-map stealth services (installed by `StealthFeature` on every map) with
`CanCreatureSee(Unit creature, Player target, out bool alert)`:

- an unstealthed target is always seen (so today's aggro behaviour is unchanged);
- a unit in the NO_DETECT group is not seen;
- otherwise the vmangos formula with creature constants (`Unit::CanDetectStealthOf`, `Unit.cpp:6543-6616`: 5/6 yard base and per level,
  skill difference, 9 yards less behind the creature, a stunned creature never detects, always detected under 1.5 yards) and line of sight;
- `alert` is true for a target beyond sight inside the 5 yard band (vmangos `OnMoveInStealth`, `GridNotifiersImpl.h:54-70`).

## One line for the creature-ai lane

`CreatureMapSystem.CanAggroOnSight` (`Game/Creatures/CreatureMapSystem.Ai.cs`) is owned and being rewritten by creature-ai-eventai, so this
lane does not edit it. After the existing range and hostility checks add:

```csharp
&& (StealthServices.Find(Map) is not { } stealth || stealth.CanCreatureSee(creature, player, out _))
```

(vmangos `CallAIMoveLOS` evaluates `moving->IsVisibleForOrDetect(creature, creature, true, false, &alert)` and calls `MoveInLineOfSight` only
when it is true; when it is false and the player is stealthed and `alert` is set it calls `OnMoveInStealth`.)

## Limits

- The alert behaviour is not implemented: SMSG_AI_REACTION, turning to face the player, the 5 s distract, the 10 s alert cooldown and the
  conditions (hostile, not civilian, not passive, not in combat; `AI/CreatureAI.cpp:349-385`). `alert` is exposed for the creature-ai lane.
- `docs/areas/creature-ai.md` still says stealth does not modify the aggro radius; that line stays true until the one-liner above is applied.
- The Vanish "cannot be detected for 1 s" window (`Unit.cpp:6115-6141`) belongs to RG-08.
- The creature template `Detection` column (98.7 percent default 18) is not used by this model; vmangos does not use it for stealth either.

## Tests

`tests/ArcaneCore.Game.Tests/Rogue/CreatureStealthTests.cs`: unstealthed regression, the sniffed level 4 creature versus level 1 rogue distance
(3.33 yd, alert to 8.33), stealth removal, facing away (-9 yd), stunned creature, NO_DETECT, per-map install lookup.
