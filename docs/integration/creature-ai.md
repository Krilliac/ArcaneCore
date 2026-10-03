# Integration notes: creature AI and movement (`feat/creature-ai`)

Built on the creature, combat and spell areas. It is wired through the existing seams:
`IDataModule`, `IWorldFeature`, `IMapUpdater` and the creature map system. It does not edit
`Map.cs`, `WorldRuntime`, `Player.cs`, the DbContexts or the host wiring.

## Integrated schema allocation

World v8 follows gameobjects/loot v7. `CreatureAiDataModule.Version = 8` adds
`creature_ai_scripts`, `creature_ai_texts`, `creature_template.AIName` and
`creature_movement.Run`. Original source heads retain provisional v7.

`AddColumnChange` × 2: `creature_template.AIName` and `creature_movement.Run`. |

World 8 was reserved for this area. The bootstrapper only accepts contiguous steps
(`SchemaBootstrapper.EnsureAsync` refuses a gap), and world 7 (gameobjects + loot) was not in
the source branch's base, so that branch shipped `Version = 7` and recorded
`ReservedVersion = 8`. In the integrated tree world 7 is present: `Version` and
`ReservedVersion` are both 8, and `IntegratedSchemaTests` lists the module and expects world
steps `[2..8]`. Nothing else depends on the number.

`AddColumnChange` on tables created in an earlier step needed one generic bootstrapper fix. An
upgrade across both steps (for example world 1 → 7) creates `creature_template` from the
current model in step 2, so the column already exists when step 8 runs. `SchemaBootstrapper`
now asks the engine catalog (SQLite `pragma_table_info`, MySQL/PostgreSQL
`information_schema.columns`) and skips an `AddColumnChange` whose column exists.
`IntegratedSchemaTests.EnsureAndInspectAsync` likewise leaves out columns that later steps add
when it inspects a prefix.

## Shared-file edits (minimal, additive)

| File | Change | Why |
|---|---|---|
| `src/ArcaneCore.Game/Spells/SpellSystem.cs` | New `public event Action<Unit, Unit, SpellInfo>? SpellHit`, raised once per unit target after `ApplyEffects` in `Cast`. | The EventAI SPELLHIT event and `CreatureAI.OnSpellHit` need it. It has no other behaviour change. |
| `src/ArcaneCore.Data/Schema/SchemaBootstrapper.cs` | `AddColumnChange` is skipped when the column already exists, checked through the catalog (`ColumnExistsAsync`, sharing `CatalogCountAsync` with `TableExistsAsync`). | Upgrades across a create-table step and a later add-column step (see above). |
| `tests/ArcaneCore.Data.Tests/IntegratedSchemaTests.cs` | Module row for the AI step, world current version and steps list (source branch: 7 / `[2..7]`; integrated: 8 / `[2..8]`). Prefix inspection skips columns that later steps add. | Schema ledger. |
| `src/ArcaneCore.Kernel/WorldData/Creatures/CreatureContent.cs` | `CreatureTemplate.AIName`, `CreatureWaypoint.Run`, and an optional `CreatureAiContent` constructor argument exposed as the `Ai` property. | Content model. |
| `src/ArcaneCore.Data/World/Creatures/CreatureDataModule.cs` | `CreatureTemplateRow.AIName` (≤ 64) and `CreatureMovementRow.Run`. | The columns added by the step. |
| `src/ArcaneCore.Data/World/Creatures/EfCreatureDataStore.cs`, `CreatureDumpImporter.cs` | Load and import AIName, Run, `creature_ai_scripts` and `creature_ai_texts`. | Data path. |
| `src/ArcaneCore.Game/Creatures/Creature.cs` | `AI`, `Motion` (MotionMaster), evade state (`IsInEvadeMode` now reports it), combat start point, `ExtraFlagNoAggro`. `OnAttackedBy` goes through the AI and `OnJustDied` passes the killer. `MovementGenerator` is replaced by `Motion`. | AI host. |
| `src/ArcaneCore.Game/Creatures/CreatureMapSystem.cs` (now `partial`, plus the new `CreatureMapSystem.Ai.cs`) | Optional `CreatureAiServices` constructor argument. Movement goes through `MotionMaster`; multi-point splines and catch-up use `BuildPath`. Every tick runs the AI update (aggro scan, then script) before motion. Pending assists and summon despawns are processed. Spell state is dropped when a creature leaves the world. | AI host. |
| `src/ArcaneCore.Game/Creatures/CreatureMovement.cs`, `CreatureMovePackets.cs`, `CreatureDefines.cs` | Generator interface, `ICreatureMover` growth, multi-point SMSG_MONSTER_MOVE (`BuildPath`, `PackXYZ`) and the new `CreatureOptions` entries. | Movement. |
| `src/ArcaneCore.World/Creatures/CreatureWorldFeature.cs` | Builds `CreatureAiServices` from DI and options and passes them to every map system. | Wiring. |
| `tests/ArcaneCore.Game.Tests/CreatureTestSupport.cs` | The template builder gains `AIName` and `Civilian`. | AI tests. |
| `tests/ArcaneCore.World.Tests/Creatures/CreatureWorldTests.cs` | One new test: the feature wires `AiServices` and spawned creatures get an AI. | Wiring test. |

