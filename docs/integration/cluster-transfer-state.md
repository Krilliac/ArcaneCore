# Cluster transfer-state inventory (M15.0 item 1)

Per-feature inventory of the state a player carries, split into what reaches the
characters database and what exists only in the memory of the one `World` process.
It is the input to the M15.3 "complete feature state capture/install" work in
[CLUSTERING_DESIGN.md](../CLUSTERING_DESIGN.md) and to the M15.0 decisions recorded in
[cluster-m15-0.md](cluster-m15-0.md). It changes no code.

## Basis and limits

- Read at `c3dea16` (the integration candidate) by static reading of source and
  the existing integration notes. Nothing was built, run or tested for this document, so
  every entry is a reading of the code, not a compiler or test result.
- Every claim carries `path:line` against that commit. Line numbers drift; re-check an
  entry before building on it.
- Other lanes were changing these areas while this was written: reputation durability
  (`ReputationWriteQueue`, the reputation store), durable loot/instance contents,
  economy settlement and auction recovery, character deletion, quest reward
  collaborators, and death removing auras (`ac-7a`). Entries for those areas describe `c3dea16`, not their lanes' results.
  Re-read them after those lanes merge.
- No real 1.12.1 client was involved. Nothing here says how a client observes a loss.

Legend. **D** durable: reaches the characters database, with the trigger. **M**
memory-only: gone when the process exits, crashes, or a character moves to another
process. **R** derived: rebuilt from durable or content state when the player enters a
map, so it never needs to travel. **P** process-wide authority: not per-player state,
but one process holds the only copy, which a second process would not see.

## 1. What reaches storage, and when

`Player.CreateSnapshot` (`src/ArcaneCore.Game/Entities/Player.cs:398`) builds a
`CharacterState` (`src/ArcaneCore.Kernel/Characters/CharacterState.cs:10-25`): id, map,
zone, position, orientation, level, played time, level played time, money, action bar
toggles, action buttons (only when changed), home bind, and the full inventory. It
holds nothing else, so it is **not** a handoff serialization. Everything outside that
record is saved, if at all, by a feature's own mechanism.

Core snapshots are enqueued on: logout/disconnect (`WorldRuntime.RemovePlayer`,
`src/ArcaneCore.Game/Maps/WorldRuntime.cs:237-249`), autosave every 15 minutes
(`WorldRuntimeOptions.cs:19`, `WorldRuntime.cs:291-298`), host stop (`WorldRuntime.cs:156`,
`SaveAll` at `:383-393`), and a few explicit `SavePlayer` calls: level up
(`ProgressionFeature.cs:133`), money/bind change (`QuestNpcFeature.cs:252`), GM commands
and a quest-journal adapter (`BuiltinCommands.cs:82,93`, `QuestJournalAdapter.cs:62,68`).
A pending quest settlement suppresses the snapshot (`WorldRuntime.cs:111,246,388`).

Seven write mechanisms exist, with no shared contract. `ICharacterSaveQueue.Enqueue`
returns `void` (`src/ArcaneCore.Game/Maps/IPlayerSession.cs:38`), so the simulation never
learns whether a snapshot reached storage.

| Mechanism | Where | On repeated failure | Surfaced to caller |
|---|---|---|---|
| Core character state + inventory | `World/Persistence/CharacterSaveQueue.cs` | 3 attempts (`:16,282`), then the snapshot is **kept** in `_failed` (`:313,323`), merged into the next save (`:203-206`, `:332-337`), retried at stop, which throws if undrained (`:251-254`) | Only settlement/flush callers get a `Task`; ordinary snapshots log and retain. `ResumeCharacter` discards the retained snapshot only for a quarantined character (`:135-139`) |
| Quest rows and taxi mask | `World/Npc/QuestNpcPersistence.cs` | Failure recorded and the authoritative snapshot retained (`:313-324`); login refuses to load until it is retried (`:50-53`) | Login barrier |
| Spellbook | `World/Spells/SpellbookCache.cs` | Failed characters are marked and retried; "the cache stays authoritative until restart" (`:325-338`, `:250-299`) | Logged |
| Spell state (auras, cooldowns) | `World/Spells/SpellStatePersistence.cs` | Failed save stays in memory so the same process can still restore it (`:188-190`, header `:9-15`); lost if the process dies | Logged |
| Social (friends, guilds) | `World/Social/SocialWriteQueue.cs` | 3 attempts, then **retained** and retried at the next write, login, logout and shutdown (like reputation); pending writes coalesced and bounded | Logged; shutdown throws naming what is not durable |
| Reputation | `World/Reputation/ReputationWriteQueue.cs` | 3 attempts, then **dropped** (`:97-100`) | Logged |
| Instance saves/binds | `World/Instances/InstanceWriteQueue.cs` | 3 attempts, then **dropped** (`:99-102`) | Logged |

