# Build-5875 DBC layouts and cross-references (2026-10-08)

Lane `codex/w3-client-data-dbd`, based on `c3102f44`. Diff is uncommitted for coordinator review.

## Implementation and references

- `tools/codegen/gen_dbc_layouts.py` reads the local WoWDBDefs `definitions/*.dbd` at commit `e3df370`, selects the block covering `1.12.1.5875`, and emits `ClientDbcLayouts.Dbd.g.cs` for the 154 extracted files. The generated metadata gives counts, sizes, field names/types/widths/array lengths/offsets and COLUMNS foreign keys. WoWDBDefs definitions are CC BY-SA 4.0; its separate code license is BSD-3-Clause. See `THIRD_PARTY_NOTICES.md`. No third-party code or `.dbd` files were copied.
- Existing vmangos `src/game/Database/DBCfmt.h` format strings remain primary for the 55 vmangos files, with four ArcaneCore reader layouts primary for their files. Generation fails on a count or record-size mismatch. The selected WoWDBDefs layouts agree with all 59 primary layouts. Header/size checking follows vmangos `src/shared/Database/DBCFileLoader.cpp::Load` and `src/game/Database/DBCStores.cpp::LoadDBC` semantics already used by `ClientDbcInspector`.
- Startup now has layouts for all 154 expected files. `DbcCrossReferences.RunDbc` checks 175 integer foreign-key columns between client DBCs, including array fields and 8/16/32-bit fields, with zero/all-ones unset values ignored. `arcane-db dbc` and `.arcane dbc validate` include the results. The foreign-key declarations come from WoWDBDefs COLUMNS, not from copied vmangos logic.

## Intake changes (Claude, 2026-10-08)

- The client-internal references are reported under their own heading (`clientReferences` in `arcane-db dbc --json`) and never make `arcane-db dbc` exit 5: on the real 5875 client 25 columns always dangle, and at least two annotations are wrong for this build (FactionTemplate.FactionGroup is a mask; Map.ParentMapID holds AreaTable ids), so gating on them would make the command fail on every pristine client.
- A missing file that nothing reads is again only counted and warned about in the directory summary, as before this lane; Codex had made it a `ClientData:Strict` start-up failure and an `arcane-db dbc` exit 5, a policy change outside the brief.
- `ClientDbcLayouts.All` keeps a primary layout the generated table lacks; a CI test checks that the generated table agrees with every primary layout and that each generated layout's columns add up to its field count and record size.
- The generator resolves its paths from its own location, so it runs from any directory.
- Wave 8 merge: the two wrong annotations are overridden in the generator (`FOREIGN_OVERRIDES`; FactionTemplate.FactionGroup is no foreign key, Map.ParentMapID points at AreaTable). The scan is now 174 columns, 151 clean, 23 dangling (152 ids, 341 rows); `docs/integration/wave8-20261008.md` lists them.

## Evidence

- `python tools/codegen/gen_dbc_layouts.py`: generated 154 layouts. Re-generation was byte-identical. A deliberately shortened AreaTable vmangos format made generation fail with a count-disagreement error.
- Local `D:/ArcaneCore-data/client-dbc-5875`: 154/154 files match their reference field count and record size. DBC-to-DBC scan: 175 columns, 150 clean, 25 with 177 distinct dangling ids across 507 rows, 0 skipped. Examples: `AreaTable.ContinentID -> Map` has 17 and 150; `AreaTrigger.ContinentID -> Map` has 24 and 28. The WoWDBDefs `FactionTemplate.FactionGroup` annotation appears to describe a mask, so these counts are diagnostic candidates, not proof that every row is bad.
- Required Release solution build first failed with `NU1900` because the sandbox could not access NuGet's vulnerability feed. `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false --nologo -v:quiet -p:NuGetAudit=false` succeeded with 0 warnings and 0 errors.
- Focused Data ClientData/DbcCrossReference tests: 16 passed. Focused World ClientData tests: 13 passed, 1 skipped (ClassicDB dump gate unset).
- Full Data project: 1,322 passed, 6 skipped. Full World project: 2,963 passed, 14 skipped, 1 failed. The failure is the unchanged `Docs.DocKeyAuditTests.EveryConfigurationKeyNamedInTheDocs_ExistsInTheCatalog`: `docs/integration/wave6-20261008.md` names the playerbot movement-packets key without its `World:` prefix (fixed on main in wave 7). This lane did not edit the playerbot area or that wave-6 page.
- `git diff --check` found no whitespace errors.

## Files and limits

New: `tools/codegen/gen_dbc_layouts.py`, `src/ArcaneCore.Data/ClientData/ClientDbcLayouts.Dbd.g.cs`, `src/ArcaneCore.Data/ClientData/DbcCrossReferences.Dbd.cs`, `THIRD_PARTY_NOTICES.md`, this report.

Updated: `src/ArcaneCore.Data/ClientData/{ClientDbcLayouts,ClientDataReport,DbcCrossReferences}.cs`, `src/ArcaneCore.Data/Schema/Upgrade/Cli/DbUpgradeCli.cs`, `src/ArcaneCore.World/ClientData/ClientDataCommands.cs`, `tests/ArcaneCore.Data.Tests/ClientData/{ClientDataTests,DbcCrossReferenceTests}.cs`, `tests/ArcaneCore.World.Tests/ClientData/ClientDataWorldTests.cs`, `docs/areas/client-data.md`.

The checked extraction is present locally; no new ClassicDB rows or schema versions were needed. A complete build-5875 extraction is still required wherever this validation runs. The 25 reported DBC reference groups need content-level interpretation before treating them as defects. No live-client or live-world database validation was run. No commit, push, stash, server start or schema change was made.
