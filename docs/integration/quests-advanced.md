# Advanced quest mechanics (`claude/vw5-quests-advanced`)

Wave 4 lane "quests-advanced", based on `claude/vw4-integration` @ `7313b9e` (waves 1-3). The lane makes quests turn-in-able by
default, adds the autocomplete (Method 0) turn-in, retail refusal replies, quest sharing and party accept, source spells,
PvP quests, event credit through `SPELL_EFFECT_QUEST_COMPLETE`, reputation-objective re-evaluation, and several retail corrections to
the giver marks. Everything not delivered is listed under "Limits" below.

Provenance (read-only references, never copied): `D:\refs\vmangos` (primary), `D:\refs\wow_messages` for packet layouts. Every
formula and message below carries its vmangos `file:line`. The classic-db counts quoted in this file come from the lane's design
survey of `ClassicDB_1_12_1_z2815` and were not re-derived in this run.

## Headline behaviour change

At the base branch **no quest could be turned in with the default configuration**: `SupportedRewardQuest` needed the quest id in
`Quests:OrdinaryRewardQuestIds` (default empty) and `Method == 2`. vmangos has no such allowlist (`Player::CanRewardQuest`,
`Player.cpp:12682-12739`). The lane replaces it by a computed gate (below) and flips the default:

* `Quests:RewardMode = AllSupported` (default, retail): every quest whose needs all have adapters is offered and rewarded.
* `Quests:RewardMode = AllowlistOnly`: the previous behaviour (`OrdinaryRewardQuestIds` is then honoured). Existing tests that
  assert the allowlist now set this mode explicitly.

Rewarding paths that were previously exercised only by allowlisted test quests (XP, reputation staging, reward spells) now run for
real data. The guards of the old gate are all kept (reputation owner for rep gates and rewards, `IQuestExperience` for XP, reward
spell preflight, coherent objectives and rewards); the operator can fall back to `AllowlistOnly` at any time. The startup log lists
what is still withheld and why.

## Delivered

### Support gate (`QuestNpcServices.Support.cs`, commit 229faf2)
`QuestNeeds.Of(quest)` is a pure classifier into `QuestAdapter` flags; `IQuestAdapterModule` providers are discovered by reflection
(parameterless constructor, ordered by full type name; two providers of one adapter fail at startup). A quest is supported iff
`needs & ~provided == 0`; `AcceptableQuest` (accept, abandon, area trigger) and `SupportedRewardQuest` are thin wrappers.

| Need | Source | Provided by |
| --- | --- | --- |
| Autocomplete | Method 0 | `AutoCompleteAdapter` |
| PartyAccept | flag 0x2 | `PartyAcceptAdapter` when the host has an `IQuestParty` |
| SrcSpell | `SrcSpell != 0` | `SrcSpellAdapter` when the host has an `IQuestSpellCaster` |
| ReqSource | `ReqSourceId/Count` | natively: vmangos reads it only in `HasQuestForItem` (`Player.cpp:14298-14316`), already implemented |
| PvpType | type 41 | `PvpQuestAdapter` |
| RepObjective | `RepObjectiveFaction` | natively when a reputation owner exists |
| EventCredit | SpecialFlags EXPLORATION_OR_EVENT | an area-trigger row (config) or a quest-complete spell (`QuestCompleteSpellEffect`) |
| Mail | `RewMailTemplateId != 0` | **nobody**: withheld, see Limits |
| AutoRewarded | flag 0x400 | **nobody**: withheld |

Flags 0x1 (STAY_ALIVE) and 0x4 (EXPLORATION) are "Not used currently" in vmangos (`QuestDef.h:150-152`) and quest types 82, 83, 84
only select a client icon, so none of them is a need any more (the base withheld them). Coverage by modules is read per query
because the spell store loads after the quest feature; the startup report is posted to the first world tick for the same reason.

### Autocomplete turn-in and retail refusal replies (bb02d52, c209fdd)
* `QuestNpcServices.TurnIn.cs`: `RewardBase` + `RewardRequirements` = vmangos `CanRewardQuest` (`Player.cpp:12682-12739`):
  an autocomplete quest needs `CanTakeQuest(skipStatusCheck)` (class/race/level/skill/chain gates still apply), any other quest must
  be accepted and complete; a rewarded quest never rewards twice; delivery items must be in the bags, not the bank.
* The reward store request gains `InsertIfMissing`: the autocomplete quest is rewarded without a journal entry; the settlement
  claims or inserts the durable row in the same serializable transaction as money and inventory
  (`EfCharacterQuestRewardStore`). A repeatable one returns to status NONE with `Rewarded = true` (`RewardQuest`,
  `Player.cpp:13081-13250`).
