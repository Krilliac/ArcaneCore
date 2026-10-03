# Quest progression, XP and leveling (`feat/quest-progression`)

Fleet round 2 worker branch. Work in progress; the PR stays draft until the
area is complete and hosted CI is green.

## Scope

- Player XP and leveling: vanilla kill XP (gray level, zero difference, elite,
  group rate), level-up stat recalculation, `SMSG_LOG_XPGAIN`, `SMSG_LEVELUP_INFO`.
- Quest XP (level-difference reduction, max-level money conversion) and reward spells.
- Objective adapters: item collection, area-trigger exploration, spell-cast and
  gameobject-use credit hooks, group kill credit.
- Repeatable quests (1.12 has no dailies), source items on accept/abandon.
- Settlement/recovery guarantees and the explicit reward allowlist are retained.

## Schema

None used. See "Limitations" once filled in.
