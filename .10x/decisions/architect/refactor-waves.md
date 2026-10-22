# Refactor waves

Status: **awaiting the maintainer's approval** ([ADR 002](../../adrs/002-refactor-wave-strategy.md), Proposed). This is the summary.
The per-item owner files, hazards and acceptance checks are in [plan](../../reviews/2026-10-22-refactor-audit-plan.md) section 4.
Finding numbers refer to the [findings](../../reviews/2026-10-22-refactor-audit-findings.md).

## Pipeline per item

implementer → build-runner (full solution Release build with warnings as errors, plus the named `tests/ci/*.slnf`) → reviewer.
Wave 2 items put characterisation tests (test-author) first. Before any rename or move, grep `src`, `tests`, `docs`, `tools`,
`scripts` and `.github`, excluding `.claude/worktrees/`.

## Waves

| Wave | Theme | Items | Risk | Gate |
|---|---|---|---|---|
| 0 | Guards and decisions | 0a conventions and `.editorconfig` at suggestion level (done in the conventions lane, [ADR 001](../../adrs/001-naming-conventions-and-enforcement.md)); 0b namespace-edge freeze test built from IL type references (#9) | low | 0a: full build green with zero `.cs` diff. 0b: World.slnf, and the test must fail on an injected probe edge |
| 1 | Mechanical de-duplication, disjoint files, run in parallel | 1a GridCoord (#16, #83) · 1b InventoryResult (#19) · 1c DefaultBoundingRadius (#47) · 1d TeamRules (#66, #70, #69) · 1e stat curves (#87) · 1f SecsToTimeString in Kernel (#95) · 1g periodic damage log, golden bytes first (#3) · 1h private helpers (#34, #48, #63, #23) · 1i ConfigChecks (#109, #110) · 1j TryParseEnumName (#104, #105) · 1k PlayerbotGeometry (#135, #136, #134) | low | the named slnf, with test counts > 0 and unchanged |
| 2 | Parameterised shared helpers, characterisation tests first | 2a WorldGeometry (#82, #1, #20, #56, #33) · 2b rand_dither variants (#0) · 2c power/wand/ledger (#2) · 2d ScriptTimers (#33, #22) · 2e DialogueRunner (#21) · 2f CombatActionHelpers (#32) · 2g ImportTransaction (#119, #123, #122) · 2h DBC field checks and namespace move (#121, #124) · 2i transport anomalies (#44, #45) · 2j CharacterKeyedWriteQueue (#94) · 2k CostBoundedLazyCache after #168 (#157) · 2l WowAuthDigest/WorldFrame (#143, #195) · 2m InstanceAttachments (#67) | medium | seeded tests unchanged; one folder or lane per PR |
| 3 | Partial-file and type moves, behaviour-neutral, one commit each (for blame-ignore-revs) | 3a Map partials (#39) · 3b WorldRuntime partials (#40) · 3c InstanceManager partials (#30) · 3d SpellSystem member moves and Cast helpers (#6 stage 1, #7) · 3e file/type splits (#25, #215, #11, #97, #76, #80, #101) · 3f ClientRejection to Game/ClientErrors (#41) · 3g BlackwingLair constants (#36) · 3h Kernel layering (#111, #149) | low to medium | per-project slnf; regenerate `docs/ops/exception-audit.md` after moving code |
| 4 | Test infrastructure | 4a `tests/ArcaneCore.TestSupport` (#177, #171–#174, #196, #197) · 4b test namespaces and helper names (#182, #179, #180, #181) | medium | all four slnf; per-project test counts unchanged |
| 5 | Build and graph hygiene | 5a central package management (#189) · 5b InternalsVisibleTo into csproj (#186) · 5c policy note only (#188) | medium | clean `dotnet restore --locked-mode`, all slnf |

## Order and parallelism

- Wave 0b lands before any Wave 2 or 3 item that touches Spells, Combat, Creatures, Pets, Items or Instances.
- Wave 1 items own disjoint files and can run as parallel lanes. Within Wave 2, items that share a file (2a with 2d over the
  distance helpers, 2c with 1h around MapCombat) run in sequence.
- Wave 3 runs after Waves 1 and 2 have settled for the same files, so blame-ignore commits stay purely about moves.
- Bug fixes (plan section 3) are not part of any wave and go ahead now.

## Explicitly deferred

Collaborator extraction from the god classes (#17, #85, #57, #211), namespace realignment and suffix renames, the Gateway → World
split, Playerbots restructuring, CLI consolidation, the deploy-script rewrite and the small-primitive merges. Reasons are in plan
section 5 and [ADR 002](../../adrs/002-refactor-wave-strategy.md).
