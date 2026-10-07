# Random hit durability wear

`MapCombat.ApplyHitDurability` is the owned helper for vmangos `Unit.cpp:879-895`. The root
combat producer calls it only after positive, non-lethal damage is applied and before the existing
`DamageDealt` event. Victim-player and attacker-player checks are independent, so PvP and self
damage can produce two one-point losses. Periodic damage follows the source placement; misses,
zero delivery, and lethal damage do not call this helper. Lethal 10% death wear remains the
separate `Kill` path.

The helper reads each player inventory's configured `DurabilityLossChanceDamage` (0.5 percent by
default), uses the map's existing `ICombatRandom`, selects a uniform slot in
`0..InventorySlots.EquipmentEnd-1`, and delegates to
`PlayerInventory.DurabilityPointLossForEquipSlot`. This preserves break-stat transitions,
update fields, and ordinary item snapshot persistence.
The producer wiring is in the positive non-lethal `MapCombat.DealDamage` path before
`DamageDealt`; the hit-wear helper is intentionally independent of the lethal `durabilityLoss`
argument.

Ground truth: vmangos `src/game/Objects/Unit.cpp:879-895`; death wear
`Unit.cpp:1189-1202`; durability mutation `Player.cpp:4794-4898`. Game tests cover deterministic
100%/0% options, independent PvP rolls, uniform slot selection, positive non-lethal `DealDamage`,
lethal-path separation, and the existing inventory path. World tests exercise the live world player
and combat producer. This slice adds no schema or new random source.