Economy and quest-reward settlements are not queues: each is one store transaction
(`EconomyFeature.cs:23-27`; `docs/integration/quest-settlement-async.md`).

Consequence for a handoff barrier: the login path flushes only quest settlement,
`ICharacterSettlementBarrier` (implemented only by `EconomyFeature`, `EconomyFeature.cs:29`)
and the core queue (`World/Handlers/CharacterHandlers.cs:198-212`). There is no
"all feature writes for this character are durable" call.

## 2. Per-feature inventory

### Character core and session

| State | Class | Evidence | Note for transfer |
|---|---|---|---|
| Map, zone, position, orientation, level, played time, money, action bar, home bind | D, via snapshot (triggers above) | `CharacterState.cs:10-25`; `EfCharacterStore.cs:113-141` | Up to 15 minutes stale after a crash unless a trigger fired |
| Current health and power | M. A reload sets full health and starting power | `Player.cs:464-472` | A damaged player who relogs is whole |
| Current XP in the level, rested bonus | M. The characters table has no XP column; a relog resets the bar | `ProgressionFeature.cs:68` (`InitializeLoadedPlayer(player)` with the default stored XP of 0); `PlayerProgression.cs:193-194`; `quest-progression.md:125-132` | Needs a schema change before any handoff can preserve it |
| Player flags (GM, AFK, DND, PvP), AFK/DND text, GM chat | M | `Player.cs:135,145,152,155` | Not in `CharacterState` |
| Selection, movement counter, rooted state, last save request | M | `Player.cs:65,123,158,161` | |
| Logout countdown | M | `Player.cs:64,190,373` | |
| Visible-object set, pending update block, visibility dirty flag | R | `Player.cs:117,195,198` | Rebuilt on map enter |
| `IPlayerSession` (socket, cipher, scoped stores, account settings) | M, owned by the gateway side | `WorldSession.cs:47-102`; `Player.cs:101` | Must not travel; `IPlayerSession` is the only session surface Game sees (`IPlayerSession.cs:12-31`) |
| Account data and tutorial flags | D, in the **characters** database (`account_data`, `account_tutorial`) | `CharacterDbContext.cs:61-62`; loaded into `WorldSession.cs:102` | Session-scoped, not map-owner state; keyed by account, so a per-realm characters database holds a copy per realm |
| Session claim (one session per account) | P, process-local | `SessionRegistry.cs:12-31`; `WorldSession.cs:448,473` | The client's server-id field is discarded (`WorldSession.cs:405`) |
| Online registry, name index | P | `WorldRuntime.cs:22-23,54-63` | |

### Items and inventory

| State | Class | Evidence | Note |
|---|---|---|---|
| Equipment, bags, bank, keyring, with per-item count, creator, duration, charges, flags, enchant triples, random property, durability, text id | D, in the core snapshot | `PlayerInventory.cs:242-270`; `ItemInstanceData.cs:8-31` | Written as a whole; a null snapshot means "never loaded, do not overwrite" (`PlayerInventory.cs:239`) |
| Buyback slots | M by design (vmangos deletes them) | `PlayerInventory.cs:247-248`; `PlayerInventory.Vendor.cs:20-21` | Lost on logout, and would be on handoff |
| Bank bag slots bought | Not stored; purchases are refused | `NpcServicesFeature.cs:77-79` (no persistence delegate passed; `InventoryItemService.cs:29`) | |
| Item GUID counter | P. One in-process counter seeded from `MAX(item_instance.guid)` | `ItemTemplateStore.cs:47-78`; `ItemsFeature.cs:70`; also `EconomyFeature.Mail.cs:402` | Two processes would collide |
| Vendor session start time | M | `InventoryItemService.cs:31,128-130` | |

