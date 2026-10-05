# Server death and item continuation, 2026-10-04

Continues the [revival milestone](server-revival-20261004.md) in
`/workspace/arcanecore-work`, branch `codex/wave3-escrow-loot-cleanup`, based on
`7313b9eb089197b253dc51bd8bb5ceb803adcc6b`. The user requested continued server
development with agent fanout. Three implementation agents and a read-only
reviewer completed independent areas; the parent integrated their changes and
scheduled verification. Earlier local edits are preserved. Everything remains
local and uncommitted.

## Implemented behavior

Resurrection effect 113 restores the retained corpse of the current controlled
summoned pet. It caps flat health, clears old combat/motion/AI state and corpse
timers, and installs a fresh `PetAI`. The same object, action bar, powers,
cooldowns and death-persistent aura holders survive. Observers receive the
existing build-5875 teleport packet before and after relocation. The owner's
Demonic Sacrifice override auras are removed through ordinary handlers.
Settlement holds and exact current map/pet identity protect the transition.
See [pet revival evidence](pet-revival-20261004.md).

Ordinary PvE player death applies 10% equipment durability loss once and sends
one empty `SMSG_DURABILITY_DAMAGE_DEATH` to the victim. The caller's suppression
flag, source spell's `NO_DURABILITY_LOSS` attribute, resolved player owner or
charmer, and battleground map classification control eligibility. Existing
party credit and corpse PvP attribution are unchanged. The notification still
follows the source when item mutation is disabled by configuration. Backpack
and bag contents do not receive the death penalty. Normal snapshots preserve
the changed item durability across disconnect and relog.
See [death durability evidence](death-durability-20261004.md).

Durability effects 111 and 115 now dispatch signed points or percentage loss to
a selected equipment/bag slot, all equipment, or equipment plus carried
contents. They share the existing inventory option and persistence path.
Breaking equipment unapplies bonuses before durability reaches zero; signed
repair reapplies bonuses after a broken item becomes usable. No duplicate
bonus contribution is created. Source selector bounds and the unusual
negative-selector/zero-percent branch ordering are preserved.
See [durability spell evidence](durability-spells-20261004.md).

The spell damage seam now carries the caller's durability flag into map combat.
Instant kill and flat/percent redirected damage explicitly suppress death wear,
while other effects in a mixed spell retain their ordinary behavior. Existing
environmental damage suppresses the combat penalty and applies its own once.
Default interface forwarding preserves compatibility with health-only sinks.

No schema migration or new configuration switch is introduced. References are
pinned to vmangos `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`; implementations are
independent C#. The empty death notification is corroborated by wow_messages.
Upstream source and proprietary content are not vendored.

## Regression and review evidence

Before implementation, six of the initial 11 Game pet cases failed on the
missing revival branch; five guards passed. Both World pet cases failed on
the unchanged corpse state. The initial 11 Game death-durability cases had
five missing-penalty/notification failures and six guard passes; three of five
World cases failed, with exemption and instant-kill guards already passing.

After correcting fixture assumptions, the separate durability baseline ran
32 Game cases with 20 expected failures and 12 passes. It demonstrated missing
effects and both shared API stat transitions independently. The two World
cases failed because client casts left item durability unchanged. Compiler-only
fixture fixes and assumptions about fist damage or held-target errors are not
counted as product findings.

Three routing regressions demonstrated missing caller-specific suppression
for instant kill and the two split-damage types. Review also found that the
revived pet's server relocation lacked an observer packet. Four expanded pet
cases reproduced the absent packets, including an `Alive` zero-health pet whose
AI could not later correct its client position. The existing packet builder
now publishes the source's observer transition. Independent final review found
no remaining blockers.

The first focused World run caught a timing assumption: a partial-health pet
regenerated before its health was sampled after socket collection. The fixture
now captures exact restoration in the production spell-hit event, then permits
subsequent ordinary regeneration. This required no production change.

## Validation

The combined Release solution builds with zero warnings and errors. Focused
Game checks pass **293/293** and focused World checks pass **31/31**, including
existing pet, combat, environmental, duel, item, mitigation and resurrection
regressions. This tranche adds 62 Game cases and nine World cases; synthetic
content is used through both the engine and production host.

The build-5875 mock client self-test passes **59/59 checks**, receiving 148
frames. The full Release solution passes **14,240 tests**, with **eight
fixture-dependent skips** and no failures. This adds 71 passing cases to the
preceding milestone of 14,169. No skip is counted as a pass.

| Test project | Passed | Skipped |
|---|---:|---:|
| Cryptography | 8,017 | 0 |
| Data | 796 | 6 |
| Game | 3,849 | 1 |
| MockClient | 195 | 0 |
| Realm | 37 | 0 |
| World | 1,346 | 1 |

Commands use .NET SDK 10.0.301 from `global.json`:

```sh
dotnet build ArcaneCore.slnx -c Release --no-restore -m:1 --verbosity minimal
dotnet test ArcaneCore.slnx -c Release --no-build --no-restore -m:1 --verbosity minimal
dotnet run --project tools/ArcaneCore.MockClient -c Release --no-build -- self-test
git diff --check
```

Logs are in `/workspace/scratch/`: `item-death-release-build.log`,
`item-death-focused-game.log`, `item-death-focused-world.log`,
`item-death-release-tests.log` and `item-death-mock-self-test.json`.
Failure baselines are `item-death-game-baseline.log`,
`item-death-world-baseline.log`, `item-durability-baseline.log` and
`item-death-wire-baseline.log`.

All final validation commands exited successfully. `git diff --check` and local
handoff links are clean. The skipped cases require proprietary DBC/world data
or the configured provider race fixture; no external provider matrix is
configured in this workspace.

## Acceptance boundaries

Pet revival supports only the current `SummonKind.Pet`. Upstream's broader pet
subtypes include guardians and mini pets; those branches remain outside this
slice. The persistent hunter-pet cache/store and effect 109 are absent, so
revival does not preserve a pet across owner logout, map departure or restart.
Negative flat health follows upstream's unsigned conversion and cap, while
zero health still completes the `Alive` state and cannot replay the effect.

Durability effect 111 does not add upstream's spell execute-log entry because
the general execute-log path is absent. Random hit wear and durability
insurance remain separate. Battleground classification does not implement
membership or matches. The existing environmental spell effect approximation
is outside this continuation's fidelity claim.

Actual build-5875 client acceptance, proprietary DBC/world content and the
external MariaDB/PostgreSQL provider matrix remain pending. Release-dialog
self-resurrection availability, Soulstone/Reincarnation producers and their
reagent/cooldown rules remain recorded in the preceding revival handoff.
