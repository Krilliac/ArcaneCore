# Native models, extra attacks and stat wire fields

Native player display scale and geometry are initialized before map insertion
and the first socket create packet. Initialization runs once per player object,
preserving live scale auras across subsequent map transfers. Creature creation,
temporary spawns and respawns use loaded display/model metadata. Scale overrides
retain the originally chosen display slot across gender substitution.
World schema 23 persists vmangos display scales 2–4; scale 1 uses the existing
Scale field. SQLite upgrade/import/EF reload tests retain all four values.

The discovered AddExtraAttacks effect queues a bounded batch of up to 100
attacks. The count is visible immediately to proc recursion checks; execution
is deferred to the next combat unit update. Generated base swings use the
existing melee path while the queue is locked, consume each attack after the
callback, and reset the normal base timer once. The full batch is consumed even
after a lethal hit, matching the pinned source's no-op/decrement loop. Death and
decided duel completion clear pending counts without unlocking a live drain.
Interrupted duels preserve the pending counts. Trigger-2
extra-attack item procs are suppressed while a batch is pending; enchant procs
retain their separate source behavior. The cap is an explicit local bound.

Resistance tooltip fields now use floats, preserving fractional existing
positive and negative contributions. Raw resistance fields remain integers.
Intellect modifies the latent mana pool whenever create/base mana exists,
including Cat form. Attached stat refresh remains idempotent on apply/removal.

Socket create packets, actual map updates, item proc producers, duel completion,
SQLite upgrade/reload and online enchant expiry/logout/relogin are covered.
Original client/DBC acceptance and external database providers remain pending.
The broader enchant stat/equip-spell engine and enchant spell producers remain
separate work. Read-only review findings and coordinator gate logs are retained.

The first full gate timed out after releasing held settlements in the existing
eight-player quest responsiveness test. Its unchanged isolated run passes;
the timeout cause remains unproven. Both results are retained, and a single
typed retry runs the complete six-project suite again.
