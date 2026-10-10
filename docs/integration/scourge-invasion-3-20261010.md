# Scourge invasion choreography, part 3 — 2026-10-10

**Stacked on #37** (`grok/scourge-invasion-2`, characters schema 46). Merge #37 first. This slice adds **no schema**.

## Added (mangos-classic `scripts/world/scourge_invasion.cpp`)

- **Starting an entry's own path (new Game capability).** `CreatureMapSystem.StartEntryWaypointPath(creature, pathId)` is cmangos
  `MoveWaypoint(pathId, PATH_FROM_ENTRY)` after `Clear(false, true)`. It walks path `pathId` of the creature's entry from
  `creature_movement_template`, path 0 included, which a temporary summon could not do before. If the path is missing it returns false and
  changes nothing. `ChangeMovement` is untouched.
- **Zone yells (new Game capability).** `CreatureMapSystem.ZoneYell(creature, textId, target)` is ScriptDev
  `DoBroadcastText(..., CHAT_TYPE_ZONE_YELL)`. It sends a monster yell to the players of the map who stand in the speaker's zone, uses the text's language, plays its
  sound, and logs a missing text once.
- **City attacker walking route.** After the summon, the Pallid Horror or Patchwork Terror walks the reference's entry path: Undercity spawn 0
  takes path 1 and spawn 1 takes path 0; Stormwind spawn 0 takes path 2 and spawn 1 takes path 3. A missing path is logged and the attacker holds its spawn point.
- **PallidHorrorAI.** On spawn it gets Aura of Fear (28313), a storm (0.25, permanent) and 5-9 Flameshockers (16383). The Flameshockers are 1-hour
  out-of-combat-or-corpse summons that follow at 2.5 yd in a ring; if one falls out of formation outside combat, it rejoins every 2.5 s. In combat:
  - a zone yell from the eight `BCT_PALLID_HORROR_YELL*` texts (first after 5 s, then every 65-300 s);
  - Damage vs Guards (28364) on the victim (every 11-81 s);
  - a Flameshocker next to a random attacker every 2 s while it has fewer than 30.
  On death, Bolvar (1748) or Sylvanas (10181), if within 100 yd, zone-yells the defence line (12318 or 12331), every Flameshocker dies, it casts the
  capital's crystal summon (28424 for Stormwind, 28699 for Undercity), the weather clears, and the **next attack is saved 45-60 minutes after the death**
  (`CityAttackDefeatedAsync`, the same `world_scourge_invasion_city` row).
- **Flameshocker MinionAI.** Spawn-in and immolate visuals (28234 and 28330), Touch (28314 or 28329) every 30-45 s in combat, Revenge (28323) on death.
- **MouthAI.** It's passive. At zone start it sets a storm and zone-yells start text 13121 or 13125, then a random 13122-13126 text every 150 s to 1 h. When the zone ends
  (defeat, 150 victories or stop), it zone-yells one of 13163-13165, clears the weather and force-despawns, in place of the plain despawn from part 2.

## Tests

- World `ScourgeInvasionChoreographyTests`, two new tests:
  - The attacker is on its entry path (`Waypoint`), its AI has 5-9 living, following Flameshockers, and killing it kills them, saves the
    capital's next attack 45-60 minutes out, and stops a re-summon before that time.
  - Mouths are `ScourgeMouthAi`, a player in the zone gets the start yells and one end yell, the defeated Mouth is gone, and
    `StartEntryWaypointPath` with no path returns false and leaves movement unchanged.
- Data: `CityAttackDefeatedAsync` saves death time + 45-60 min and refuses while the invasion is off.
- Locally: the full solution builds in Release with 0 warnings. World Scourge/condition/game-event/waypoint/EventAI filter: 66 passed, 7 skipped (they need the imported DB).
  Data Scourge tests pass.

## Not done (honest limits)

- **Cultist Engineers and summoner shields/traps** (16230, 28132 or 181142, buttress channel, rune gossip, Shadow of Doom) are not ported. I ran out of time.
- The reference picks a Flameshocker spawn target from any attackable, non-civilian unit in the zone within the BG visibility range. Here
  it picks from the attacker's threat list, with the same "no Flameshocker within 5 yd" rule.
- Not ported:
  - guards being pulled onto the Pallid by `MoveInLineOfSight`;
  - the 10 s corpse delay;
  - Flameshocker 60 s out-of-combat self-despawn (the 1 h summon timer still applies);
  - the Sylvanas answer yell event.
- Whether the city-attacker paths, broadcast texts and spells all exist depends on the imported ClassicDB/DBC. Only the synthetic fixture
  was exercised, so original-client acceptance is outstanding and no live profile was touched.

## Next slice

Cultist Engineers and shields (summon shield object, buttress channel, 8-rune gossip, Shadow of Doom), then guard assistance against
city attackers.
