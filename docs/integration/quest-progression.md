# Quest progression, XP and leveling (`feat/quest-progression`)

Fleet round 2 worker branch (PR into `codex/integrate-feature-fleet-20261003`).
It broadens the ordinary quest path. It also adds player experience and leveling,
which didn't exist before this branch.

Provenance: behaviour follows vmangos `4b3d241cffe245a1f68da11380bce96c23db48c0`
(`Formulas.h` MaNGOS::XP, `Player::GiveXP`/`GiveLevel`, `Player::RewardQuest`,
`Quest::XPValue`, `Player::KilledMonsterCredit`, `CastedCreatureOrGO`,
`AreaExploredOrEventHappens`, `Group::RewardGroupAtKill`). Wire layouts follow gtker
wow_messages `70abb9de` (`SMSG_LOG_XPGAIN`, `SMSG_LEVELUP_INFO`,
`SMSG_QUESTGIVER_QUEST_COMPLETE`). All code is a clean-room C# re-expression.
No GPL text was copied.

## What this branch adds

### Experience and leveling (`src/ArcaneCore.Game/Progression`, `src/ArcaneCore.World/Progression`)
- `ExperienceFormulas`: gray level, zero difference, level factor, base gain, kill
  gain (elite ×2, `Rate.XP.Kill`/`Rate.XP.Kill.Elite`, non-raid dungeon bonus hook),
  group rate and the vmangos group split by level. Table tests cover levels 1–60.
- `PlayerXpTable`: XP needed per level, with no XP at or above `MaxPlayerLevel`.
- `PlayerProgression` (implements `IPlayerExperience`/`IQuestExperience`):
  `GiveXp` (dead players get nothing; rested bonus is consumed on kills only),
  multi-level `GiveLevel`, base health/mana/stat recalculation from an optional
  level stats table, `SMSG_LOG_XPGAIN`, `SMSG_LEVELUP_INFO`, and the `LevelChanged`
  event (which saves the player).
- `KillRewards` and `ProgressionFeature`: kill XP on `UnitKilled`. A solo player gets
  full XP. In a group, online members on the same map within
  `Progression:GroupXpDistance` (3D) share it by level, and grey kills give nothing.
- `PlayerLevelStatsTable`: a fail-closed CSV, one row per line in the form
  `race,class,level,basehp,basemana,str,agi,sta,int,spi`. A malformed or duplicate
  row rejects the whole file.

### Quest rewards (`QuestNpcServices.Rewards.cs`, `QuestRewardPlan`)
- Quest XP: `Quest.XpValue(playerLevel)` (with the level-difference reduction) ×
  `Quests:RateXpQuest`. It is granted only below max level. The level reached is
  computed before settlement and persisted with it (`Level`, `LevelPlayedTime`). At
  max level, `RewMoneyMaxLevel` is added to the money reward.
  `SMSG_QUESTGIVER_QUEST_COMPLETE` carries the real XP.
- Required items are destroyed inside the same staged inventory transaction as the
  reward grants (`TryStageQuestRewards(grants, removals, …)`). A delivery that frees
  slots for its own reward works. If a required item is missing, the whole reward
  fails.
- Reward spells (`RewSpellCast`, else `RewSpell`) and reputation run through
  `IQuestRewardEffects.QuestRewarded`. That happens exactly once, after the
  settlement is durable and released (`PublishRewardEffects`). Recovery paths never
  re-publish.
- Reputation: any world feature that implements `IQuestReputationRewards` gets
  `RewardQuestReputation(player, quest)`. It is found among the registered
  `IWorldFeature`s, so `WorldFeatures.cs` is not edited. This branch stores no
  reputation; `feat/reputation` owns that.

### Objective adapters (`QuestNpcServices.Progression.cs`, `QuestObjectiveAdapter`)
- Item collection: per-player `PlayerInventory.ItemCountChanged` drives
  `ItemAdded`/`ItemRemoved`. Counts are recomputed from the inventory, bank
  included. At login, `ReconcileItemCounts` repairs counters after a relog or crash.
- Exploration: `Quests:AreaTriggerQuests` holds the areatrigger_involvedrelation rows.
  `CMSG_AREATRIGGER` calls every world feature that implements the new
  `IAreaTriggerListener` after the zone check and before teleports. Only living
  players get credit.
- Spell-cast credit: `SpellSystem.SpellHitTarget` → `CastedCreatureOrGo`. When the
  caster is not the original caster, only `QUEST_FLAGS_SHARABLE` quests get credit.
- Gameobject use: `QuestNpcServices.CastedCreatureOrGo(player, goEntry, goGuid,
  isCreature: false, spellId, originalCaster)` is the hook. No gameobject feature exists yet.
- Group kill credit: every eligible group member (same map, within distance, alive
  or not yet a ghost) gets `KilledMonsterCredit`. Raid groups credit only raid quests
  (type 62).

