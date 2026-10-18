# Dungeon scripts, batch 19 (2026-10-17): the Dire Maul Dreadsteed ritual

This batch adds the warlock epic mount ritual in Dire Maul West, quest 7631 "The Dreadsteed of Xoroth". Before it, event 8420 (J'eevee's
summon) and event 8428 (the Dreadsteed's summon) were always refused, because the Dire Maul port had no TYPE_DREADSTEED state. That made
the quest impossible to complete. classic-db z2815 already has the DB side, matching mangos-classic:
- J'eevee's path 1, whose waypoint scripts 1450004-1450008 show and activate the bell, the wheel and the candle;
- the nine demon waves (relays 1450101-1450109);
- event 8428, which summons the Xorothian Dreadsteed.

## Port

The instance side is `DireMaulDreadsteed.cs`, a partial of `DireMaulInstance`. It ports these parts of mangos-classic
`instance_dire_maul.cpp`: Update, the TYPE_DREADSTEED branch of SetData, OnObjectCreate (portals, symbols, the bell, wheel and candle, the
circle, the candle aura trap), OnCreatureDeath (Lord Hel'nurath sets DONE) and ProcessDreadsteedRitualStart.
- **`event_spells_warlock_dreadsteed`:** `DireMaulInstance.OnSpellEvent`.
  - **8420** from a player while the state is NOT_STARTED or FAIL: the player summons the ritual controller (14501) and J'eevee (14500)
    on her path 1, and the state becomes IN_PROGRESS. The event's DB script then runs.
  - **8428** from a player while the state is SPECIAL: the state becomes DONE and the DB script summons the Dreadsteed.
  - Any other case refuses the event. `ScriptedEvents.GatedOff` now leaves both events to the instance in Dire Maul and keeps them shut
    elsewhere.
- **Failure:** while the ritual is IN_PROGRESS it fails in either case below:
  - J'eevee is still there and only GMs are left in the instance;
  - J'eevee is gone and the bell, the wheel and the candle are all deactivated.

  FAIL despawns the controller, the portals, the symbols, the three objects and the circle. A failed ritual can be started again.
- **`go_dreadsteed_quest_objects` (179672-179674):** each object casts its aura on its last user every 6 s:
  - the bell casts 23117;
  - the wheel casts 23120;
  - the candle's 23226 is cast through the invisible candle trap 179688.

  When J'eevee uses an object, the player who started the ritual gets the aura. An object that spawns outside an IN_PROGRESS ritual goes
  away at once.
- **`npc_warlock_mount_ritual` (14501):** this is `DreadsteedRitualAI` in `DireMaul/DreadsteedRitualAI.cs`, registered in
  `CreatureAiServices`.
  1. 48 s after the ritual starts, it shows the circle for 900 s and the side portals for 380 s.
  2. It runs nine waves. The first eight are 45 s apart; the last runs 17.5 s. Each wave also shows one ritual symbol for 900 s.
  3. At the end, every demon it summoned teleports out (29216), says -1429005 and goes a second later. The circle activates, the state
     becomes SPECIAL and the controller despawns.
  4. From 70 s on, every 30-45 s, it deactivates one of the three objects that is still active.

  The summoned demons first walk halfway to the pedestal at z -32, then to the pedestal, then wander within 15 yd of it. The pedestal is
  their home.
- **`CreatureMapSystem.RespawnGameObjectFor(go, seconds)`:** the body of SCRIPT_COMMAND_RESPAWN_GAMEOBJECT, now callable as ScriptDev2's
  `DoRespawnGameObject`. The DB command uses it unchanged.

## Deviations

- **Demon movement:** ArcaneCore has no summoned-movement-inform or summoned-reached-home hook. Instead, each update the controller checks
  each demon's movement and gives it the next leg once the current one ends and the demon is out of combat.
- **Object auras:** the reference keeps pulsing an object's aura after the controller deactivates the object. Here the pulse stops while
  the object is not activated, so a deactivated object stops helping until a player uses it again.
- **Circle activation:** the circle is a generic object, so its "use" at the end sets its state to active.
- **Not ported:** `spell_ritual_candle_aura` (the candle aura skips player-controlled targets) and `spell_summon_netherwalker` (22876;
  classic-db has no EventAI that casts it).

## Tests

- `DireMaulDreadsteedTests` (3 tests): the event gates, the failure and its cleanup, and the controller's waves, demon guidance and end.
- Game filter `DireMaul|ScriptedEvent|RelayScript|DbScript|ScriptDev2Dungeon|GameObjectScript|RespawnGameObject|CreatureAiServices|ScriptCatalog`:
  196 passed.
- World filter `Docs|ScriptCatalog|ScriptedEvent|QuestScript|DireMaul`: 121 passed.

## Still open

- `event_wildhammer_message` (hinterlands.cpp): needs an Eastern Kingdoms world map script.
