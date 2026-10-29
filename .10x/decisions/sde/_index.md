# SDE: decisions and observations

Source: [plan](../../reviews/2026-10-22-refactor-audit-plan.md), [findings](../../reviews/2026-10-22-refactor-audit-findings.md). Working rules: [CLAUDE.md](../../../CLAUDE.md), [docs/conventions.md](../../../docs/conventions.md).

## Observations

- [DISCOVERED] The error idiom is `catch when (ex is not OutOfMemoryException)`, then one `LogFault("site", ex, ...)`. `LogFault` already calls `FaultLedger.Record` (`FaultLedger.cs` `Log`), so a `Record` + `LogFault` pair under the same key counts one fault twice (#1618). The earlier rule that recording both is deliberate (#43, #55) is superseded: one `LogFault` per fault, and a separate `Record` only under a different, deliberately distinct key. A shared boundary helper was rejected (#43, #55; refuted #4, #68, #89).
- [DISCOVERED] Some renames compile cleanly and still break behaviour. Watch for:
  - reflection in tests (`_procDepth`, `UnloadGrid`)
  - the `MapUpdateDiagnostics` switch on type names
  - the `PlayerbotQaLogTap` category strings
  - discovery that orders by FullName
- [DISCOVERED] With warnings as errors, these compiler diagnostics break the build:
  - CS0108: a new `WorldObject.DistanceTo` would collide with `GameObject.DistanceTo`, and per-class `Due` copies must be deleted in the same commit as the shared one.
  - CS1574: a stale cref.
  - CS0618: an `[Obsolete]` forwarder.
  - CS0219: a dead local.
- [DISCOVERED] Bit-exact constants and operation order matter. `0.388999998569489f` is defined four times (#47). Squared versus non-squared comparisons, and `<` versus `<=`, vary from site to site (#1, #82).
- [DISCOVERED] Small confirmed bugs are ready for a test-first fix: #116, #131, #93, #118, #64, #15, #168, #210, #220, #132 (plan section 3).
