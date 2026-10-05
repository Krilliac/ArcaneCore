# Combat death durability — 2026-10-04

This continuation connects ordinary PvE player death to the existing inventory
durability primitives. The reference is vmangos commit
`0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`, `Unit.cpp:977-981,1191-1202`.
The C# implementation is independent; no source or content assets are vendored.
No schema or configuration switch is added.

## Behavior

A lethal combat damage call applies 10% of maximum durability to equipped items
only, once. The existing inventory implementation handles rounding, a minimum
one-point loss, saturation at zero, and removal of equipped bonuses when an item
breaks. Backpack and bag contents are unaffected. Damage that leaves the player
alive does not apply this death penalty.

Eligibility follows the caller's durability-loss flag, the source spell's
`AttributesEx3` bit `0x20` (`NO_DURABILITY_LOSS`), and the killer's resolved
charmer or owner player. A player killer, a self kill, and an actual creature
whose owner or charmer resolves to a player in its map skip ordinary death wear.
The existing `OwnerLinks.GetCharmerOrOwnerPlayerOrSelf` reads real update-field
links; no test-only controlled-unit interface is substituted. Charmer links
take precedence over owner links, and an unresolved player GUID does not
fabricate a player tap. This eligibility lookup does not change party kill
credit, rewards, or corpse PvP attribution.

Battleground map templates skip ordinary combat death wear. This is the existing
map classification, not a claim that battleground membership or match services
are implemented. The duel damage clamp runs before death: an opponent's clamped
hit leaves one health and no death penalty, whereas a lethal third-party PvE
hit follows ordinary death handling.

The dying player's session receives one `SMSG_DURABILITY_DAMAGE_DEATH` even when
the existing `DurabilityLossEnable` option prevents item mutation. This ordering
matches upstream: the inventory primitive checks the configuration; the caller
still sends the notification. The packet is private to the victim and has an
empty body. Its opcode is the existing `0x02BD`, independently checked against
[wow_messages at 70abb9de](https://github.com/gtker/wow_messages/blob/70abb9deff0bb63440d8aeb4386b820653e8a176/wow_message_parser/wowm/world/combat/smsg_durability_damage_death.wowm)
and vmangos `Packets/Misc.cpp:809-816`.

## Damage callers and persistence

Combat exposes an optional durability-loss flag and source spell. World spell
damage forwards both into map combat. The instakill effect passes false for its
own damage call, as in `SpellEffects.cpp:285`; mixed-effect spells retain the
ordinary eligibility of their other effects. Split damage likewise retains its
upstream caller-specific suppression.

Environmental damage already applies its own 10% loss and death notification
after self damage (`Player.cpp:713-775`). Its combat call suppresses ordinary
wear so falling and environmental hazards are charged once. The existing
creature-to-player approximation of the environmental spell effect is outside
this continuation's fidelity claim. Random wear from dealing or taking hits,
durability insurance, and battleground services are also outside this change.

Quest settlement holds block damage and death before inventory mutation, and
callers may retry after release. Already-dead targets cannot replay the penalty.
The normal inventory snapshot carries the changed item instance durability;
disconnect saving and relogging require no new storage field or sidecar.

## Verification

The first executable baseline ran all 11 initial Game cases before death wear
was connected: five failed on the missing penalty or death notification, and
six existing guards passed. The five World cases produced three matching
failures; spell-exemption and instakill guards already passed before ordinary
wear existed. Compiler-only fixture corrections are not counted as product
regressions. The source then gained explicit caller/spell suppression and
battleground-template checks, covered by three additional Game cases.

The parent integration run owns build and test scheduling. New Game cases cover
equipment versus backpack loss, victim-only empty packets, repeat death,
configuration suppression, nonlethal and player/self damage, actual creature
owner and charmer links, unresolved owners, settlement retry, environmental
single charging, and duel clamping. New World cases exercise real spawned
creature damage through production map combat, socket delivery, saved inventory
and relog, the production spell damage sink's spell exemption, and an actual
creature instakill cast. Final combined results are recorded in the
[milestone handoff](server-item-death-20261004.md).
