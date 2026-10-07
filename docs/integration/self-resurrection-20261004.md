# SELF_RESURRECT effect 94 — 2026-10-04

This continuation implements effect 94 through the ordinary Game spell engine and
the production World cast handler. The source reference is vmangos commit
`0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`, `SpellEffects.cpp:5334-5370`;
behavior was independently implemented in C#. No source or content assets are
vendored, and no schema or configuration switch is added.

## Behavior and persistence

A negative calculated effect value supplies flat health through its negation and
flat mana through `MiscValue`. A nonnegative value supplies a percentage of both
maximum health and mana. Fractions use stochastic rounding, and results are
bounded by the current maxima after the shared revival removes ghost state.
Negating `int.MinValue` is safe, large percentages are bounded, and negative flat
mana cannot wrap to a large unsigned amount. Rage is emptied and energy filled.

Only a dead player still in its owning map can be restored. Settlement holds
block the effect. Shared revival clears ghost/root state and any external
resurrection offer, and body removal delegates to the corpse's owning map,
including a body on a different map from its spirit. The effect restores the
player at its current position and does not request a teleport.

The completed effect raises `MapCombat.PlayerSelfResurrected` after powers and
corpse cleanup. `SelfResurrectionFeature` subscribes existing and newly created
maps and saves the matching online player through the normal snapshot queue.
Subscriptions are removed when a map unloads and when the feature is disposed.
Repeating the effect on an already alive death state leaves vitals unchanged and
does not create another resurrection save.

Existing cast checks remain in charge: a dead caster still needs
`AllowCastWhileDead`, and a normal client cast still requires a known spell.
The handler grants no spell or release-dialog availability. No general caster
restriction bypass is introduced.

## Zero-value boundary

Pinned upstream accepts a 0% value and leaves the player in the `Alive` death
state with zero health. This effect preserves that result without inventing a
minimum health amount. ArcaneCore's existing `Unit.IsAlive` additionally checks
positive health, so that property remains false for this malformed or unusual
template. The effect's replay guard follows the death state, and World still
saves the completed transition. External resurrection offers and acceptance
also reject an `Alive` death state, including zero health. Production spell
templates require real DBC validation; this boundary is not an availability
producer or an instruction to grant zero-value spells.

## Regression evidence and validation

Before production changes, the initial 12 Game cases compiled and ran: nine
restoration cases failed on the missing effect handler, and three existing
guard cases passed. The completed regression set contains 15 Game cases and
two World cases, covering percentage and flat values, safe boundaries,
controlled fractional rounding, maxima changed during revival, cross-map
body cleanup, dead creature exclusion, offline/alive exclusion, settlement,
source caster attributes, zero-value replay and external-offer exclusion.

World cases send ordinary `CMSG_CAST_SPELL` for an explicitly learned synthetic
spell, restore both unreleased and released players, verify the durable life
snapshot, reject restoration replay while alive, and log out and back in.
Parent-coordinated combined validation and its exact results are recorded in
the [continuation handoff](server-revival-20261004.md); no tests or builds were run independently by this
implementation agent.

## Remaining acceptance

Release-dialog `CMSG_SELF_RES`, `PLAYER_SELF_RES_SPELL` availability, Soulstone
and Reincarnation producers, and their specific reagent/cooldown logic are
separate follow-ups. This slice provides the restoration effect for valid
templates and existing cast entry points; it does not make any resurrection
spell freely available. Pet resurrection is separate. Actual build-5875 client
acceptance, proprietary spell data, and external provider verification remain
pending.
