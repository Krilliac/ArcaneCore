# Durable trade costs and committed cast publication

The deferred enchant plan now uses existing CharacterLife snapshots for mana or
health cost after-images. Strict initial checks and triggered final acceptance
checks run through the spell system. The plan never changes live resources.
The store compares non-null Before.Life against all durable vitals/corpse fields
and commits After.Life with money and complete inventory in one transaction.

Reagent planning selects exact existing item GUIDs/counts from the detached
inventory after outgoing items are excluded and before incoming items are added.
Each selected live item passes its own unequip gate. Publication destroys those
same instances, including partial stacks. Fully consumed GUIDs are explicitly
declared in the participant and pass conservation validation as consumed rather
than transferred; duplicate, retained, foreign, absent or escrow declarations are
rejected. Legacy participants without declarations retain their old rules.

After authoritative commit, World applies planned health/power under the existing
publication guard and publishes the inventory. The triggered cast then emits
spell/category cooldown and SMSG_SPELL_GO exactly once for that plan, without a
new GCD or another power/reagent/effect execution. Mana payment starts the normal
regeneration interruption window unless the spell suppresses it.

The build-5875 request still carries TRADE_ITEM with packed raw slot6. Primary
vmangos SpellCastTargets::updateTradeSlotItem changes the final SpellGo item field
to the real partner item GUID while retaining TRADE_ITEM. Item targets are absent
from the packet's unit/gameobject hit list. No deferred SpellStart is emitted.

Tests cover immutable plans and live-state preservation, exact selection and
partial/full-stack payment, durable life conflicts and full-stack conservation,
SQLite success/failure with mana and enchant changes, and byte-exact final
SpellGo. No schema bump: World24 and Characters24.

Cast-item costs, randomized/mixed enchant effects, original client/DBC acceptance
and external database providers remain pending. Duplicate live callbacks for the
same plan are suppressed; crash recovery of cast packets/cooldowns is not backed
by a durable publication journal. Incoming items cannot pay this bounded planner's
costs, an explicit stricter boundary than general post-transfer inventory access.
