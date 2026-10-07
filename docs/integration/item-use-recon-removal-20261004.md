# Item-use reconnaissance and live reagent removal — 2026-10-04

Source inspection found a larger producer gap than the earlier handoff's
"cast-item context" label suggested: `CMSG_USE_ITEM` exists in the generated
opcode enum but is not registered by the World item handlers. No item-use
handler or cast-item context is delivered by this tranche.

Grounded next implementation requirements:

- wow_messages at `70abb9deff0bb63440d8aeb4386b820653e8a176`,
  `world/item/cmsg_use_item.wowm`: vanilla opcode `0x00AB`, three u8 values
  (bag, slot, spell index), followed by the spell-target block. TBC's cast
  counter and item GUID are not vanilla payload fields.
- vmangos `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`,
  `SpellHandler.cpp:36-139`: current mover, exact owned item/slot, on-use
  spell index, equipped-state restrictions, live usability, trade/combat,
  binding, target and shapeshift checks.
- `Player.cpp:7362-7400`: iterate all valid on-use item spell slots, first
  cast normal and subsequent casts triggered, preserving actual cast item.
- `Spell.cpp:4991-5075`: charge signs, per-slot decrement, stack behavior,
  expendable item removal after spell-go, and item casts' power policy.
- `Spell.cpp:5082-5131,7249-7279`: shared cast-item/reagent charge adjustments
  and consumed target/cast-item pointer clearing. These rules must travel with
  a real cast context rather than be patched into Ankh handling.

## Implemented fix

The existing reagent operation stages changes in a detached inventory, which
has no live combat/disarm/logout state. Two tests reproduced a real gap:
the caster's protected equipped armor was consumed as a reagent despite
`CanUnequipItem` refusing the live removal, for both a full removal and a
partial stack decrement. This returned success and ran the spell effect.

After detached staging, the runtime now validates every consumed live item
against the inventory's existing `CanUnequipItem` rule, including decremented
stacks. Rejection discards the stage before cooldown, power, inventory or
effects change. Ordinary carried-item use remains governed by the existing
rules. The full-cost operation is still applied atomically before callbacks.
No schema, wire field, configuration switch or item-use scaffold is added.

Both baseline regressions failed for the intended behavior; the implemented
focused reagent/Reincarnation run passes 30 cases. The combined Release
build has zero warnings/errors. Final review found no blocker in this fix.
The full solution run passed 14,312 cases and skipped six, but failed the
existing `TickStatsTests.Record_DoesNotAllocate`: expected zero allocation,
observed 2,016 bytes. The cause is uninvestigated; this is not a green full-run
claim. The other five projects passed. No rerun or production change was used
to conceal the failure.

The run reached its 60-minute ceiling after verification. A separate portable
checkpoint saves this current source; the previous qualified 14,311-test patch
and evidence remain preserved. Additional goal work is awaiting a renewed
bounded run. The broader goal remains active and is not marked complete.
