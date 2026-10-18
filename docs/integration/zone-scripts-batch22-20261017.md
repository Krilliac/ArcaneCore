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

## npc_redemption_target (npcs_special.cpp:847-994 plus the PaladinQuestReviveSelf spell script): `RedemptionTargetAI`
- Henze Faulk 6172 and Narm Faulk 6177 lie dead-looking (dynflag dead, stand state dead) and offer no quests.
- Symbol of Life (8593) revives him:
  - Quest Self Healing (25155) and he stands.
  - At 3 s he faces the paladin.
  - At 4 s his quest giver flag returns (needed for 1783/1786) and he says bt 2283.
  - At 7 s he kneels.
  - After 2 min he evades and lies back down.

## Tests
- Game.Tests `ZoneScriptBatch2|CreatureAiServices|EntryScript`: 16 pass.
