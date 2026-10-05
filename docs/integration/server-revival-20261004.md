# Server revival continuation, 2026-10-04

Continues the [spell flow milestone](server-spell-flows-20261004.md) in
`/workspace/arcanecore-work`, branch `codex/wave3-escrow-loot-cleanup`, based on
`7313b9eb089197b253dc51bd8bb5ceb803adcc6b`. The user requested continued server
development with agent fanout. Three implementation agents and an independent
reviewer completed this tranche, with shared integration and validation scheduled
by the parent. All changes remain local and uncommitted alongside earlier work.

## Implemented behavior

Self-resurrection effect 94 restores a dead player at its current location through
the existing cast engine. Negative values specify flat health and mana; other
values specify percentages. Fractional amounts use stochastic rounding, with
results bounded by the maxima after revival hooks. Shared revival clears ghost
and root state and external offers, removes the body through its owning map,
resets rage and fills energy. The World feature saves the finished transition
through the normal character snapshot queue. Normal spell knowledge, dead-caster
attributes and settlement checks still apply. An `Alive` death state prevents
replay and external resurrection offers even when a zero-value spell restored
zero health. See [self-resurrection evidence](self-resurrection-20261004.md).

Ghost aura 95 updates the unit visibility bit and player ghost flag while
preserving unrelated bits. Production combat hooks cast imported 8326 and,
when the character knows 20585, 20584. They preserve existing custom combat
callbacks and the content-free movement fallback. Persisted ghost login reuses
the permanent self-owned form already rebuilt from death state, avoiding a
water-walk apply/remove/apply sequence. Unrelated saved auras still restore.
Fallback movement checks the latest pending order to avoid a duplicate.
Public aura lookup/removal also checks exact target identity, and removal waits
for callers to retry after settlement holds. See
[ghost form evidence](ghost-form-20261004.md).

A dying caster's owned Hunter's Mark is removed through normal aura handlers.
Ownership and target identity prevent historical caster GUIDs or replacement
objects from taking over cleanup. A settlement hold defers removal of the exact
captured holder until it is safe. Creature corpse disposal and respawn now
unapply old-life aura contributions before spell-state forgetting or fresh AI
initialization. This prevents retained stats from accumulating across lives,
including a script's aura applied during the invisible `DEAD` interval. See
[death aura lifecycle evidence](death-aura-lifecycle-20261004.md).

No schema migration or configuration switch is introduced. Behavioral references
are pinned to vmangos `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa` and independently
implemented in C#. Upstream source and proprietary content are not vendored.

## Regression and review evidence

The initial self-resurrection baseline failed nine restoration cases because the
effect handler was absent; three existing guards already passed. The ghost
handler baseline failed three missing behavior cases. Integration regressions
also demonstrated aura mutation during a settlement hold and an old target
object removing a same-GUID replacement's holder before the shared API guards.

The World baseline exposed the duplicated ghost hydration movement orders and
both creature stat leaks. After corpse disposal, a fresh 11-point aura produced
29 strength because the previous holders' 18-point contribution had been
forgotten without unapplying handlers. Race-four login failures were invalid
fixture assumptions; valid human/orc fixtures now test the Wisp spellbook
predicate independently of race.

Independent review covered zero-health revival, ghost hydration and movement,
settlement holds, stale ownership and identity, selective Mark cleanup, and
cleanup before respawn AI. No blocking findings remained after integration.

## Validation

The Release solution build passes with zero warnings and errors. Focused Game
checks pass **76/76** and focused World checks pass **21/21**, including the prior
resurrection, Grounding and totem immunity flows. This tranche adds 30 Game cases
and 11 World cases.

The full Release solution passes **14,169 tests**, with **eight fixture-dependent
skips** and no failures. This adds 41 passing cases to the preceding milestone
of 14,128. The build-5875 mock client self-test passes **59/59 checks**, receiving
148 frames. No fixture-dependent skip is counted as a pass.

| Test project | Passed | Skipped |
|---|---:|---:|
| Cryptography | 8,017 | 0 |
| Data | 796 | 6 |
| Game | 3,787 | 1 |
| MockClient | 195 | 0 |
| Realm | 37 | 0 |
| World | 1,337 | 1 |

Commands use .NET SDK 10.0.301 from `global.json`:

```sh
dotnet build ArcaneCore.slnx -c Release --no-restore -m:1 --verbosity minimal
dotnet test ArcaneCore.slnx -c Release --no-build --no-restore -m:1 --verbosity minimal
dotnet run --project tools/ArcaneCore.MockClient -c Release --no-build -- self-test
git diff --check
```

Logs are retained in `/workspace/scratch/`: `revival-release-build.log`,
`revival-focused-game.log`, `revival-focused-world.log`,
`revival-release-tests.log` and `revival-mock-self-test.json`.
Failure baselines are retained separately as `revival-game-baseline.log`,
`revival-lifecycle-baseline.log`, `revival-world-baseline.log` and
`revival-ghost-guards-baseline.log`.

All final commands exited successfully. `git diff --check` and local handoff
links are clean. The skipped cases require proprietary DBC/world data or the
configured provider race fixture; the external provider matrix remains
unconfigured in this workspace.

## Acceptance boundaries

Release-dialog `CMSG_SELF_RES`, `PLAYER_SELF_RES_SPELL` availability, Soulstone
and Reincarnation producers and their reagent/cooldown rules remain follow-ups.
Pet resurrection is separate. Zero-health `Alive` retains the existing
`Unit.IsAlive` distinction rather than changing that shared property.

Hunter's Mark cleanup uses the known Hunter family/flag plus `MOD_STALKED`.
ArcaneCore does not yet import the database `Custom` single-target flag or own
the general single-cast registry; arbitrary custom stalked spells and rank
exclusivity require that content/model work. Existing creature corpse-disposal
timing is preserved, earlier than upstream's respawn-time clear. Generic
spell-state forgetting and out-of-world aura retention are unchanged.

Tests exercise synthetic content through the Game engine and production World
socket host. Actual build-5875 client rendering and acceptance, proprietary
DBC/world data, real night elf starting spell content, and external
MariaDB/PostgreSQL provider runs remain pending.
