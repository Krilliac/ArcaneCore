# Warlock, mage and utility spells (wave 4 lane "warlock-mage-utility")

Status: in progress on branch `claude/vw5-warlock-mage-utility` (base `claude/vw4-integration` 7313b9e). Each section below is one
delivered slice with its scope, limits, provenance and the file list. Nothing is copied from the reference servers; every formula
cites the vmangos source it was checked against (`D:\refs\vmangos`, GPL, read-only).

Slices the design listed and this lane did not deliver are in "Not delivered" at the end, with the reason.

## wlm-02 Spell scripts (`Game/Spells/Scripts`, `World/Spells/Utility/SpellScriptFeature.cs`)

vmangos keeps class logic in `SpellScript` objects keyed by spell id (`src/scripts/spells/spell_warlock.cpp`, `spell_mage.cpp`).
`ISpellScript` + `[SpellScript(ids...)]` + `SpellScriptRegistry` (reflection discovery, one script per spell id, a duplicate id,
a missing attribute or an empty id list throws) + `SpellScriptDispatcher` mirror it without editing `SpellSystem`:

| Hook | Where it runs | vmangos |
|---|---|---|
| `OnCheckCast` | `ISpellCastCheck`, phase `Final`, order `int.MaxValue` (after every other check, also for triggered casts) | Spell.cpp:6480-6481 |
| `OnCast` | `ISpellCastObserver.OnCast`: power and ammo are taken, targets and effects not yet run | Spell.cpp:3716-3724 |
| `OnEffectExecute` | chained DUMMY (3) and SCRIPT_EFFECT (77) handlers; runs before the previously installed handler | Spell.cpp:5254-5257 |
| `OnSuccessfulDispel` | chained DISPEL wrapper: only when the target lost at least one aura stack | scripts/spells |

SCRIPT_EFFECT has no behaviour of its own in vmangos (SpellEffects.cpp:3685-3700, :4560-4570 fall through to the database script
table, which is empty here), so the dispatcher installs a no-op for it: the mage Teleport spells and the Create Healthstone ranks
no longer log "effect 77 is not implemented". Chaining keeps any handler another feature installed before the dispatcher; a feature
that replaces DUMMY, SCRIPT_EFFECT or DISPEL after it without chaining would drop the scripts (the world feature attaches after
`SpellFeature`, alphabetically).

Limits: `OnEffectExecute` is raised for DUMMY and SCRIPT_EFFECT only (vmangos raises it before every effect; no script of this lane
needs the others). vmangos `OnSummon` is not provided because nothing here raises it yet. When the crafting lane's cost hook
(`ISpellCostTaker`) merges, script `OnCast` must stay after it (a reagent failure must never fire a script); the integrator checks
the observer order. No script ships in this slice: the scripts of later slices register their ids with the attribute.

Tests: `tests/ArcaneCore.Game.Tests/Spells/Utility/SpellScriptTests.cs` (12 tests: hook order, the late-check ordering, power already
taken at `OnCast`, no unsupported-effect log, dispel only on removal, duplicate/unknown/unmarked scripts, double install, chaining
before and after).
