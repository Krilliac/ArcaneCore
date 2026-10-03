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
- Reward spells (`RewSpellCast`, else `RewSpell`) are accepted in exactly one of two
  shapes (`QuestRewardEffects`, `IQuestRewardEffects`). Anything else keeps the quest
  unsupported, so it is never offered for turn-in.
  - **Pure grant**: every effect is `LearnSpell` or `CreateItem`. The spell is never
    cast. `TryPrepareRewardSpell` resolves it before anything is held: spells the
    player already knows are skipped (`ISpellbook.HasSpell`), and a created item's
    template must exist. Its count is the effect value rolled once and frozen,
    clamped to 1..`Stackable`. Created items join the quest grants in the same
    staged inventory (after them), so the full-inventory refusal, item GUIDs and
    `SMSG_ITEM_PUSH_RESULT` (created flag set) come from the existing path. The learned
    spells and the final inventory go into `CharacterQuestRewardRequest.LearnedSpells`
    and the same serializable transaction as the journal row, money and level
    (`EfCharacterQuestRewardStore`), so there is no durable-intent table and no
    characters schema change. After the commit the cached spellbook adopts the rows
    (`SpellbookCache.AdoptCommitted`, no write queued), and only the original live
    player is sent `SMSG_LEARNED_SPELL` (inside `ApplyReward`, before the quest-complete
    packet). Passive learned spells are cast after the hold is released
    (`SpellSystem.CastLearnedPassive`). A replacement Player never receives a replay: it
    loads the committed rows at login.
  - **Transient**: nothing durable. It is cast once after the settlement is released
    (`PublishRewardEffects`), for the original online player only. Nested triggers must
    be transient; cyclic or unknown chains are refused. Executable effects are checked
    against the active handler registry. Every aura-applying effect (`ApplyAura` and all
    `ApplyAreaAura*`/`PersistentAreaAura`) needs its aura handler, a finite duration and
    a nonpassive spell. `TeleportUnits` and `Summon` are accepted only while their
    built-in handler is still installed (`SpellSystem.HasBuiltInEffectHandler`) and
    only for the reward spell itself, never a nested one; every other teleport or
    summon effect, and any replaced handler, is refused because preflight cannot model
    unknown handler code. Their preflight runs at preparation, before any hold, and
    again at publication:
    - teleport: the destination resolves (`SpellSystem.TryResolveTeleportDestination`,
      shared with the effect) and `ITeleportSink.CanTeleport` accepts it. For players
      that is `TeleportService.CanTeleportTo`, which runs the checks of `TeleportTo` in
      the same order and changes nothing;
    - summon: an `ISpellSummonSink` exists, the caster is in a map, the entry is set and
      `ISpellSummonSink.CanSummon` accepts it. The daemon registers no summon sink, so
      summon rewards stay refused there.
  - **Mixed** spells (a grant plus anything else) are refused: the transient half
    could not be cast without repeating the grant. Other permanent effects
    (`LearnPetSpell`, skills, reputation, enchant, bind, `SummonChangeItem`, scripted
    effects, ...) stay refused.
  - A refusal at preparation returns before any hold: journal, inventory and the
    reward choice are untouched and the reward stays available. The client gets no
    reply (an inventory refusal still sends its equip error); its offer window stays open.
  - A transient teleport or summon that fails at publication, after a successful
    commit, is logged and lost. It was never persistent, and the preflight narrows the
    window but cannot close it.
- Reputation rewards (`RewRepFaction1..5`/`RewRepValue1..5`, world schema v10) go through
  `IQuestReputationSettlement` (`ReputationService`), passed as
  `QuestNpcDependencies.ReputationRewards` only when Faction.dbc is loaded. Pairs with
  a zero faction or value, and unknown or reputation-less factions, are skipped exactly
  as vmangos `RewardReputation` and `ReputationService.RewardQuest` skip them. A quest
  with any pair that counts and no reputation owner is unsupported. `TryStage` rolls each
  gain once (dither included), applies it to a deep copy of the player's standings and
  returns the faction rows (standing and flags). Those rows are written by the reward
  transaction (upsert, last writer wins). `Publish` runs inside `ApplyReward`: it replays
  the frozen gains through the normal apply/notify path without queueing a write, then
  requires the live rows to equal the staged rows. On a mismatch it queues the live rows
  and logs a warning.
- While a settlement holds a character, `ReputationService.Change` (so
  `ModifyReputation`, `SetReputation`, kill and quest rewards), `SetAtWar`, `SetInactive`,
  `SpellbookCache.ForgetSpell` and the `.unlearn` command are refused. A queued write
  carries full standing or removal rows and could revert the committed reward. A refused
  client flag toggle is not echoed, as with refused inventory operations.
  `SetWatchedFaction` is not guarded: it writes its own table, which the transaction never
  touches.
