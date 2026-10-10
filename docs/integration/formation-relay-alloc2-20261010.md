# Wave 17: relay command 51 formations, spawn-group and MoveTo allocation (grok/formation-relay-alloc2)

Branch from integrate/wave17 at 90c5802. PR into integrate/wave17.

## 1. dbscript / relay command 51

cMaNGOS names command 51 `SCRIPT_COMMAND_SPAWN_GROUP` (DBScripts/ScriptMgr.h); its subcommands (`datalong`) manage a spawn group's formation
(ScriptMgr.cpp, the load checks and ExecuteDbscriptCommand; Maps/SpawnGroup.cpp CreatureGroup::SetFormationData, FormationData::Reset,
Disband, TrySetNewMaster, SetMovementInfo). Now handled in `CreatureMapSystem.RelayFormation.cs`:

| sub | does | columns |
| --- | --- | --- |
| 150 | create a dynamic formation for group `datalong2` (0: the target's, else the source's group) | shape `dataint`, spread `x`, options `dataint2` |
| 151 | remove group `datalong2`'s formation: followers back to their own movement, a dynamic leader keeps its own | |
| 100 | switch the target's formation shape | `datalong2` |
| 101 | set its spread (0.5-15 yd) | `x` |
| 102 | set its options (stored; KEEP_COMPACT is not modelled) | `datalong2` |

Following cMaNGOS: a dynamic formation never sets the leader's movement (the script does that). A second create on a group that already
has a formation does nothing. Steps that cMaNGOS drops at load do nothing here: a bad shape, a spread out of range, or a 151 whose group is
not a spawn group of the map. MOVEMENT (20) on a formation follower is skipped, and on the leader it records the formation's movement type
and path (SetMovementInfo).

classic-db z2815 uses only subcommand 150: relay 1162501 (Cork Gizelton), `51, 150, 19019, dataint 1, x 10`, which forms spawn group 19019
"Desolace - Gizelton Caravan" in single file. Tests: `tests/ArcaneCore.Game.Tests/SpawnGroups/RelayFormationTests.cs`, built on the dump's
caravan rows.

**Overlap with PR #36** (grok/content-gaps, still open): #36 adds cases 37/39/42/52 to the same switch in
`CreatureMapSystem.RelayScripts.cs`, right next to the new `case 51`. On merge keep both. A comment at `case 51` notes this.

## 2. Allocation leftovers

- `SpawnGroupState.Spawn`: its lambdas and local functions captured `host` and `random`, so a closure was allocated on **every** call,
  including the early returns of a full or waiting group (every group, every map update). It also rebuilt its eligible list, entry
  dictionaries and shuffle order for every respawn. Now the early checks capture nothing, and the body works in scratch collections
  the group keeps; a respawn allocates only the list it returns. `AnyMemberReady` used `foreach` over `IReadOnlyList`, which boxed an
  enumerator per waiting group per update; it is an indexed loop now.
- `CreatureMapSystem.MoveTo` wrapped each point in `[new Vector3(...)]`. It now reuses a one-element array, which is safe because a
  one-point spline keeps no `Path`. `MovePath` hands the packet builder `path` instead of `spline.Points` (the same points, without
  the one-point list). `Creature.StartSpline` uses an indexed loop (no boxed enumerator).

Behavior is unchanged: the same member order, the same random draws and the same spline points.

### Numbers (CreatureTickAllocationTests harness from PR #35, 1,570 creatures, 800 ticks at 50 ms)

Measured on PR #35's tree (the harness and its earlier cuts are there) with this slice applied on top:

| | bytes/tick |
| --- | --- |
| before (PR #35 head) | 14,160 (13.8 KiB) |
| after | 9,819 (9.6 KiB), -31% |

Gone from the top allocators: `<>c__DisplayClass35_0` (3,064 B/tick, the Spawn closure), `<>z__ReadOnlySingleElementList<Vector3>`
(798) and the boxed `List<Vector3>.Enumerator` (799). `SpawnGroupStateTests.Spawn_OfAFullOrWaitingGroup_AllocatesNothing_AndARespawnOnlyItsResult`
pins 0 bytes for a full or waiting group and at most 128 B for a respawn.

Full suites on this branch: ArcaneCore.Game.Tests 8006 passed / 16 skipped; ArcaneCore.World.Tests 3328 passed / 50 skipped; 0 failures.
