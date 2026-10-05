# Durability spell effects — 2026-10-04

This continuation adds discovered handlers for `DURABILITY_DAMAGE` (111) and
`DURABILITY_DAMAGE_PCT` (115). They use the existing player's inventory durability
operations, so item update fields, inventory snapshots, durability configuration,
and equipped item contributions share the normal inventory path. No schema or
content migration is required.

## Source contract

The reference is vmangos `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`,
`SpellEffects.cpp:5576-5640` and `Player.cpp:4794-4898`. The implementation is
clean-room C#; reference source and content are not vendored.

- Only player unit targets are affected. Ordinary cast validation, target
  resolution, effect application, and quest settlement mutation holds remain
  authoritative.
- A nonnegative miscellaneous value selects the player's own equipment or
  equipped bag slot; values at or beyond `INVENTORY_SLOT_BAG_END` (23) and empty
  slots do nothing. Direct backpack, bank, or keyring positions are outside this
  bound. Selecting an equipped bag is within the source bound even though real
  bag templates ordinarily have no durability.
- A selector of `-1` applies to equipment. Values below `-1` additionally visit
  backpack and equipped bag contents. Bag objects, bank items, bank bags and their
  contents, and keyring entries are omitted by the existing traversal.
- Flat damage passes the signed effect value to the inventory primitive. Positive
  points reduce durability, negative points repair it, and the result is clamped
  to zero through the item's maximum. Breaking equipped items removes their
  contributions before setting durability to zero. Repairing an equipped broken
  item reapplies contributions after its durability becomes positive.
- Percentage damage divides the effect value by `100.0f` and calls the existing
  maximum-durability percentage primitive. Its minimum loss is one point.
  Selected slots reject nonpositive percentages, whereas negative selectors call
  the all-items primitive before that check. The zero-percent all-equipment case
  consequently loses one point per durable item, matching the source branch
  ordering. Negative percentages with negative selectors preserve the existing
  primitive's behavior; exact parity with C++ out-of-range unsigned floating
  conversions is not claimed.

## Regression coverage

`DurabilitySpellTests` exercises both handlers through normal known-spell cast
requests, selected slots, source selector bounds, inventory traversal exclusions,
percentage rounding and clamping, signed repairs, disabled durability loss,
nonplayer targets, and held casters or targets. Synthetic durable bags and keys
make traversal exclusions observable without requiring proprietary content.

`DurabilityPointStatTests` independently checks the shared durability primitive:
equipped weapon stats are removed when it breaks, restored exactly once when
signed points repair it, and unchanged when already broken or loss is disabled.
These cases isolate the inventory defect from missing spell handler behavior.
Removing a worn weapon clears its raw weapon damage entry to zero; an item
loaded already broken retains the initial fist entry of one. Public damage
calculation applies its unarmed fallback separately, so those raw states are
intentionally distinct.

`DurabilitySpellWorldTests` sends real `CMSG_CAST_SPELL` packets for known flat
and percentage spells, checks the owned item's durability update field and player
snapshot, then verifies saved durability after logout and relog. Item persistence
uses the existing in-memory World inventory store adapter; provider-backed and
actual client acceptance remain separate.

The parent schedules combined builds and regression runs to avoid concurrent
compiler output writes. Final commands and results belong to the continuation's
combined integration handoff.

Before production changes, the corrected Game regression baseline compiled and
ran **32 cases: 20 failed, 12 passed** (`/workspace/scratch/item-durability-baseline.log`).
It established both missing effect handlers and the independent equipped weapon
break/repair defects in the shared inventory primitive. The two World durability
cast cases likewise failed because the item's durability remained unchanged
(`/workspace/scratch/item-death-world-baseline.log`, alongside other lanes).
Earlier baseline fixture mistakes about the fist damage count and held target
failure code were corrected before this baseline and are not product findings.

An independent read-only review found no blocking issue in the final handler
dispatch or durability contribution ordering. Final integrated verification is
recorded in the [combined milestone handoff](server-item-death-20261004.md).

## Remaining limits

The flat damage source handler adds durability data to `SMSG_SPELLLOGEXECUTE`.
ArcaneCore currently has no shared spell execute-log builder, and this slice
does not add one. Durability mutation and ordinary cast packets are covered;
that audit packet is still pending.

Actual build 5875 durability spell definitions and a real client fixture are not
available in this workspace. Tests use explicit synthetic definitions and do
not claim acceptance for imported spell ranks or real client animation and
execute-log behavior.
