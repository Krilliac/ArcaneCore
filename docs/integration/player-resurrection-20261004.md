# Player resurrection spells and responses — 2026-10-04

Ordinary player resurrection now follows the spell offer and client acceptance flow. Effect 18 (`RESURRECT`) captures a percentage of the target's maximum health and mana; effect 113 (`RESURRECT_NEW`) captures flat health from the calculated effect value and mana from `EffectMiscValue`. Applying the spell sends one resurrection request and leaves the target dead. A second cast cannot replace an outstanding request.

`CMSG_RESURRECT_RESPONSE` accepts only the request's caster GUID. Declining clears the request regardless of the response GUID, as the source handler does. Player-origin requests capture the caster's map, instance, coordinates and orientation when the spell lands. Acceptance starts the existing teleport handshake and restores life only after the corresponding acknowledgement and successful arrival at that captured destination. Creature-origin offers restore life at the target's current position.

Restoration removes ghost/root state, clamps captured health and mana to the current maxima, empties rage, fills energy, removes the target's corpse through its owning map, and queues a normal character snapshot. It does not apply resurrection sickness. The character save already contains health, powers, ghost status and corpse state; no database migration is needed. Requests themselves are transient and are cleared on disconnect, another revival, or a new `JUST_DIED` transition. A request offered between `JUST_DIED` and the next `CORPSE` update remains valid.

Released ghosts can be targeted through their local corpse even when the spirit has moved to another map. The spell system resolves that body to its online owner, checks that it is still the owner's current corpse, rejects conflicting explicit unit/corpse owners, and measures line of sight against the body. Normal cast bars recheck this mapping; logout/body removal prevents an offer from landing. Source `CheckRange` does not impose a separate corpse range or measure against the ghost's position, so the implementation preserves that behavior. The source-supported `LocationCasterDest` selector routes resurrection to the explicit dead player or corpse owner.

Quest settlement gates prevent offer/acceptance/restoration from mutating a held character. Duplicate responses and acknowledgements cannot replay the resurrection. A cancelled, redirected or superseded teleport cannot restore life at another destination; completion also checks the original online player object and original request identity.

## Source and packet evidence

Behavior was reimplemented from the pinned vmangos commit `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`:

- [SpellEffects.cpp](https://github.com/vmangos/core/blob/0e3ff01e76d4758e8a7c3108b2717cc785ed56fa/src/game/Spells/SpellEffects.cpp): `EffectResurrectNew` at 209–263; `EffectResurrect` at 5228–5248.
- [Spell.cpp](https://github.com/vmangos/core/blob/0e3ff01e76d4758e8a7c3108b2717cc785ed56fa/src/game/Spells/Spell.cpp): resurrection corpse selection at 3110–3117; request construction at 4934–4946; corpse LOS at 5780–5788; range checks at 6872–6953.
- [Player.cpp](https://github.com/vmangos/core/blob/0e3ff01e76d4758e8a7c3108b2717cc785ed56fa/src/game/Objects/Player.cpp): new-death invalidation at 1508–1522; delayed restoration at 2157–2174; `ResurrectUsingRequestData` at 20065–20120.
- [MiscHandler.cpp](https://github.com/vmangos/core/blob/0e3ff01e76d4758e8a7c3108b2717cc785ed56fa/src/game/Handlers/MiscHandler.cpp): response guards and decline semantics at 605–622.
- [Server/Packets/Spell.cpp](https://github.com/vmangos/core/blob/0e3ff01e76d4758e8a7c3108b2717cc785ed56fa/src/game/Server/Packets/Spell.cpp): request wire at 632–650.

Build 5875 `SMSG_RESURRECT_REQUEST` (`0x015B`) is an unpacked `u64` caster GUID, `u32` UTF-8 name byte length including its terminator, a CString, a sickness byte, and a delay byte. A player caster has an empty name, yielding a 15-byte payload. Delay is false for `AttributesEx3.NO_RES_TIMER` (`0x10`, Rebirth). Creature names use the existing template name; localized template names are not available through this seam.

The permissive [wow_messages response definition](https://github.com/gtker/wow_messages/blob/main/wow_message_parser/wowm/world/resurrect/cmsg_resurrect_response.wowm) agrees on `CMSG_RESURRECT_RESPONSE` (`0x015C`): unpacked `u64` GUID and one acceptance byte. Its [request definition](https://github.com/gtker/wow_messages/blob/main/wow_message_parser/wowm/world/resurrect/smsg_resurrect_request.wowm) lists only one trailing boolean and labels it `player`. The two trailing bytes follow the pinned vmangos packet implementation, independently corroborated by [cmangos Player.cpp](https://github.com/cmangos/mangos-classic/blob/8ec338a1704e7dcb1c0213eb7ed58f9231ade40f/src/game/Entities/Player.cpp) at 18892–18903. This discrepancy remains visible rather than silently inventing a packet shape.

The cast target block also has distinct incoming and outgoing shapes. A client cast with both `UNIT` and `CORPSE_ALLY` carries separate packed unit and corpse GUIDs. The existing source-shaped `SpellCastTargets.Write` builds the server's `SMSG_SPELL_START`/`SMSG_SPELL_GO` target block, which writes one selected GUID for those combined flags. The production loopback regression therefore writes the additional incoming corpse GUID explicitly; neither production wire helper was changed.

## Validation and boundaries

Before implementation, both new Game test cases failed because effects 18 and 113 sent no resurrection offer. Tests now cover both restoration formulas, captured destinations and clamping, alive/dead attribute guards, one outstanding offer, forged/replayed responses, ghost/corpse cleanup, `JUST_DIED` timing, a normal cast bar to a cross-map ghost, corpse disappearance, `LocationCasterDest`, creature names and UTF-8 packet bytes. World tests drive real cast/response/near-ACK/far-ACK packets through the production host, including save and relog behavior.

Release validation: all 17 focused Game resurrection cases and all 7 focused World resurrection cases passed. The shared solution built with zero warnings/errors. Combined full-suite and mock-client results are recorded in the parent continuation note after its final verification. Real build-5875 client validation and live database-provider validation remain pending.

`SELF_RESURRECT` (94), the pet branch of `RESURRECT_NEW`, soulstone/reincarnation release-dialog buttons, and dungeon-entrance recovery for a changed instance binding are outside this slice. The existing teleport service cannot address a specific different instance on the same map; such offers remain unaccepted. A far arrival in a different instance than the captured offer also leaves the player dead. The existing corpse model removes the body rather than retaining cosmetic bones, matching the other current resurrection paths.
