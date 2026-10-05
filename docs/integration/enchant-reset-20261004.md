# Owned item enchanting and combat reset boundaries

Permanent and temporary enchant spells resolve a live item from the caster's
own inventory. Checks validate item requirements and the permanent spell's
minimum target item level before reagent staging. Temporary spells replace the
previous temporary enchant and convert the effect's seconds to bounded unsigned
milliseconds. World schema 24 supplies immutable spell_enchant_charges entries;
startup ignores rows referring to unknown spells. Trade targets remain refused
until a real trade-slot owner resolver exists.

Equipped enchants apply damage, equip spells, resistance, stats and Shaman totem
effects through the owner. Each effect is filtered independently; removal uses
the applied definition and original hand. Fractional weapon-delay damage survives
late stat-maintainer attachment. Item-owned aura cleanup preserves a later foreign
same-ID replacement under the existing positive-aura replacement policy. Stat
effects compose with the percentage ledger. Health and mana removals clamp live
values to the new maxima.

Replacement, equipment transitions, break/repair hooks, charge-one depletion and
online duration expiry remove effects before clearing fields. Inspected slots
0 and 1 update the existing visible-item fields. Socket tests cover actual
permanent/temporary casts, saved relog state and expiry; Game tests cover reagent
consumption, refusal and effect ownership. Original client/DBC acceptance, trade
enchanting, DBC non-owned binding metadata and runtime catalog reload remain pending.

Accepted far teleports and explicit CMSG_ATTACKSTOP clear pending extra attacks.
Refused transitions preserve them. Same-map teleport behavior remains unchanged
because the pinned source proves a separate reset only for far transfer.

The first full gate timed out in the existing eight-held-settlement mock test.
Its unchanged isolated run passes, and one typed complete-suite retry is retained.
The timeout cause remains unproven. Focused-r3 results used stale binaries after
a failed build and are diagnostic only; focused-r5 is the valid focused gate.
