# Staff engineer: decisions and observations

Source: [plan](../../reviews/2026-10-22-refactor-audit-plan.md) section 2 and the [findings](../../reviews/2026-10-22-refactor-audit-findings.md).

## Decisions

- [Conventions](conventions.md): the naming standard, its exceptions and how it is enforced. Recorded as [ADR 001](../../adrs/001-naming-conventions-and-enforcement.md) (Accepted). The published form is [docs/conventions.md](../../../docs/conventions.md).

## Observations

- [DISCOVERED] The dominant conventions are strong:
  - `_camelCase` fields (6,393)
  - `*Store` (183; 0 `*Repository`)
  - `*Options` (194)
  - `Async` suffix (1,926 vs 28)
  - `Id`/`Guid`/`Npc` casing
  - `Subject_Behaviour` test methods (16,864 of 18,290)
- [DISCOVERED] `.editorconfig` declared `_camelCase` for private fields but had no rule for statics, while the tree uses PascalCase for static readonly fields (447) over `s_` (188) (#216). Three finder findings got the dominant style wrong and were refuted (#13, #62, #91).
- [DISCOVERED] About 20% of `src` files declare a namespace that differs from their folder. Most of these are deliberate collapses, which `SpellSystem` partials and FullName-ordered discovery depend on (#214, #10, #26, #49). They are documented as an exception, not renamed.
- [DISCOVERED] Whether a type uses `AI` or `Ai` follows its role (scripts vs infrastructure), not chance (refuted #27, #78). The 70 `Ai` script types stay.
- [DISCOVERED] Most of the de-duplication value is in families of copy-pasted helpers: rand_dither ×17, 3D distance ×15+, Due ×14, team-from-race ×5, write queues ×7 (#0, #1, #82, #33, #66, #94). In every family the copies differ in small ways from site to site, and a shared helper must keep those differences.
