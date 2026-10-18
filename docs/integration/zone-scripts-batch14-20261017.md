# Zone scripts, batch 14 (2026-10-17)

Ported from mangos-classic ScriptDev2. None of these scripts existed before this batch.

| Script | Source | Where |
| --- | --- | --- |
| `npc_kitten` (9937), spell script `spell_corrupted_saber_visual` (16510), `npc_corrupt_saber` gossip (10042), quest 4506 | kalimdor/felwood.cpp:40-158 | `Creatures/Scripts/FelwoodKittenAI.cs`, registered as a map entry script by `KalimdorWorldMapData.OnCreatureCreate` |
| `npc_overlord_runthalak` (14392), `npc_high_overlord_saurfang` (14720) | kalimdor/orgrimmar.cpp:31-63 | `Creatures/Scripts/RallyingCryGossip.cs`, `QuestNpcFeature` |
| `npc_major_mattingly` (14394), `npc_field_marshal_afrasiabi` (14721) | eastern_kingdoms/stormwind_city.cpp:1320-1352 | same |

## Engine seams

- **Database gossip menus.** `ScriptedGossipMenu.DatabaseMenuId` and `ScriptedGossipMenu.Database(menuId)` let a gossip script pick a
  different database gossip menu. This is a pGossipHello calling `PrepareGossipMenu(creature, id)` and `SendPreparedGossip`.
  `QuestNpcServices.GossipHello` builds and sends that menu exactly as it would the creature's own, with its conditions and options.
- **Showing another entry.** `CreatureMapSystem.ShowAsEntry(creature, entry)` is cmangos `SetEntry` plus a display chosen from the
  other template and its scale, without `UpdateEntry`. The creature keeps its template, faction, flags, stats and AI. A respawn shows
  its own entry again.

## Behaviour

- **Kitten.**
  - On the first update that finds a player owner, it follows the owner, paused. It also does the jump emote and takes
    UNIT_FLAG_SPAWNING.
  - 7.5 s later it casts Corrupted Saber Visual. When the cast succeeds, it shows as the Corrupted Saber (10042), drops the spawning
    flag, says the follow emote to its owner and follows.
  - While following, out of combat, and within interaction distance of Winna Hazzard (9996), it gains the gossip flag.
- **Corrupted Saber gossip.** The gossip belongs to the AI, so it works whichever entry the creature shows.
  - As a saber it offers "I want to release the corrupted saber to Winna." to a player on quest 4506 while Winna is within 5 yd.
  - Choosing the line closes the menu, completes the quest (AreaExploredOrEventHappens) and ends the follow, so the saber despawns.
- **Heralds.** When the head the source pairs with a herald is up within 100 yd, the herald shows the head's database menu:
  - Runthalak: Nefarian's head (Orgrimmar), menu 6024.
  - Saurfang: Onyxia's head (Orgrimmar), menu 6024.
  - Mattingly: Onyxia's head (Stormwind), menu 5754.
  - Afrasiabi: Nefarian's head (Stormwind), menu 6027.

  Otherwise the herald's own menu is shown.

## Deviations

- The source starts the kitten in its constructor. Here it starts on the first update that finds an owner, because the owner link
  may not be set when the AI is built.
- The spell script's `SetEntry` runs in the AI after a successful cast, not inside the spell's effect.

## Tests

`ZoneScriptBatch14Tests` (Game) covers the kitten's paused start, the no-owner case, the transform timing and fields, the Winna gossip
flag, the saber's gossip and quest credit, the four heralds' menus with the 100 yd cutoff, and a head that doesn't match the herald.
