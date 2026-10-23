# Conventions decision

Status: **Accepted** ([ADR 001](../../adrs/001-naming-conventions-and-enforcement.md)). The published standard is
[docs/conventions.md](../../../docs/conventions.md), and the root [CLAUDE.md](../../../CLAUDE.md) carries the short form.

## What was decided

1. **The standard is the measured majority.** Plan section 2 ([plan](../../reviews/2026-10-22-refactor-audit-plan.md)) became
   the table in `docs/conventions.md`, with its counts and exceptions. No rule was chosen against the dominant usage.
2. **Exceptions are named, with reasons:**
   - feature-folder namespace collapse (SpellSystem partials, FullName-ordered discovery, QA log-tap strings)
   - `ServerConsole` for `Console` folders (avoids shadowing `System.Console`)
   - `Ai` for infrastructure and for the legacy `EventAi`/`SmartAi` folders and their configuration section
   - `t_` for `[ThreadStatic]` fields
   - multi-type script bundles
   - zone-script batch test names
3. **New and changed code follows the standard. Existing code is not mass-renamed.** The deferred renames and their hazards are
   listed in `docs/conventions.md`.
4. **`.editorconfig` encodes the rules at suggestion severity**, never stronger, until the tree is clean for a rule:
   - static readonly → PascalCase
   - const → PascalCase
   - mutable statics → unchecked (a naming rule cannot see `[ThreadStatic]`)
   - IDE0011 braces

   The existing rules and severities are unchanged.
5. **IDE0130 (namespace matches folder) is not turned on.** Expressing the collapse exception needs one `.editorconfig` section
   per collapsed folder. Revisit when ratcheting.

## Ratchet procedure (for a later lane)

For one rule at a time:
1. Fix the whole tree for that rule in a behaviour-neutral commit, using the rename checklist.
2. Raise the rule to `warning` in the same PR.
3. Run a full Release build. Under `TreatWarningsAsErrors` that build proves the tree is clean.

Naming rules also need `dotnet_diagnostic.IDE1006.severity` raised before the build enforces them.
