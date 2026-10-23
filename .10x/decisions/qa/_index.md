# QA: decisions and observations

Source: [plan](../../reviews/2026-10-22-refactor-audit-plan.md) sections 3 and 4 (Wave 4), [findings](../../reviews/2026-10-22-refactor-audit-findings.md).

## Rules for this repo

- A bug fix starts with a reproducing test that fails on base.
- A helper extraction starts with characterisation tests that pin current behaviour, including seeded RNG output and golden bytes ([ADR 002](../../adrs/002-refactor-wave-strategy.md)).
- Report per-suite test counts compared with base. A filter that matches 0 tests reads as green. So does a `--no-build` run against a stale `bin/`.

## Observations

- [DISCOVERED] There is no shared test-support project, and World.Tests source-links files from Game.Tests and Data.Tests (#177). The same test doubles are re-declared many times (#171–#174, #196):

  | Double | Copies |
  |---|---|
  | IPlayerSession stubs | ~37 |
  | Save-queue stubs | 30+ |
  | RecordingCaster | 16+ |
  | CapturingLogger | 33 |
  | TimeProvider fakes | 37 |

  Wave 4a would add `tests/ArcaneCore.TestSupport`.
- [DISCOVERED] World.Tests is a 1,147-file catch-all that includes 498 Playerbots tests (#178).
- [DISCOVERED] `HangStackCaptureTests` sets a fixed process-wide environment variable and never restores it (#183, bug).
- [DISCOVERED] Test-helper suffixes are mixed: Support, TestKit, Kit, Rig, Fixture and Harness (#180). The standard is `<Area>TestKit` and `<Area>Fixture`. Wave 4b applies it and compares test counts before and after.
