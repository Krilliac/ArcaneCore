# ON_EQUIP form compatibility (2026-10-04)

The item-equip sink now has a default `OnPlayerFormChanged` callback. The
shapeshift service invokes it after committing the new form field; the world
item feature reconciles equipped, unbroken ON_EQUIP spells. Compatible spells
are applied only when no matching item-owned holder exists; incompatible item
holders are removed by item GUID. ON_USE charges/cooldowns and duplicate item
owners remain independent.

Ground truth: vmangos `Player.cpp:7181-7228` and
`Player.cpp:7231-7240`. Item sets and chance-on-hit procs remain separate
features. Full real-client acceptance and coordinator verification remain
pending.
