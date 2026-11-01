# WorldGeometry migration: scripts, creatures, holidays, taxi

Hand-rolled `dx * dx + dy * dy + dz * dz` between two world objects now goes through `WorldGeometry` (`DistanceSq3D`,
`CenterDistance3D`). The helper keeps the same float operation order, so results are bit-identical (squaring makes the
`a - b` versus `b - a` sign irrelevant).

Migrated: `CreatureAI` (heal-target search), `DireMaulSlipkikTrap`, `MoltenCoreInstance`, `MoltenCoreTargetModule`,
`NaxxramasSpellScripts`, `RaidSummonScripts` (both sites), `ArugalAi`, `StratholmeInstance`, `OuroAIs`.

Documented exceptions (one operand is not a `WorldObject`, so `WorldGeometry` does not apply):

- `Holidays/LunarFestivalScripts.cs`: object to a path waypoint tuple.
- `BlackwingLairTargetModule.DistanceSq`, `DeadminesInstance.Near`, `KernobeeAi`, `ThermapluggAi`: object to raw coordinates.
- `CreatureMapSystem.Linking.cs`: spawn-to-spawn distance (creature spawn records, not live objects).
- `Npc/TaxiFlightSystem.cs`: double-precision segment projection over waypoints.