## Seams

- **vmap-los / pathfinding** (`feat/vmap-los`, PR #17; this branch merges it). Creature AI uses
  the shared `ArcaneCore.Game.Maps.Collision` services through `map.Collision`, with no local
  stand-ins:
  - Chase, flee, home and point moves call `map.Collision.FindPath` (`IPathfinder`). The
    corners after the start go into one multi-point SMSG_MONSTER_MOVE (vmangos
    `MoveSplineInit::MovebyPath`). On `PathType.NoPath` the creature goes straight, as vmangos
    chase does outside instances.
  - Aggro on sight and assistance use `map.Collision.IsWithinLineOfSight` (`ILineOfSight`, eye
    height 2 yd).
  - Without vmaps or mmaps the defaults (`OpenLineOfSight`, `StraightLinePathfinder`) keep the
    old behaviour. `WorldCollision.Of(world).Install(...)` swaps them, which is what the
    collision feature and the tests do.
  - `CreaturePathing.MoveTowards`, the one-corner-at-a-time helper, still works against
    `CreatureMapSystem.MoveTo` and has a test. The generators do not use it, because they launch
    the whole path at once.
- **Reputation / factions.** `ICreatureHostility` (`IsHostile`, `CanAssist`). The default
  `FactionCreatureHostility` reads FactionTemplate.dbc rows from a registered
  `FactionTemplateCatalog` or from `Creatures:FactionTemplateDbcPath`. It answers from the
  template only: no reputation, no at-war. Without a catalog nothing aggroes on sight, which
  fails closed. The reputation area can register its own `ICreatureHostility`.
- **Spells.** `ICreatureSpellCaster`, with the default `SpellSystemCreatureCaster` over
  `SpellFeature.System`.
  - A creature cast uses the creature object as the caster, so auras capture that exact
    creature (docs/integration/aura-caster-ownership.md).
  - When a corpse is removed or a creature despawns or leaves the world, `SpellSystem.RemoveUnit`
    runs and revokes the ownership token.
- **Scripts.** Register C# AIs under an `AIName` with `CreatureAiFactory.Register`. Register the
  factory in DI to have the world use it. Subclass `CreatureAI`, whose hooks are `OnAggro`,
  `OnUpdate`, `OnDeath`, `OnKilledUnit`, `OnSpellHit`, `OnEvade`, `OnReachedHome`, `OnRespawn`,
  `OnMovementInform`, `OnAttackedBy` and `MoveInLineOfSight`. Helpers: `AttackStart`,
  `UpdateVictim`, `EnterEvadeMode`, `DoCast`, `DoCallForHelp`.
- **Instances.** The leash (`ThreatRadius`) is off on instanceable maps
  (`CombatHooks.IsInstanceable`). Per-instance map updaters belong to `feat/instances`; this
  area only adds a per-map `IMapUpdater` as before.

## Configuration (`Creatures` section)

| Key | Default | Meaning (vmangos/cmangos config) |
|---|---|---|
| `AggroRate` | 1.0 | Rate.Creature.Aggro; 0 turns aggro on sight off |
| `AssistanceRadius` | 10 | CreatureFamilyAssistanceRadius |
| `AssistanceDelayMs` | 1500 | CreatureFamilyAssistanceDelay |
| `FleeAssistanceRadius` | 30 | CreatureFamilyFleeAssistanceRadius |
| `FleeDelayMs` | 7000 | CreatureFamilyFleeDelay |
| `ThreatRadius` | 60 | ThreatRadius (leash) |
| `FactionTemplateDbcPath` | — | FactionTemplate.dbc for the default hostility |

## Merge notes

- `CreatureMapSystem.cs` and `Creature.cs` are the conflict-prone files. Other areas that touch
  creature movement must now push generators through `creature.Motion` instead of setting
  `MovementGenerator`.
- `IsInEvadeMode` is now live, so `MapCombat.Attack` and `CombatHooks.CanAttack` refuse targets
  that are running home, as the combat doc anticipated.
