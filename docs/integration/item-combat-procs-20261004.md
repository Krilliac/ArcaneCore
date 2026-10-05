# Item chance-on-hit combat spells (2026-10-04)

Trigger-2 weapon item spells are now produced from the post-damage weapon-hit
event. The producer selects the equipped weapon for the attack type, filters
`ItemSpell.Trigger == CHANCE_ON_HIT`, suppresses GCD-blocked or stale/broken
items, computes PPM/chance from the available item delay, `PpmRate`, and
`SpellInfo.ProcChance`, and casts triggered with item provenance. A recursion
guard bounds reentrant weapon-hit dispatch. Extra-attack effects are not yet
implemented, so their Vanilla recursion rule remains pending. Triggered item procs do not consume
item charges or power.

Ground truth: vmangos `Unit.cpp:1771-1777`, `Player.cpp:7256-7306`, and
`Unit.cpp:5786-5791`. Enchantment combat procs, item sets, and random properties
remain outside this slice because their build-5875 DBC/runtime data is not
represented by the current item model.

Game tests cover a successful non-self weapon hit with exact cast-item GUID and
unchanged charges, miss/dodge/parry rejection, dead-target rejection, and GCD
suppression without changing item charges or power. The World test drives a
real hostile CMSG_ATTACKSWING and verifies the outgoing proc packet's exact
cast-item GUID. Explicit PPM above 100 remains intact. Original-client acceptance
and broader proc producers remain pending.