### Accept / abandon / repeatable
- Repeatable quests (`QUEST_SPECIAL_FLAGS_REPEATABLE`) can be accepted again after
  being rewarded. The rewarded history row is kept with status None. 1.12 has no
  dailies, so there is no reset timer.
- Source items (`SrcItemId`/`SrcItemCount`) are given on accept. A full inventory
  refuses the accept with the equip error. They are taken back on abandon (bank
  included) unless the quest also requires that item.
- `AcceptableQuest` widens the old journal-only gate to quest types 0/1/21/62/81.
  These stay denied: `SrcSpell`, `ReqSource*`, PartyAccept, AutoRewarded, StayAlive,
  reputation objectives without a reputation owner, and exploration quests without a
  relation.

## Allowlist (not bypassed)
`Quests:OrdinaryRewardQuestIds` is still required for every reward. Supported rewards
are broader now. XP needs an `IQuestExperience` owner, and reward spells need
`IQuestRewardEffects.CanCastRewardSpell`. Objectives must be coherent (counts with ids,
no mixed creature/GO sign confusion). If any of these is missing, the reward fails
closed.

## Configuration
```jsonc
"Progression": { "MaxPlayerLevel": 60, "RateXpKill": 1, "RateXpKillElite": 1,
                 "GroupXpDistance": 74, "LevelStatsPath": null },
"Quests": { "OrdinaryRewardQuestIds": [ ... ], "RateXpQuest": 1,
            "AreaTriggerQuests": [ { "TriggerId": 0, "QuestId": 0 } ] }
```

## Shared-file edits
| File | Change |
| --- | --- |
| `Game/Entities/Player.cs` | `ResetLevelPlayedTime(nowMs)` (level-up resets level played time) |
| `Game/Items/PlayerInventory.Rewards.cs` | staged removals (`Removed`, new `TryStageQuestRewards` overload); removals apply and notify first |
| `Game/Npc/QuestNpcServices.cs` | `QuestNpcDependencies.RewardEffects` (optional) |
| `Game/Npc/QuestNpcOptions.cs` | `AreaTriggerQuests` |
| `Game/Npc/QuestNpcServices.Interaction.cs` | repeatable accept, source items, `AcceptableQuest` |
| `Game/Npc/QuestNpcServices.Objectives.cs` | raid/original-caster rules, inventory counts, `Pending` |
| `Game/Npc/QuestNpcServices.QuestMenu.cs` | reward display uses inventory counts |
| `Game/Npc/QuestNpcServices.Rewards.cs` | XP, max-level money, required item removal, repeatable rows, effects publication, allowlist rules |
| `Game/Npc/QuestRewardPlan.cs` | giver, experience, level before/after |
| `Game/Quests/QuestPackets.cs` | `Complete(quest, experience, money)` |
| `Game/Spells/SpellSystem.Effects.cs` | `SpellHitTarget` event |
| `World/Teleport/TeleportHandlers.cs` | calls the world features that implement `IAreaTriggerListener` |
| `World/Npc/QuestNpcFeature.cs` / `.Rewards.cs` | progression wiring, item listeners, level in the settlement image, effects after settlement |
| `World/Npc/QuestObjectiveAdapter.cs` | group kill credit, spell-hit credit |
| `Data/Quests/EfCharacterQuestRewardStore.cs` | repeatable rewarded rows (status 0) accepted as a fresh settlement |
| `Data/Quests/EfCharacterQuestStore.cs` | a rewarded row is kept only against a non-rewarded incoming row |

## Schema
None used. Limitation: **XP within the current level isn't persisted**. The
`characters` table has no XP column, so a relog resets the bar to 0 (level is
persisted). Fixing this needs a reserved characters migration that adds `xp` (and
`rest_bonus`, for rested XP).

## Limitations / known gaps
- No rested XP accumulation (inn/city resting) and no persistence. The rest pool is
  in memory only.
- Level-up doesn't grant talent points or skill increases, and doesn't update
  health/mana regen formulas beyond base values.
- Kill XP goes to the killing-blow player and their group. Tap ownership, pet/totem
  kills, `CREATURE_FLAG_EXTRA_NO_XP`, and the battleground/PvP honor paths are not
  handled.
- Kill XP earned while that character's quest settlement is pending is dropped,
  because the settlement owns its level and state until publication.
- Giving the source item and writing the quest row are not one transaction. The
  login reconcile fixes counters, but an orphaned source item stays in the inventory.
- Repeatable "turn in without accepting" (`CanCompleteRepeatableQuest` from the
  giver's ender list) isn't implemented.
- These remain denied: `SrcSpell`, `ReqSource*`, PartyAccept, StayAlive and the PvP
  quest type. Timed quests keep the existing quest-log timer.
- Area trigger relations come from config only, with no world-DB table/loader yet.
- Gameobject use has only the hook. Nothing calls it until a gameobject feature exists.
- The native mock-client scenario isn't extended. It still passes, but there are no
  new XP/exploration wire checks.
