# WorldGeometry migration: scripts, creatures, holidays, taxi

Hand-rolled `dx * dx + dy * dy + dz * dz` between two world objects now goes through `WorldGeometry` (`DistanceSq3D`,
`CenterDistance3D`). The helper keeps the same float operation order, so results are bit-identical (squaring makes the
`a - b` versus `b - a` sign irrelevant).

Migrated: `CreatureAI` (heal-target search), `DireMaulSlipkikTrap`, `MoltenCoreInstance`, `MoltenCoreTargetModule`,
`NaxxramasSpellScripts`, `RaidSummonScripts` (both sites), `ArugalAi`, `StratholmeInstance`, `OuroAIs`, `HolidaySpellScripts` (Winter Wondervolt), `InsigniaLoot.Distance`, `SpellSystem.ScriptTargets` (script-location target search).

Documented exceptions (one operand is not a `WorldObject`, so `WorldGeometry` does not apply):

- `Holidays/LunarFestivalScripts.cs`: object to a path waypoint tuple.
- `BlackwingLairTargetModule.DistanceSq`, `DeadminesInstance.Near`, `KernobeeAi`, `ThermapluggAi`: object to raw coordinates.
- `CreatureMapSystem.Linking.cs`: spawn-to-spawn distance (creature spawn records, not live objects).
- `Npc/TaxiFlightSystem.cs`: double-precision segment projection over waypoints.
- `Conditions/ConditionEvaluator.cs` (`Away`): object to `NpcInfo` coordinates, in double precision.
- `Entities/WorldObject.cs` (`TakeRelocationNotify`): object to its cached last-notified position, not a second object.
- `Spells/SpellSystem.GameObjectTargets.cs`: game object to a selected centre point (raw coordinates).
- `Teleport/TeleportService.cs` (landing check, two uses of the same expression): player to requested destination coordinates.