* Refusals: missing delivery item is `EQUIP_ERR_ITEM_NOT_FOUND` naming the item (`Player.cpp:12708-12716`); not enough money is
  `QUESTGIVER_QUEST_INVALID` 22 (`Player.cpp:12722-12726`, sent regardless of `msg`); bag space is `QUESTGIVER_QUEST_FAILED` 4
  and a unique-item clash 17 (`Player.cpp:12755-12774`), no longer equip errors; after each of these the offer-reward window
  is sent again (`QuestHandler.cpp:262-271`).
* `CMSG_QUESTGIVER_COMPLETE_QUEST` goes through the retail redirect (`GossipDef.cpp:484-497`): the request-items window with the
  completable flag, replaced by the offer-reward window when the quest has no request text, or no items and is complete.
  `CanCompleteRepeatableQuest` (`Player.cpp:12663-12680`) is implemented for that request. `CMSG_QUESTGIVER_REQUEST_REWARD` offers an
  autocomplete quest on `CanTakeQuest` alone (`CanCompleteQuest`, `Player.cpp:12612-12614`).

### Share quest and party accept (c4e2ef7)
`CMSG_PUSHQUESTTOPARTY` (0x019D), `MSG_QUEST_PUSH_RESULT` (0x0276), `CMSG_QUEST_CONFIRM_ACCEPT` (0x019B), `SMSG_QUEST_CONFIRM_ACCEPT`
(0x019C); layouts from wow_messages `quest/*.wowm` (message numbers are the 1.12 `QuestPartyMessage`, versions "1 2": SHARING 0,
CANT_TAKE 1, ACCEPT 2, DECLINE 3, TOO_FAR 4, BUSY 5, LOG_FULL 6, HAVE_QUEST 7, FINISH_QUEST 8; the 3.3.5 enum in the same file has
different numbers and no TOO_FAR).
* Push order per member as vmangos (`QuestHandler.cpp:403-459`): SHARING, TOO_FAR (3D, strictly inside 14.0, `Object.h:72`),
  FINISH_QUEST, HAVE_QUEST, CANT_TAKE_QUEST, LOG_FULL, BUSY, then the details window from the pusher and a pending offer.
* Accept from a player (`QuestHandler.cpp:107-196`): the giver must hold a sharable quest (`CanShareQuest`, `Player.cpp:13795-13802`),
  both alive (`Player.cpp:2437`); an offer for the quest must still be pending with the sharer on the map within 14.0, else the sharer
  is told TOO_FAR; success tells the sharer ACCEPT_QUEST; a shared timed quest takes the sharer's remaining time
  (`Player.cpp:12855-12860`).
* PARTY_ACCEPT (`QuestHandler.cpp:166-191`): after an accept, every other member on the map who could take the quest gets a pending
  offer and `SMSG_QUEST_CONFIRM_ACCEPT`; `CMSG_QUEST_CONFIRM_ACCEPT` (`QuestHandler.cpp:332-381`) adds the quest.
* `MSG_QUEST_PUSH_RESULT` from the receiver is forwarded to the sharer with the receiver's guid (`QuestHandler.cpp:461-474`).
* Config `Quests:SharePushRequiresQuest` (default **false = retail**): vmangos does not check that the pusher holds the quest (only the
  later accept does); switching it on refuses such pushes up front.
* Deviations: players on different maps are never within sharing distance (vmangos compares coordinates only); the offer lives in
  memory with the receiving player.
* The group lookup is a new `IQuestParty` dependency, implemented by `WorldQuestParty` over the social feature's group manager and
  the online registry; `QuestShareHandlers` registers the three opcodes (payload lengths are enforced).

### Accept side effects (f4a7cd1)
Source spell cast on the player after the accept and the gossip close (`QuestHandler.cpp:202-203`) through `IQuestSpellCaster`
(`SpellSystemQuestCaster`, a triggered self cast); a type 41 quest sets the PvP flag (`Player.cpp:12866-12867`,
`MapCombat.UpdatePvp`).

### Event credit by spell (a5cb9e4)
`SPELL_EFFECT_QUEST_COMPLETE` (16): `Spell::EffectQuestComplete` (`SpellEffects.cpp:5324-5331`) credits `AreaExploredOrEventHappens(EffectMiscValue)`
to a player target. `QuestCompleteSpellEffect` is both the spell handler module and the adapter that marks the quests those spells
name as covered. The quest feature installs its sink on the spell system (`QuestSpellEvents`, same lookup shape as the duel service).

### Reputation objectives (c78baca)
`QuestNpcServices.ReputationChanged` (`Player::ReputationChanged`, `Player.cpp:14239-14264`) and the seam `IReputationChangeSource`. The
**reputation lane must raise the event** from its change path (`ReputationService.ApplyAndNotify`) and register an implementation in the
service container; until then `QuestReputationBinding` subscribes nothing and logs once, and reputation objectives refresh only on
accept and login (as before).

### Giver marks (1599359)
Quest level 0 (and negative) uses the player's level (`Player.h:1114`); the status query only refuses hostile creatures
(`QuestHandler.cpp:36-77`, it never tested the quest-giver npc flag); an autocomplete repeatable quest shows RewardRep, not Reward2
(`QuestHandler.cpp:517-526`).

