# Reincarnation and spell reagents — 2026-10-04

This goal continuation extends the recovered local candidate on
`codex/server-continue-20261004`, based on `7313b9eb089197b253dc51bd8bb5ceb803adcc6b`.
The prior Soulstone milestone passed 14,262 local tests, six skips, and the
59-check mock scenario. All previous work is preserved and remains uncommitted.

## Implemented

The existing Spell.dbc importer and database row already carry eight signed
reagent IDs and eight unsigned counts. The runtime spell factory now preserves
those exact slots, with an immutable copy in `SpellInfo`. No schema migration
or content substitution is needed. Runtime casts validate carried reagent
counts at preparation and completion; bank items cannot pay the cost. The
entire cost is staged before power, cooldown or effect publication. Removal
uses the existing detached inventory operation with no grants; all stacks
change before count observers run, and existing item identities are preserved.

Cancelled casts consume nothing. A reagent removed during a cast prevents the
effect at completion. Negative and zero IDs or zero counts are ignored.
Duplicate slots are aggregated; invalid combined counts cannot wrap, partly
consume, or grant an unpaid effect. Counts above the inventory notification's
signed-int bound are rejected as invalid content.

The originating spell/aura travels with triggered casts. A standalone server
trigger skips its own reagents; a child skips duplicate costs when its master
has a nonzero reagent in slot zero. If the master has no reagent in that slot,
the child pays its own cost. Actual trigger effects and periodic aura ticks
forward the origin. Foreign trade targets still require reagents. Next-swing
casts reuse the same final check and cost stage when the swing fires.

Reincarnation is offered at death when the player knows passive `20608`,
internal effect `21169` is ready, and an Ankh `17030` is carried. Soulstone
retains priority. The empty self-resurrection request uses an ordinary cast,
so its imported reagent cost and cooldown apply. The changed inventory and
life are captured by the existing revival save. Socket tests verify Ankh
consumption, saved life, logout/relog inventory, and restored cooldown.

## Grounding

All formulas and protocol behavior use the existing pinned local references:

- vmangos `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`,
  `SpellEntry.h:636-637` (DBC columns 42-49 and 50-57),
  `Spell.cpp:5082-5131,7070-7081,7249-7279,3716-3718`,
  `Player.cpp:19934-19939`.
- The prior Soulstone handoff grounds the empty build-5875 `CMSG_SELF_RES`
  and its private field; no wire value changed here.

References were read to independently implement C# behavior. Upstream code and
proprietary content are not vendored.

## Verification and review

The initial Game baseline reproduced seven behavior failures and passed eight
guards. The implemented expanded focused run passes 46 Game cases, and 13
World cases pass, including both new Reincarnation socket paths and the
eight-slot production factory test. This tranche adds 28 Game and three World
cases. The integrated Release solution built with zero warnings/errors.
The final full suite passes 14,293 tests with six skips and no failures:
Cryptography 8,017; Data 797 (five skips); Game 3,896; MockClient 195;
Realm 37; World 1,351 (one skip). The standalone build-5875 mock passes 59/59
checks and receives 148 frames. Unconfigured external-provider members are
not passes.

One final bounded read-only review found no concrete correctness blockers.
It checked held-state and callback ordering, complete reagent staging, item
identity, bank exclusion, normal and triggered casts, next-swing costs,
Reincarnation eligibility, cooldowns and snapshot ordering.

The currently supported trade target has no resolved owner in
`SpellCastTargets`; triggered `TradeItem` targets are conservatively treated
as foreign. Cast-item charge adjustment is not modeled by the current cast
context. Extending those pre-existing item-cast seams must pass the upstream
owner/charge rules through the cast context rather than guessing them.

## Remaining goal work

Twisting Nether death selection, pending self-resurrection offers across dead
relog/restart, database-backed pet recovery and effect 109 remain. Real client,
full content and external MariaDB/PostgreSQL qualification remain pending.
The wider roadmap and M15 clustering are not proven complete by these tests.

The active goal remains open. This run is bounded to 60 minutes; source changes
remain local and uncommitted. No push, merge, deployment, database upgrade or
client configuration change occurred.
