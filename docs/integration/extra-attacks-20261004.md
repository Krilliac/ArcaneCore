# Extra attack state

The extra-attack slice follows vmangos `Unit::m_extraAttacks` and `EffectAddExtraAttacks`.
`UnitCombat.QueueExtraAttacks(int)` accepts one bounded batch (maximum 100), rejects nested
batches while a queue is pending or locked, and `MapCombat.UpdateMeleeAttackingState` drains the
ready queue on the next combat unit update through the existing base weapon `AttackerStateUpdate` path while locked. The lock prevents
an extra-attack spell from recursively queueing more attacks during its own generated swing.

The item combat producer applies the vmangos recursion guard only to trigger-2 item spells whose
spell contains `AddExtraAttacks` while extra attacks are pending. Enchantment combat spells keep
their separate source behavior and are not suppressed by this guard.

`ExtraAttackSpellEffects` implements `ISpellHandlerModule` and is discovered automatically at
spell-system initialization. Death and decided duel completion clear pending counts; interrupted
duels preserve them. The scheduler resets the base timer once after the generated batch.