- The old `Progression.IQuestReputationRewards` hook (a world feature implementing
  `RewardQuestReputation`) was never implemented by anyone and is removed.

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
are broader now. XP needs an `IQuestExperience` owner, reward spells need
`IQuestRewardEffects.CanCastRewardSpell`, reputation rewards need an `IQuestReputationSettlement`. Objectives must be coherent (counts with ids,
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
| `Game/Items/PlayerInventory.Rewards.cs` | `InventoryRewardGrant.Created` (spell-created items push with the created flag) |
| `Game/Npc/QuestNpcServices.cs` | `QuestNpcDependencies.ReputationRewards` (optional) |
| `Game/Npc/QuestNpcServices.Rewards.cs` | reward-spell and reputation preparation before any hold, created items appended to the grants, learned-spell/reputation publication in `ApplyReward`, post-release publication |
| `Game/Npc/QuestRewardPlan.cs` | `SpellGrant`, `Reputation` |
| `Game/Progression/QuestProgressionContracts.cs`, `QuestRewardSpells.cs`, `QuestRewardSpellGrant.cs` | `IQuestRewardEffects` is now prepare / announce / publish; shared caster resolution; the frozen grant types |
| `Game/Quests/Quest.cs`, `Kernel/Quests/QuestTemplate.cs` | `RewRepFaction1..5`, `RewRepValue1..5` |
| `Game/Reputation/*` | `IQuestReputationSettlement`, `QuestReputationStage`, `PlayerReputation.Clone`, hold guards |
| `Game/Spells/SpellSeams.cs`, `SpellTargetingSeams.cs` | `ITeleportSink.CanTeleport`, `ISpellSummonSink.CanSummon` |
| `Game/Spells/SpellSystem*.cs` | `HasBuiltInEffectHandler`, `TryResolveTeleportDestination`, `AnnounceLearnedSpell`, `CastLearnedPassive` |
| `Game/Teleport/TeleportService.cs` | `CanTeleportTo` (the checks of `TeleportTo`, nothing changed) |
| `World/Progression/QuestRewardEffects.cs` | shape classification, preparation and preflight |
| `World/Spells/SpellbookCache.cs`, `SpellCommands.cs`, `WorldSpellSinks.cs` | `AdoptCommitted`, hold guard for `ForgetSpell`/`.unlearn`, `CanTeleport` |
| `Kernel/Quests/ICharacterQuestRewardStore.cs`, `Data/Quests/EfCharacterQuestRewardStore.cs` | `LearnedSpells`, `ReputationAfter` committed in the reward transaction |
| `Data/Quests/QuestReputationRewardWorldModule.cs` | new: world schema v10 |
| `Game/Quests/QuestPackets.cs` | `Complete(quest, experience, money)` |
| `Game/Spells/SpellSystem.Effects.cs` | `SpellHitTarget` event |
| `World/Teleport/TeleportHandlers.cs` | calls the world features that implement `IAreaTriggerListener` |
| `World/Npc/QuestNpcFeature.cs` / `.Rewards.cs` | progression wiring, item listeners, level in the settlement image, effects after settlement |
| `World/Npc/QuestObjectiveAdapter.cs` | group kill credit, spell-hit credit |
| `Data/Quests/EfCharacterQuestRewardStore.cs` | repeatable rewarded rows (status 0) accepted as a fresh settlement |
| `Data/Quests/EfCharacterQuestStore.cs` | a rewarded row is kept only against a non-rewarded incoming row |

## Schema
World v10 (`QuestReputationRewardWorldModule.Version`): the ten `quest_template` reputation-reward columns, added with default 0 to databases created before the step (a fresh database already has them). No characters or auth change: learned spells and faction rows go into the existing `character_spell` (v4) and `character_reputation` (v7) tables inside the reward transaction. Limitation: **XP within the current level is not persisted**. The
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

## Reward collaborators (handoff item 5): delivered scope, limits, provenance

Delivered: atomic `LearnSpell`/`CreateItem` reward spells and quest reputation rewards in the
reward transaction; destination and summon-owner preflight for transient teleport and summon
rewards; the area-aura family sharing the finite/nonpassive guard; hold guards for reputation
and spellbook removal. Details are in the "Quest rewards" section above and in
[quest-settlement-async.md](quest-settlement-async.md).

Differences from vmangos that are deliberate:
- A `CreateItem` reward that does not fit in the bags refuses the whole turn-in (the equip
  error is sent and the reward stays available). In vmangos the item is lost and the turn-in
  completes. This also applies to any quest item that does not fit, as before.
- Spells that mix a grant with anything else are refused (see above).
- Created items are announced with `SMSG_ITEM_PUSH_RESULT` carrying the created flag, one packet
  per grant. vmangos announces per stored stack and sets the item creator field for
  non-consumable, non-quest items; the creator field is not set here.

Limits:
- The area-aura and non-base teleport/summon holes were latent: nothing in `src` registers a
  handler for them, so no shipped quest reaches them. The tests register synthetic handlers.
- Summon rewards stay refused in the daemon: no `ISpellSummonSink` is registered. They are
  covered with fakes only.
- Transient teleport and summon rewards can still fail after the commit (the destination or
  instance became unavailable). They are logged and lost, never replayed.
- The reputation queue now retains failed writes (handoff item 2). The settlement retries the
  character's retained writes with `ReputationFeature.FlushCharacterAsync` before the transaction and
  refuses while they are not durable (`QuestRewardAsyncRecoveryTests.RetainedReputationWrite_*`).
- The extra serializable reads and writes were exercised on SQLite only in this lane. The
  MariaDB 10.11 and PostgreSQL 16 matrix (next-key locks on `character_reputation` /
  `character_spell`, PostgreSQL 40001 retries) is exercised by the hosted CI run, not here.
- SMSG_LEARNED_SPELL, the item push and the faction standing UI have no real-client capture.
- vmangos behaviours cited here (DoCreateItem clamp and created flag, RewardReputation skip
  rule, packet order) were applied from recollection of the reference and cross-read against
  the existing ArcaneCore ports. They were not re-checked against a 4b3d241 checkout in this
  lane, which had no network or reference tree. The packet order between the spell,
  reputation and quest-complete packets is therefore unproven.
