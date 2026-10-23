# Security: decisions and observations

Source: [plan](../../reviews/2026-10-22-refactor-audit-plan.md) section 3, [findings](../../reviews/2026-10-22-refactor-audit-findings.md).

## Observations

- [DISCOVERED] Gateway ban lookups do not pass the cancellation token (#152). The World sibling, `WorldSession.IsBannedAsync`, has no token parameter at all. The fix must not fail open.
- [DISCOVERED] Data's `RepositoryPathGuard` does not resolve symlinks or junctions, unlike the Atlas path guards (#208, #202). Fixing this changes what paths are allowed, so test both directions.
- [DISCOVERED] `RealmListWriter` truncates the realm count and the body length without a guard (#221, #154). A count guard keeps valid output byte-identical. The realm list always reports 0 characters, a known gap that needs a characters-DB source.
- [DISCOVERED] The bounded line readers do not agree on whether a trailing CR counts toward the line limit (#209). The fix keeps the in-progress bound and the order of the IAC/NUL filtering.
- [DISCOVERED] When gh CLI output exceeds its cap, the failure is reported as a timeout and a faulted task is left unobserved (#219).
- [DISCOVERED] Apart from these, wire sizes are bounds-checked before allocation, and text that may carry a secret is logged as the exception type only (xc-bugs summary, and the policy in `docs/ops/exception-audit.md`).
