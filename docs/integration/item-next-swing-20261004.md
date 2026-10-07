# Item next-swing integration (2026-10-04)

`QueueNextSwing` now retains the full item cast context: triggering spell,
cast-item instance, item spell index, item cooldown/category overrides, and
effective category. The queued cast emits the cast-item GUID in
`SMSG_SPELL_START`, exempts item casts from spell power cost, and hands the
original bag/slot identity to the existing swing-time cast pipeline.

`Prepare` passes that context directly into the queue. Swing-time execution
continues through `CastQueuedMeleeSpell`/`Cast`, so moved/deleted/traded items,
settlement/transit/logout, reagent staging, cooldown ownership, and charge
settlement are rechecked at the actual swing. Cancellation and replacement keep
the existing no-cost melee-slot behavior.

Ground truth: vmangos `Spell.cpp:7607-7616`, `Player.cpp:7362-7403`,
`Spell.cpp:4991-5049`, and `Player.cpp:22187-22215`. Root-owned plumbing and
the assigned Game/World tests exercise moved/deleted/traded item invalidation,
deferred charges, cancellation, actual combat swing packets, and relogged item
identity/charges/cooldown owner fields. Qualification is recorded in the saved
wave evidence rather than inferred from implementation.
