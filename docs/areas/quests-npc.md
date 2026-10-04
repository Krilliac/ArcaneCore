# Quest and NPC daemon adapters

M13a loads and persists journals, serves quest/text queries, and expires timed quests.
[M13b](../../MILESTONE_M13B.md) adds the bounded live-creature status/details/accept/abandon
surface. [Acceptance](../M13B_ACCEPTANCE.md) documents the mock and later client procedures.

The creature adapter reads the actual player map, visible GUID set, creature template entry,
spawn ID, current NPC flags, health/death state, combat, selectability, charmer, position, and
bounding radius. An injectable immutable catalog or optional developer-supplied
FactionTemplate.dbc establishes hostility. Unknown, nonzero NPC faction IDs, and contested-guard
templates deny interaction until their reputation/PvP state adapters exist.

No schema version is introduced. CreatureTemplateRow's version-2 model remains unchanged.
Gossip-menu and trainer metadata do not exist in that model, so these M13a/M13b creature adapters
never consume those fields or register gossip, vendor, trainer, completion, or reward behavior themselves.
That behaviour was delivered later by separate features on newer schema modules: gossip routing, vendors,
repair, trainers, innkeepers, bankers, spirit healers and flight masters are described in
[the NPC services notes](../integration/npc-services.md). Non-repeatable journal-only quests
can be accepted and abandoned completely without inventing source-item or spell effects.

Related quest details do not require acceptance eligibility, as in the pinned read-only query
handler. Mutating acceptance enforces that eligibility. Status reads visible known-faction
creatures independently of interaction range/liveness. Repeatables remain outside journal
mutation until their rewarded history can be handled by completion and timer rules.

Remaining work: establish Faction.dbc/reputation state, import actual gossip/trainer metadata via
an additive upgrade, connect item/spell/combat objective events, and provide transactional
reward collaborators. These require separate bounded slices and evidence.

## Quest tooling and item-started quests (lane L11-quest-tickets-and-commands)

Evidence: mangos zero (`/home/user/mangosserver/server`) `ChatCommands/QuestCommands.cpp:47-283`, `WorldHandlers/Chat.cpp:527-533,801`,
`WorldHandlers/QuestHandler.cpp:171-181,264-273,878-903`, `Object/Item.h:393`, `Object/PlayerQuest.cpp:690-803,855-875,1637-1667`,
`Tools/Language.h:421-423`. GM tickets are not part of this slice: they shipped with the gm-audit lane (`World/Gm/Audit/`).

### Implemented