### Quests, gossip and flight paths

| State | Class | Evidence | Note |
|---|---|---|---|
| Quest statuses, objective counters, explored flag, reward choice, timer end (absolute unix seconds) | D, per change | `PlayerQuestLog.cs:7-23`; `QuestNpcFeature.cs:246-247`; `QuestNpcPersistence.cs` | Timers survive because the end is absolute |
| Quest log slot layout (update fields) | R from the statuses at login | `PlayerQuestLog.cs:49-120` | |
| Taxi known-node mask | D, per change | `QuestNpcServices.cs:45`; `QuestNpcFeature.cs:249-250` | |
| Gossip/dialog menu state | M | `QuestNpcServices.cs:42` | Safe to drop on handoff; the client reopens |
| "Loaded" flag and staged load data | M | `QuestNpcServices.cs:48`; `QuestNpcFeature.cs:35,195-198` | |
| Item-count listener wiring | M, reinstalled at login | `QuestNpcFeature.cs:37,207-221` | |
| In-flight settlement (operation GUID, hold flag) | M. Durable result is one transaction; the hold itself is memory | `Player.QuestSettlement.cs:5-14`; `QuestNpcFeature.Rewards.cs:21`; `quest-settlement-async.md` | A handoff must refuse while pending: teleports already do (`TeleportService.cs:89-91,232-234,266-268`) |
| Active taxi flight | M. Logout lands the player at the final node before saving | `TaxiFlightSystem.cs:20-27,42,135`; `NpcServicesFeature.cs:141-144` | Mid-flight state cannot be resumed |

### Spells, auras and cooldowns

| State | Class | Evidence | Note |
|---|---|---|---|
| Known spells | D, written through; the cache holds **every** character's spellbook, loaded once at startup | `SpellbookCache.cs:10-16,20,53-60` | P: a second process would hold a stale copy |
| Cooldowns (spell, category) and saveable auras | D **at logout only** (absolute unix ends, remaining time, stacks, charges, per-effect amounts, periodic timers) | `SpellFeature.cs:318-328`; `SpellSystem.Persistence.cs:64-69`; `SpellStatePersistence.cs:120-143` | Not part of autosave (`WorldRuntime.cs:383-393`) or the core snapshot. A crash, or any path that does not raise `PlayerLoggingOut`, loses them |
| Auras not saved: passive, channeled, party area aura received from another caster | M | `SpellAuraHolder.cs:143` | |
| Global cooldowns, school lockouts | M | `SpellCast.cs:80-84`; `SpellSystem.Persistence.cs:69` | |
| Cast in progress (generic/channeled slot) | M | `SpellCast.cs:71-72` | |
| Aura caster ownership tokens | M. A restored foreign aura gets a permanently revoked token and keeps the caster GUID as provenance only | `SpellSystem.Persistence.cs:166-175,204-208`; `aura-caster-ownership.md` | Cross-worker casters cannot be re-bound after a transfer |
| Per-unit spell state table | M | `SpellSystem.cs:20` | |
| Staged restore data | M | `SpellFeature.cs:41-42,229-236` | |
| Cooldown/aura clock | M. World-thread milliseconds, converted to wall clock only when saved | `SpellCast.cs:74-81`; `SpellSystem.Persistence.cs:79-95` | |

### Combat, death and corpse

| State | Class | Evidence | Note |
|---|---|---|---|
| Death state, death timer, pvp death, corpse reference | M. "Death state and corpses are not persisted yet" | `UnitCombat.cs:20,65,74,77`; `docs/integration/combat.md:33` | Death state is not restored and health is set to full at construction, so a relogged ghost is expected to come back alive (read from code, not run; a saved ghost aura could still be restored) |
| Victim, melee/attack timers, combat timer, mana-use timer | M | `UnitCombat.cs:13,23,26,46,60` | |
| Threat lists | M, hold direct references to units | `UnitCombat.cs:14` | Cannot cross a process boundary |
| PvP flag and timers | M | `UnitCombat.cs:82,85` | |
| Corpse object | M. Lives in the source map; a far teleport keeps the body reclaimable, logout removes it | `Corpse.cs:10`; `MapCombat.cs:54-60,129-133` | Corpse ownership stays with the source map in the design (CLUSTERING_DESIGN.md:273) |

