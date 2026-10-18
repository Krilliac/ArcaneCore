# Dungeon scripts, batch 17 (2026-10-17): Sunken Temple, Malfurion Stormrage

This batch comes from a scan of the cmangos ScriptDev2 dungeon and raid directories against ArcaneCore. The scan matched by script
name, then by creature, object, trigger and event entry. Malfurion was the highest-value gap it found: the Sunken Temple area trigger
that summons him and his AI were both missing. Malfurion has no world spawn in classic-db, and he is the only giver of "Eranikus, Tyrant
of the Dream" (8733). That made 8733, and the AQ scepter chain after it, unobtainable.

The port is mangos-classic `sunken_templeScripts.cpp`:
- **`AreaTrigger_at_shade_of_eranikus` (:44-61)** becomes `SunkenTempleInstance.OnAreaTrigger` (`Instances/Scripts/SunkenTemple/SunkenTempleInstance.Malfurion.cs`).
  It runs for trigger 4016 when the player meets all three conditions:
  - has turned in 8555;
  - has not turned in 8733;
  - does not have 8733 complete.

  If Malfurion is not already `IN_PROGRESS`, the player summons him at aSunkenTempleLocation[2], (-660.53, -16.71, -90.84). He stays
  until he dies, and the instance marks `TypeMalfurion` `IN_PROGRESS`.
- **`npc_malfurion_stormrage` (:64-149)** becomes `MalfurionStormrageAI`, registered for entry 15362 in `CreatureAiServices`.
  - On spawn he casts Spirit Spawn-in 17321 and has no quest giver flag. In a dungeon he also says emote -1109000.
  - He then bows, says -1109001..-1109004, 2/12/12/11/4 s apart, using the talk emote state. Then he takes the quest giver flag and
    goes back to emote state 30.

Deviations:
- **No `Map::IsDungeon()`:** ArcaneCore has none, so "in a dungeon" means the map runs an instance script.
- **Quest checks:** the quest status checks use the map's quest seam (`IInstanceQuestRewards`) and the instance's completed-unrewarded
  predicate. A map without the seam summons nothing.

## Scan notes (remaining candidates)

The scan found these still unported:
- **Dire Maul:** `npc_warlock_mount_ritual` + `go_dreadsteed_quest_objects` (the warlock epic mount ritual) and `go_fixed_trap`.
- **Zul'Gurub:** `npc_zulian_prowler`.
- **Temple of Ahn'Qiraj:** `event_cthun_reset` (9738).
- **Stratholme:** `mob_restless_soul` (11122). It has no spawn in classic-db, and the Spectral/Ghostly Citizen AI already handles
  Egan's Blaster, so it is low value.

The other names the scan flagged by name (the Naxxramas horsemen, Stalagg/Feugen, the C'Thun and Ouro pieces, the Twin Emperors,
Gnomeregan buttons, the BRD bar and others) turned out to be covered under other class names.