* **`.quest add|complete|remove #quest_id|[$quest_title]|#shift-click-quest-link`** (`src/ArcaneCore.World/Gm/Quest/QuestCommands.cs`,
  an `ICommandGroup` on the retail root `quest`; the three sub-commands are retail level 6 as in the mangos table, the root 3).
  The target is the selected player or the invoker (mangos `getSelectedPlayer`; a creature selection answers "No character selected.").
  The Game-side entry points are `QuestNpcServices.GmAddQuest`, `GmRemoveQuest` and `GmCompleteQuest`
  (`src/ArcaneCore.Game/Npc/QuestNpcServices.Interaction.cs`); they run on the world thread and hand every delta to the retained-write
  quest persistence before returning, so a restart keeps what a GM did.
  * `add`: refuses an item-started quest with LANG_COMMAND_QUEST_STARTFROMITEM (the item store's `QuestStartingItem`), then mangos
    `CanAddQuest` (a free slot, the source item storable) and `AddQuest` (status, timer, PvP flag, source item, delivery counters, the
    completion check). Silent on success, as in mangos.
  * `complete`: the missing delivery items are stored and announced (skipped silently when they do not fit), every kill, cast and
    game-object objective is credited to its count with one SMSG_QUESTUPDATE_ADD_KILL each (a kill objective naming a creature without
    a template is skipped, mangos `ObjectMgr::GetCreatureTemplate`), the reputation objective is set through the reputation feature,
    the required money is given, then the quest is forced COMPLETE and its log slot marked. Silent on success.
  * `remove`: every log slot holding the quest is cleared, the source item taken back silently (`TakeQuestSourceItem`, an un-equippable
    one is left), the status becomes NONE and the rewarded flag is reset ("Quest removed.").
* **`.quest status [#quest_id|...]`** (ArcaneCore's, level 3): the target's log (slot, link, status) or one quest's counters, explored
  flag, timer end and rewarded flag. Read-only.
* **Item-started quests** (`item_template.startquest`): CMSG_QUESTGIVER_QUERY_QUEST and CMSG_QUESTGIVER_ACCEPT_QUEST accept an item in
  the player's bags as the quest giver when its template starts the quest (mangos `Item::HasQuest`, the `TYPEMASK_..._OR_ITEM` lookups;
  the only interaction check for an item giver is that the player is alive). The details carry the item's GUID. On accept the item is
  destroyed, whole stack, unless the quest requires it back (a `ReqItemId`) or hands it out as its source item (`PlayerQuest.cpp:780-803`).
  The inventory walk is per request, never per tick.

### Deliberate differences from mangos (no switch)

* `add` refuses a quest already in the log (mangos gives it a second slot) and a quest the support gate withholds (it would
  half-work; the reply names the missing adapters). A full log and a refused source item are reported to the invoker as well as to
  the player (mangos only tells the player). Neither CanTakeQuest nor the rewarded flag is consulted, as in mangos: a quest already
  rewarded stays un-rewardable until `remove` resets the flag.
* `complete` refuses a FAILED quest ("remove it and add it again"): ArcaneCore's turn-in re-checks the objectives, so mangos's forced
  status alone would leave a quest that can never be rewarded. For the same reason an exploration/event quest gets its explored flag
  set here.
* `remove` of a quest the player has no status row for writes nothing (mangos stores a NONE row).
* The invoker must outrank the target (`CommandContext.CanActOn`), the ArcaneCore convention for every GM command.
* The quest may be named by its exact title (`[Title]`), like `.additem`'s item name.

### Known gaps

* `LiveItemTemplateStore` does not forward `QuestStartingItem` (the interface default answers 0), so the item-started check of
  `.quest add` reads the store through `LiveItemTemplateStore.Unwrap`; a non-`ItemTemplateStore` implementation behind the live store
  answers "no starting item". The abandon path's `TakeOrReplaceQuestStartItems` (`QuestNpcServices.Progression.cs`, not this lane's)
  calls the live store directly and is affected by the same gap; reported, not changed here.
* The reputation objective of `.quest complete` is left alone when no reputation feature is present (the Game layer's
  `IPlayerReputation` has no setter).
* mangos's `quest_tracker` bookkeeping (`completed_by_gm`) has no counterpart: ArcaneCore keeps no quest tracker table.
* UNVERIFIED against a real 1.12.1 client: that the client sends CMSG_QUESTGIVER_QUERY_QUEST with the item GUID when a quest-starting
  item is used. The server side follows the reference handlers (both accept TYPEID_ITEM givers); the loopback tests drive the opcodes
  directly.

### Tests

`tests/ArcaneCore.World.Tests/Gm/Quest/QuestCommandTests.cs` (loopback): levels; add (log slot, persistence, duplicate, unknown id and
title, item-started refusal, withheld quest, link and title forms, source item grant); a full log (SMSG_QUESTLOG_FULL plus the reply);
complete (two ADD_KILL packets and the stored count, delivery items pushed and counted, money, a quest not taken, a creature without a
template); remove (unknown, never taken, source item taken back, rewarded flag reset and re-add); status; acting on the selected player
and a creature selection; the item-started flow (wrong quest, unseen creature, details with the item GUID, accept destroys the note,
a gone item offers nothing, a required start item stays and completes the quest, a dead player is refused).
