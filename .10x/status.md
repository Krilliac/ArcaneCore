# Team status

Updated 2026-10-31. Base: the local `claude/uc-integration` branch at 8d0be6482, which carries `main` (PRs #1326 and #1329-#1333 merged) plus the unmerged ultracode lanes. The full CI gate runs on that branch, not per lane.

## Phase

| Track | State |
|---|---|
| Discovery: refactor audit | **Complete.** 229 raw findings, 222 after dedup, 166 confirmed, 56 refuted ([findings](reviews/2026-10-22-refactor-audit-findings.md)). The architect's plan is in [reviews/2026-10-22-refactor-audit-plan.md](reviews/2026-10-22-refactor-audit-plan.md). Everything was found by static reading. |
| Conventions (Wave 0a) | **Merged** (PR #1326): `CLAUDE.md`, `docs/conventions.md` and the suggestion-level `.editorconfig` rules. |
| Bug-fix lane (plan section 3) | **Largely merged.** Each section 3 id has an `audit #NNN` commit on this branch: #152, #116, #131, #93, #118, #64, #15, #168, #208, #169, #219, #140, #209, #170, #221, #210, #183, #220, #52, #53 and #132. #52 landed as a doc correction (the ack-timeout sweep doc now matches the recovery behaviour) and #53 as a finish-RemovePlayer fix with a test. No section 3 item is known to be open; `git log --grep "audit #"` is the check. |
| Refactor waves 1-5 (plan section 4) | **Merged** (PRs #1329-#1333, one per wave). Waves 1a-1k, 2a-2m, 3a-3h, 4a-4b and 5a-5b each have a commit. 5c was a policy decision with no edit. |
| Wave 2 follow-ups | **Merged on the integration branch**: remaining `WorldGeometry` migrations (spells, scripts and AI, object-to-object sites), the last `rand_ditheru` copies on `SpellRounding`, `TaxiFlightWriteQueue` on `CharacterKeyedWriteQueue`, `InstanceAttachments` conversions, and the shared test doubles in `ArcaneCore.TestSupport`. |
| Wave 0b (namespace-edge freeze test, finding #9) | **Merged** (commit bad57001f): `tests/ArcaneCore.World.Tests/Architecture/GameFeatureCycleFreezeTests.cs` freezes the Spells feature-namespace edges by IL inspection. |
| ADR 002 (wave strategy) | Still marked "Proposed" in its own header although the waves ran. Update the header when the maintainer confirms. |
| Roadmap and area docs | `docs/ROADMAP.md` refreshed 2026-10-31 (opcodes item 8 stays open and extended beyond upstream, LFG row corrected, SmartAI recount 9,977 of 12,448 rows, AH bot and Warden rows). |

## Gates every lane uses

The local merge gate is in [CLAUDE.md](../CLAUDE.md): locked restore, Release build, the four `tests/ci/*.slnf` suites with `-m:1`, and the MockClient self-test, all run through `tools/ops/ci/run-in-slot.ps1`. Report per-suite test counts compared with base, not exit codes. Ultracode lanes skip builds and tests; the gate runs once on the integration branch, and generated files (docs reference, conf catalog, SQL updates, schema snapshot) are regenerated there.

## Open decisions

| Decision | Owner | Blocking |
|---|---|---|
| Confirm ADR 002 as accepted | maintainer | ADR header only |