### Schema (9cffb13)
World step 21 (`QuestAdvancedWorldModule.Version`): `quest_template.RewMailTemplateId` (signed: negative = sent by the quest giver) and
`RewMailDelaySecs`, default 0, mapped by name from the dump (`ObjectMgr.cpp:5558`). **The integrator renumbers this constant.** No
characters schema change, hence no `ICharacterDataCleanup` (the reward rows live in the existing `character_queststatus`).

## Provider verification (hosted-CI rule)
Local runs are SQLite only. The provider theories (`TestDatabases.AvailableProviders`) of `QuestAutoCompleteRewardStoreTests` and
`QuestAdvancedSchemaTests` therefore ran on SQLite here and on MariaDB/PostgreSQL only on hosted CI. They are written against real
provider semantics: the insert-if-missing read under serializable isolation takes next-key locks on MariaDB and may raise a
serialization failure (40001) on PostgreSQL, which the settlement's reconcile path (`ReadRewardOutcomeAsync`, now aware of the
virtual row) absorbs because one settlement per character runs at a time; the upgrade test drops the two columns and sets the version
row back, so it covers the partially-applied restart on MariaDB (non-transactional, implicitly committing DDL) and the transactional
PostgreSQL case alike.

## Limits (not delivered, documented, no stubs)
* **Item-started quests** (204 quests have no NPC/GO starter) and the accept-by-item path: needs a capture of what the 1.12.1 client
  sends when a quest-starting item is used; vmangos only shows the query/accept handlers taking an item guid. Skipped.
* **Mail rewards** (74 quests): the column is carried, the quests are withheld (`Mail` need). Delivery needs system mail with items
  inside the reward transaction.
* **AutoRewarded quests** (flag 0x400, 1 quest) stay withheld.
* **Exploration quests without an area-trigger row** (179 by the design's count) stay withheld: `areatrigger_involvedrelation` is still
  config only (`Quests:AreaTriggerQuests`); no table, importer or `QuestContent.AreaTriggers`. `GroupEventHappens`/`GroupEventFailHappens`,
  EventAI quest actions (ids 15, 26, 33) and the escort driver (43 NPC-quest pairs) are not delivered: quests only creditable by
  scripts remain withheld.
* **GM `.quest add|remove|complete|status`** (`CharacterCommands.cpp:5042-5260`): not delivered (needs a reputation setter and item creation
  for `FullQuestComplete`).
* **Autocomplete quests with a timer** are refused at turn-in (the virtual row has no timer); whether classic-db has any was not checked.
* The dead-player turn-in exception for quest 3912 (`QuestHandler.cpp:255-266, 283-312`) and the final free-slot count rule of
  `CanRewardQuest(reward)` (`Player.cpp:12810-12816`; the staged inventory already decides storage as a batch) are not reproduced.
* Item objective updates do not write the quest-slot counter or batch by 63, and `FailQuest` on a repeatable untimed quest does not run the
  abandon path (`Player.cpp:14443-14466`, `13257-13263`): unchanged from the base.
* Flight-master npc flag stripping while a quest is offerable, quest-list greeting text, quest start/end DB scripts and spell_area quest
  auras: no engine in this repository.
* Real 1.12.1 client checks are outstanding for share/party accept and for the accept-from-player flow; unit and socket tests prove the
  server half only.

## Integration notes
* New optional members on `QuestNpcDependencies`: `SpellCaster`, `Party`. New options: `RewardMode`, `LogWithheld`, `SharePushRequiresQuest`.
* `QuestNpcServices.AcceptQuest` now has one accept core (`AddQuestFrom`) used by NPC, player (share) and confirm accepts; other lanes
  editing `Interaction.cs` should rebase on it.
* Spell effect 16 is registered by `QuestCompleteSpellEffect`: a second registration by another lane fails at startup (duplicate rule).
* A new `IWorldFeature` (`QuestReputationBinding`) and `IOpcodeHandlerGroup` (`QuestShareHandlers`) are discovered automatically.

## Tests
`QuestSupportGateTests`, `QuestTurnInTests`, `QuestShareTests`, `QuestAcceptEffectTests`, `QuestCompleteSpellEffectTests`,
`QuestReputationObjectiveTests`, `QuestStatusFidelityTests` (Game.Tests); `QuestShareWorldTests` (World.Tests);
`QuestAutoCompleteSettlementTests` (MockClient.Tests, the real socket, world thread and SQLite reward transaction for an autocomplete
quest, including relog); `QuestAutoCompleteRewardStoreTests`, `QuestAdvancedSchemaTests` (Data.Tests, provider theories). Tests changed
because behaviour changed on purpose: the allowlist tests (now `AllowlistOnly`), the full-bag assertions (now `QUESTGIVER_QUEST_FAILED`
then the offer window), and the fail-closed theory (only the quests that still lack an adapter).