### Progression and reputation

| State | Class | Evidence | Note |
|---|---|---|---|
| Level | D (core snapshot, saved on level up) | `ProgressionFeature.cs:133` | |
| Applied base-stat deltas | M, per player weak table | `PlayerProgression.cs:22,49-52` | Recomputed from level and stats table at login |
| Faction standings, flags (at war, inactive), watched faction | D, per change, via a queue that drops after 3 failures | `ReputationService.cs:143-167,228-235`; `ReputationFeature.cs:196-198`; `ReputationWriteQueue.cs:97-100` | Lane `ac-2` is changing this |
| Reputation object for the player | M, rebuilt from storage at login | `ReputationFeature.cs:86-99`; `ReputationService.cs:18` | |

### Social, groups, guilds and channels

| State | Class | Evidence | Note |
|---|---|---|---|
| Friend/ignore entries | D, per change via `SocialWriteQueue` (coalesced per row, bounded, retained on failure) | `SocialWriteQueue.cs:28-29,79-82`; `FriendsService.cs:49,82` | Loaded after login on a background task (`SocialFeature.cs:191-239`) |
| Guild, ranks, members, MOTD | D via the same queue; all guilds loaded once at startup into one manager | `GuildManager.cs:31-34,98,627`; `SocialFeature.cs:315-330` | P: one process owns the guild cache and the guild id counter |
| Group membership, leader, loot settings, invites, raid subgroups | M. "Group membership is kept in memory (not persisted)" | `GroupManager.cs:17-20,104`; `Group.cs:37-38`; `docs/integration/social.md` | P: group id counter starts at 1 every process start |
| Chat channels, members, bans | M, realm-wide structure | `ChannelManager.cs:14-15`; `Channel.cs:15-16` | P |
| Online presence and name directory | P | `CharacterDirectory.cs:14`; `SocialFeature.cs:31-33` | |
| Pending social commands and "ready" marker | M | `SocialFeature.cs:31-33,267-286` | |

### Instances

| State | Class | Evidence | Note |
|---|---|---|---|
| Instance saves (id, map, reset time), character binds, last instance, raid reset times | D, via `InstanceWriteQueue` (drops after 3) | `InstanceSave.cs:65-78`; `InstanceWriteQueue.cs:28-47,99-102`; `docs/integration/instances.md:121-138` | |
| Group binds | M, because groups are not persisted | `InstanceManager.cs:48`; `instances.md:144-146,245` | After a restart only character binds remain |
| Instance id counter | P, local; starts at 101, raised past stored ids at load | `InstanceManager.cs:34,53,114,689` | |
| Per-player instance state (homebind timer, left-dungeon map) | M | `InstanceManager.cs:50,1296-1303` | |
| Instance contents: creature deaths, respawn timers, boss state | M. "A re-created map respawns everything" | `instances.md:246-248` | Lane `ac-4` (durable loot) is working near this |
| Map registry | `WorldRuntime._maps` keyed by `(MapId, InstanceId)`; each instance has its own `Map` | `WorldRuntime.cs:20,191-201`; `InstanceManager.cs:28` | This replaces the removed `InstanceRegistry` (`instances.md:164-183`) |

### Teleport (including far transfer)

| State | Class | Evidence | Note |
|---|---|---|---|
| Pending teleport: stage, destination, origin, source map | M | `TeleportService.cs:16-29,49,225-256` | Holds a direct source `Map` reference (`BeginTransit`, `:248`); no deadline journal |
| Position during a far teleport | D. Before the ack the player's map and position are already the destination, so a save in transit stores it | `TeleportService.cs:250-254` | |

### Loot and world objects

