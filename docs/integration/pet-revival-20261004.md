# Existing summoned pet revival — 2026-10-04

Effect 113 (`RESURRECT_NEW`) now restores the existing current summoned pet through
the normal Game spell engine and World `CMSG_CAST_SPELL` handler. The behavioral
reference is vmangos commit `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`,
`SpellEffects.cpp:209-263`; the behavior was independently implemented in C#.
No upstream code or proprietary content was copied, and no schema, pet store,
configuration switch or availability producer was added.

## Delivered behavior

The target must be a retained in-world corpse registered in its creature and pet
systems, with `SummonKind.Pet`, coherent current owner `PetGuid`, and exact live
map object identities. The caster can be a different unit from the owner.
Settlement holds on either player block the entire transition until the spell
is retried; transit also blocks the transition. Ordinary cast checks and spell
knowledge remain in charge.

The same pet object and GUID are moved to the caster's position and orientation.
Existing observers receive source-compatible `MSG_MOVE_TELEPORT` destination
packets before and after relocation, including when zero health prevents a later
AI movement update. The packet uses the existing packed-GUID movement builder.
Its dynamic flags and skinnable flag are cleared, both death states become alive,
and flat health is capped by its maximum. The source converts the signed computed
value to unsigned before clamping: a negative value consequently reaches the
maximum, while zero remains zero. Replay checks use the death states, including
an alive pet with zero health; `Unit.IsAlive` still additionally requires positive
health.

Combat relations, stale motion, per-fight AI state and corpse timers are cleared.
The pet receives a fresh `PetAI` linked to the current spell system and resumes
combat ticking so partial health can regenerate. Its action bar, learned spells,
cooldowns, powers and exact death-persistent aura holders survive. The path does
not invoke ordinary creature respawn, reset template fields, or recast respawn
passives. Modifier flags remain owned by their aura handlers because the current
model has no separate upstream `UnitState` word.

After revival, every owner spell carrying `OverrideClassScripts` with
`MiscValue == 2228` is removed through normal aura handlers, following Demonic
Sacrifice's 1.12 rule. Other override scripts and the foreign caster's own aura
remain. No player resurrection offer or pet action-bar replacement is sent.

## Regression evidence

Before implementation, the parent coordinated a clean Game run of the initial
11 cases: six restoration cases failed on the missing pet branch and five guards
passed. Both World cases compiled and failed because ordinary client casts left
the pet in its corpse death state. The final regression set contains 13 Game
cases and two World cases, adding the signed health boundary and preservation of
an exact death-persistent stat aura holder. Game coverage includes health caps,
zero and alive replay, corpse lifetime, powers, fresh AI, sacrifice filtering,
foreign-owner settlement retry, stale current-pet links, other summon kinds and
effect-18 exclusion. A subsequent parent run verified the four observed-corpse
cases failed on the missing teleport publications (expected two packets, received
zero), before the publication fix landed. Those cases decode the pet GUID and
destination movement block. The World cases learn a synthetic spell, send a
normal cast, and verify the existing pet's live state and movement publication
without a player offer. They capture exact restored health in the normal
post-effect `SpellHit` event, then permit ordinary regeneration while collecting
the client's packets.

The parent coordinates all builds and execution of the integrated test suite;
this implementation agent did not run a separate compiler or test process.
Final results are recorded in the [combined milestone handoff](server-item-death-20261004.md).

## Bounded scope

This branch deliberately supports only the current controlled `SummonKind.Pet`.
Upstream `ToPet()` also represents guardians and mini pets; that wider subtype
behavior remains outside this bounded implementation. Wild summons, ordinary
creatures, removed corpses, dormant pet loading and effect 109 (`SUMMON_DEAD_PET`)
are not restored by this branch.

Upstream saves the restored pet as current. ArcaneCore has no persistent pet
instance store or cached hunter-pet model, so no substitute save is invented:
this revival remains in the current map session and does not promise survival
across owner logout, map departure or server restart. Real spell templates and
build-5875 client movement acceptance still require proprietary content and an
actual client.
