# Character creation: integration notes

See [the area doc](../areas/character-creation.md) for behaviour. This records what touches shared files.

## Schema

* **World 15**: `StartActionWorldModule.Version` (`playercreateinfo_action`). No Characters or Auth step. The
  integrator renumbers this one constant when other world steps merge first;
  `IntegratedSchemaTests.FeatureModules_HaveAssignedVersions_AndDistinctTables` lists it through the constant.
* No `ICharacterDataCleanup` (a world table).

## Shared files edited (all additive)

| File | Change |
|---|---|
| `src/ArcaneCore.World/Handlers/CharacterHandlers.cs` | create handler runs `CharacterCreationRules`, rollback on a failing hook, `CharacterNameTakenException`, enum cap (single owner of the create path) |
| `src/ArcaneCore.Data/Stores/EfCharacterStore.cs` | `CreateAsync` (generated id path) maps a unique violation to `CharacterNameTakenException`; `IsUniqueViolation` |
| `src/ArcaneCore.Protocol/ResponseCodes.cs`, `PacketReader.cs` | new `CharResult` values, `ReadCStringBytes` |
| `src/ArcaneCore.Data/Content/Import/Cli/ContentImporterCli.cs`, `Spec/ContentTableSpecs.cs` | `playercreateinfo_action` importer wiring (collision candidate with db-upgrade-tooling and economy-fidelity) |
| `tests/ArcaneCore.Data.Tests/IntegratedSchemaTests.cs` | one module line |
| `tests/ArcaneCore.World.Tests/CharacterLifecycleTests.cs`, `M6LoginAndAccountTests.cs` | a valid race/class pair without a start row now answers CHAR_CREATE_ERROR (0x2F) instead of CHAR_CREATE_FAILED |

## For other lanes

* `ICharacterHooks.OnCharacterCreatedAsync` may now be followed by a rollback: a throwing hook leads to
  `CharacterDeletion.TryDeleteAsync`, so every hook's data must be removable through its `ICharacterDataCleanup` /
  `ICharacterDeleteHook` (it already had to be).
* The create answer for an unknown pair changed as described above; tests that create characters of a race/class
  the test world data does not offer see 0x2F.
* `CharacterCreation:Mode=Legacy` restores the earlier behaviour for a development host.
* A raw login sequence in a test must tolerate an optional `SMSG_TRIGGER_CINEMATIC` after
  `SMSG_LOGIN_SETTIMESPEED` once the cinematic lands (not delivered here).

## Observed flake

`Death.GhostPersistenceTests.LogoutWhileDead_RelogsAsAGhostAtTheBody` failed once in a full-suite run that
overlapped another build and passed alone and in a second full run; it does not use the create path's new code.