| State | Class | Evidence | Note |
|---|---|---|---|
| Corpse loot bags, per-player copies, allowed looters, window viewers | M | `LootService.cs:43-44`; `LootModel.cs:74,80,109,118` | `GameObjectLootFeature.cs:127-133` releases on logout |
| Game object and creature respawn timers | M. "Game object respawn times are not persisted"; creature respawn is an in-process dictionary | `gameobjects-loot.md:133-135`; `CreatureMapSystem.cs:53`; `GameObjectMapSystem.cs:34` | Lane `ac-4` |
| Item container loot | Not persisted, so containers do not open | `gameobjects-loot.md:135` | |
| Quest-flag and creature visibility caches | R | `GameObjectMapSystem.cs:37`; `CreatureMapSystem.cs:55` | |

### Economy

| State | Class | Evidence | Note |
|---|---|---|---|
| Mail, mail items, money in letters | D. Escrowed items stay in `item_instance` with owner 0 and one reference | `EconomyFeature.cs:21-28` | |
| Mailbox cache of an online character | R, dropped at logout | `EconomyFeature.Mail.cs:25`; `EconomyFeature.cs:275-279` | |
| Auctions | D. All loaded at startup into one in-memory map | `EconomyFeature.cs:93-101`; `EconomyFeature.Auction.cs:17` | P: auction cache and expiry sweep belong to one process |
| Mail, auction and item-text id counters | P, seeded from stored maxima at attach | `EconomyFeature.cs:89-93` | Lane economy is editing these |
| Open trade (items, money, accept state) | M. Cancelled at logout | `EconomyFeature.Trade.cs:15`; `EconomyFeature.cs:277` | A handoff must cancel or refuse |
| Pending economy settlement | Transactional; the barrier waits on login | `EconomyFeature.cs:130-131`; `CharacterHandlers.cs:203-206` | |

## 3. What a transfer snapshot would have to add

Everything marked **M** above that a player would notice losing, in rough order of
player impact. This is a list for M15.3 planning, not a design.

1. Current health/power, death state, ghost and corpse (also needed for a plain relog).
2. Auras, cooldowns and any cast, with a clock that is valid on the receiving process.
   Saving them only at logout (`SpellFeature.cs:319`) is a durability gap on its own.
3. XP within the level and the rested pool (needs a schema column first).
4. Group membership and group-bound instance locks (groups are not durable).
5. Combat/threat relations, which hold object references and must be detached and
   re-created, not copied.
6. Open trade, loot window, gossip, taxi flight: cancel or refuse rather than carry.
7. Item buyback and bank bag slot purchases (currently discarded or refused).

## 4. Process-wide authorities a second process would not share

These are not per-player but decide whether two `World` processes can coexist at all.
One in-memory copy each at `c3dea16`:

- Session registry (`SessionRegistry.cs:12`), online registry (`WorldRuntime.cs:22`),
  character name directory (`CharacterDirectory.cs:14`).
- Spellbook cache of all characters (`SpellbookCache.cs:20`).
- Guilds (`GuildManager.cs:31`), groups (`GroupManager.cs:17`), channels
  (`ChannelManager.cs:14`).
- Auctions and mail/auction/text id seeds (`EconomyFeature.cs:89-101`).
- Id counters: item GUIDs (`ItemTemplateStore.cs:49`), guild ids (`GuildManager.cs:34`),
  group ids (`GroupManager.cs:20`), instance ids (`InstanceManager.cs:53`). Character ids
  are database auto-increment (`CharacterDbContext.cs:82`) and are not in this list.
- Schema initialization runs in both `Realm` and `World` at startup
  (`src/ArcaneCore.Realm/Program.cs:19`, `src/ArcaneCore.World/Program.cs:19`), with no
  lock or validate-only mode (`src/ArcaneCore.Data/Schema/SchemaBootstrapper.cs:78-120`).

## 5. Not verified

- Behaviour under a real client, a second OS process, or MySQL (CI provides MariaDB
  10.11 and PostgreSQL 16 only, `.github/workflows/ci.yml:15-35`).
- Whether any path other than the world's graceful shutdown raises `PlayerLoggingOut`
  for spell state. Host stop relies on sessions closing first (`WorldHost.cs:13-15`,
  `WorldServer.cs:59-73`, `WorldSession.cs:473-484`) and then `WorldRuntime.Stop` running
  queued removals before `SaveAll` (`WorldRuntime.cs:154-156`). A crash skips all of it.
- The contents of lanes in flight (see Basis).
