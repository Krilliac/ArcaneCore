# Area: creature AI and movement

Status: in progress on `feat/creature-ai`; the integration notes are in
docs/integration/creature-ai.md.

## Scope

- **MotionMaster** (vmangos `MotionMaster`). It is a stack of movement generators over the
  spawn's default generator (idle, random or waypoint). Only the top generator updates. A
  generator that finishes is popped, and the one beneath resumes. In combat the default
  generator stays interrupted.
- **Generators**: chase, follow, fleeing (timed or not), home, point, waypoint (per-node `Run`
  flag) and random.
- **Splines.** Multi-point linear SMSG_MONSTER_MOVE with packed offsets; facing none, angle,
  spot or target.
- **AI host**: aggro radius by level difference, attack start with the assistance call, victim
  selection from the threat list with the leash, evade and return home, flee for assistance.
- **AI selection** from `creature_template.AIName`: `NullAI`, `ReactorAI`, `PassiveAI`,
  `AggressorAI`, `EventAI`, plus registered C# scripts.
- **EventAI** (cmangos-classic semantics, re-implemented) with `creature_ai_scripts` and
  `creature_ai_texts`.

The full write-up follows with the implementation commits.
