# Zone scripts batch 22 (2026-10-17): Garments quests, paladin Redemption targets

A scan of creature_template ScriptName entries against the entries the code knows turned up these two npcs_special.cpp scripts. Nothing implemented them.

## npc_garments_of_quests (cmangos world/npcs_special.cpp:535-745): `GarmentsOfQuestsAI`
- Covers the priest quests 5621 (Shaya 12429), 5624 (Roberts 12423), 5625 (Dolf 12427), 5648 (Kor'ja 12430) and 5650 (Deathguard Kel 12428).
- The guard kneels, PvP-flagged, at 70% health.
- On the quest:
  - Lesser Heal rank 2 (2052) makes him stand and say -1000231.
  - Power Word: Fortitude rank 1 (1243) then gives his thanks line and the speak-to credit (TalkedToCreature).
  - Five seconds later he says goodbye and runs his creature_movement_template escort path.
- Before this, the five quests could not be completed.

## npc_redemption_target: withdrawn
The spells lane's FaulkAI (6111c757f) already covers Henze and Narm Faulk, and it registered after this batch's RedemptionTargetAI so it won anyway. RedemptionTargetAI was removed in the follow-up commit.

## mob_restless_soul (vmangos stratholme.cpp): `RestlessSoulAI`
- Quest 5282 The Restless Souls.
- Egan's Blaster (17368) from a player on the quest tags the soul.
- Five seconds later the player gets the kill credit and the soul dies.
- It leaves a Freed Soul (11136) for 5 min, which casts 17370, says bt 6451-6455 and follows the player.

## Tests
- Game.Tests `ZoneScriptBatch2|CreatureAiServices|EntryScript`: 16 pass.
