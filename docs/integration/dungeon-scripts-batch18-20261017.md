# Dungeon scripts, batch 18 (2026-10-17): Zul'Gurub prowlers and Slip'kik's trap

Two of the small gaps from the batch 17 scan.

## Zul'Gurub: `npc_zulian_prowler` (15101)

Port of mangos-classic `boss_arlokk.cpp` npc_zulian_prowlerAI, as `ZulianProwlerAI` in `Instances/Scripts/ZulGurub/ZulGurubTrashAis.cs`,
selected by `RaidBossAI.Create` in Zul'Gurub.
- Every prowler casts Sneak (22766) when it spawns.
- Arlokk's two triggers summon prowlers with Summon Zulian Prowlers (24247) while she fights. A summoned prowler walks to the wall
  shield in front of the altar, (-11494.76, -1627.56, 41.30) from `zulgurub.h` aArlokkWallShieldPos. Before this port the summoned
  prowlers stood where they appeared.
- Once more than 40 summoned prowlers are alive, a new one despawns at once.

Deviation: the reference keeps its GUID list per AI, so its count never gets past 1 and the cap never fires. Here the count covers the
whole map, which is what the reference's comment says it should do.

## Dire Maul North: `go_fixed_trap` (179512) and quest 1193 "A Broken Trap"

The code is in `Instances/Scripts/DireMaul/DireMaulSlipkikTrap.cs`, a partial of `DireMaulInstance`.
- **The trap now appears.** classic-db z2815's CompleteScript for 1193 respawns gameobject guid 4290040. That guid is the Broken Trap
  (179485) itself; the Fixed Trap row is guid 4290049 (spawntimesecs -7200, same spot), so the trap never showed. Rewarding the quest
  at the Broken Trap now shows the Fixed Trap row, or summons one there for 12 h if the row is missing (vmangos
  `QuestRewarded_go_broken_trap`). Nothing happens while a trap is already up.
- **New hook:** `InstanceData.OnGameObjectQuestRewarded`, called from `CreatureQuestScripts` when the quest ender is a game object,
  before the DB CompleteScript (the ScriptDevAI `OnQuestRewarded(Player*, GameObject*, Quest*)` hook). A throwing hook is logged and
  recorded in the FaultLedger, and the reward carries on.
- **go_ai_fixed_trap::UpdateAI** (mangos-classic `instance_dire_maul.cpp:637-660`). The trap reacts only to Guard Slip'kik (14323) and
  never picks a player. When he comes within 2 yd (the vmangos reach):
  - he says "Doh!" (-1429004) and evades;
  - he becomes immune to players and takes faction 14 until he respawns;
  - the trap casts its Ice Lock (5249) on him and despawns.
- **GuardSlipkikTrigger (31770)**, triggered by King of the Gordok (22799), gives the stored Slip'kik the ogre faction (45) back.

## Tests

- `RaidBossScriptTests.ZulianProwler_SummonedOneSneaksToTheWallShield_AndTheSummonedAreCappedAtForty`
- `DireMaulSlipkikTrapTests` (2 tests)
- Filter `DireMaul|RaidBossScriptTests|CreatureQuestScript|ScriptDev2Dungeon|QuestScript`: 110 passed.

## Still open from the scan

- Dire Maul dreadsteed ritual (quest 7631): `npc_warlock_mount_ritual`, `go_dreadsteed_quest_objects`, TYPE_DREADSTEED.
- `event_wildhammer_message`: needs an Eastern Kingdoms world map script.
- Skipped on purpose: AQ40 `event_cthun_reset` (ArcaneCore's C'Thun follows vmangos, which never casts 26257) and Stratholme
  `mob_restless_soul` (no spawns).
