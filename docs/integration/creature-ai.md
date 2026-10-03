# Integration notes: creature AI and movement (`feat/creature-ai`)

Work in progress. Schema: world **7** on this branch, reserved **8** (the bootstrapper needs
contiguous steps and world 7 is not in this branch's base; the lead renumbers).

Seams: local optional `ICreaturePathfinder` / `ICreatureLineOfSight` (feat/vmap-los has not
published `IPathfinder`/`ILineOfSight`), `ICreatureHostility` for the reputation area,
`ICreatureSpellCaster` over the spell system, `CreatureAiFactory` for scripts.

The full shared-file edit list follows with the implementation commits.
