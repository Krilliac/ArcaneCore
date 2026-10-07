# Remaining extra-attack reset boundaries

Committed removal of main-hand, off-hand or ranged equipment clears pending
extra attacks. Armor and backpack removal preserve them; generic stat removal
for break/repair remains separate. Successful player mounting and validated
taxi flight start also clear the pending batch. Refused transitions preserve it,
and creature mounting behavior is unchanged.

The changes follow pinned vmangos Player.cpp:10523, 18081 and 18227. Existing
packet order, client-control publication and schema versions remain unchanged.
Focused Game checks exercise the authoritative inventory, mount and flight
paths; the complete suite and native mock remain the qualification gates.
