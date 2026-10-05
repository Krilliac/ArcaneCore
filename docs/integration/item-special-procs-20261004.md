# Special-attack item procs (2026-10-04)

The special-attack slice follows vmangos `Spell.cpp:1529-1538`: weapon item
procs are attempted from the target-effect path before the ordinary spell miss
return; reflected casts are excluded because vmangos redirects their target to
the caster before this point. This implementation is limited to represented
`EquippedItemClass == 2` and `RangeIndexCombat` data, preserving weapon identity,
GCD suppression, PPM/chance, item cooldown/category metadata, and the existing
triggered item-proc cost exemption.

The source-grounded references are `SpellEntry.cpp:1123-1132`,
`Player.cpp:7256-7306`, and `Unit.cpp:1755-1776`. Enchantments, custom weapon
proc attributes, ranged producers, and extra-attack recursion remain outside
the available build-5875 item model.
