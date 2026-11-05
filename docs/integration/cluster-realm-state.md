# Cluster realm-state survey (S3)

Read-only inventory of the realm-wide state that a second `World` process would have to share
or partition: battleground and LFG queues, world states, custom channels, the who list,
corpses and graveyards, group ids (including playerbot groups), outdoor PvP, and holiday and event timers.
It changes no code. It extends [cluster-transfer-state.md](cluster-transfer-state.md)
(the per-player view; its section 4 lists the process-wide authorities) and is the input to
the S3e lane list at the end.

## Basis and limits

- Read at `f09161c96` by static reading of source. Nothing was built or run for this document, so every
  entry is a reading of the code, not a compiler or test result. Line numbers drift; re-check an entry before
  building on it. Paths are relative to `src/`.
- "Holder rule" is who may run or mutate the state in a cluster today. Two rules exist in code: the realm
  authority lease (scopes `economy`, `social`, `instances`; `ArcaneCore.Kernel/Cluster/Social/RealmAuthorityLease.cs:4-14`)
  and the per-map owner (`ArcaneCore.World/Cluster/Transfer/ClusterMapPlacementTable.cs:137-140`, `:233-239`).
- Feature attachment by role is decided in `ArcaneCore.World/Cluster/Social/ClusterRoleFeatureFilter.cs`. Only
  `AutoBroadcastFeature` (social lease, `:88-103`) and `AuctionBotFeature` (economy lease, `:105-120`) are
  lease-gated; every other world feature, including the battleground, meeting stone (LFG), outdoor PvP, game event and
  war effort features, falls through to "attach on Combined and MapWorker" (`:129-132`). So on two map workers each one
  runs its own copy of every state below that is not stored. The leases a MapWorker holds are kept by
  `ArcaneCore.World/Cluster/Social/RealmAuthorityLeaseKeeper.cs:31` (it takes only `social` and `economy`, `:35`);
  the `instances` scope exists in the lease table (`ArcaneCore.Data/Characters/Cluster/RealmAuthorityLeaseDataModule.cs:33`).
- Arena queues do not exist: this is a 1.12.1 core and no arena code is present under `Game/Battlegrounds`. The brief's
  "BG/arena" row is therefore battlegrounds only.

Legend. **M** per-process memory, gone on exit or crash. **D** durable (reaches the characters or world database).
**S** realm singleton (one authority for the realm, implicit today because there is one process). **C** content or
static (loaded from the world database or DBC at start, identical on every process, not state).

## 1. Inventory

| Subsystem | Class | Where the state lives (file:line) | Holder rule today | Id counters |
|---|---|---|---|---|
| BG queues | M, S | Four `BattlegroundQueue` objects per manager (`ArcaneCore.Game/Battlegrounds/BattlegroundManager.cs:233,255-259`); per-queue player map (`ArcaneCore.Game/Battlegrounds/BattlegroundQueue.cs:145`); per-player slot array of `MaxQueuesPerPlayer` (`BattlegroundManager.cs:77`); invite and removal events in a priority heap (`BattlegroundManager.cs:242-245,733`). Nothing is stored. | None. The feature attaches on every MapWorker (`ClusterRoleFeatureFilter.cs:132`), so each worker queues its own players separately. A queue is realm-wide by nature, so it needs one holder. | `_eventSequence` (`BattlegroundManager.cs:245,733`), process-local ordering key, not an id |
| BG matches | M | Running matches by type and instance (`BattlegroundManager.cs:234`), free-slot list (`:235`), per-player state (`:237`); world side `_matches` (`ArcaneCore.World/Battlegrounds/BattlegroundFeature.cs:50`) and per-match runtime (`ArcaneCore.World/Battlegrounds/MatchRuntime.cs:32-55`) | Per-map owner: a match is a map instance, so the node that owns the instance runs it, and players in a match are reachable only there. | Client-visible instance id: lowest unused from 1 per (type, bracket) (`BattlegroundManager.cs:236,333-352`, vmangos `CreateClientVisibleInstanceId`). Map instance id: the instance allocator, with a per-process fallback starting at 1,000,000 when the instance feature is absent (`ArcaneCore.World/Battlegrounds/BattlegroundWorldHost.cs:31,53-56`). |
| BG return point | D | `character_battleground` entry point per player (`ArcaneCore.Data/Characters/Battlegrounds/CharacterBattlegroundDataModule.cs:66,75-76`); in-memory cache `_entryPoints` (`BattlegroundFeature.cs:54`) | Per character; written by the node the player is on. Survives a handoff because it is stored. | none |
| LFG / meeting stone queue | M, S | `LfgQueue` instance (`ArcaneCore.World/GameObjects/MeetingStoneFeature.cs:61`); player and group maps keyed by guid and group id (`ArcaneCore.Game/Lfg/LfgQueue.cs:84-87`). The "LFG coordinator" is this queue plus `SocialLfgGroups` (`MeetingStoneFeature.cs:74`), which forms parties through the local `GroupManager`. | None; attaches on every MapWorker like the BG feature. Needs one holder, and party formation must go through the group authority (see Group ids). | none of its own; party ids come from the group allocator |
| World states (client packet) | C, M | Registry of providers and default pairs (`ArcaneCore.Game/WorldState/States/WorldStates.cs:32-35`, `Build` at `:63`); one provider is the battleground (`ArcaneCore.World/Battlegrounds/BattlegroundSeams.cs:132-134`), war-effort and zone-script states are named at `WorldStates.cs:26-29`. The registry holds no values or counters of its own; each provider reads its own feature. | Follows its provider: the BG provider is the match's map owner; war effort and scourge are realm singletons (game events row below). | none |
| Weather and game time | M | Per-map zone weather (`ArcaneCore.Game/WorldState/Weather/MapWeather.cs:23,44-47`); game time is the system clock (`ArcaneCore.Game/WorldState/Time/IGameTime.cs:24`) | Per-map owner for weather. Two nodes rolling different weather for different maps is correct; a map moved between nodes restarts its rolls. Time needs no holder. | none |
| Custom channels | M, S | Channels by team and name (`ArcaneCore.Game/Channels/ChannelManager.cs:14`), per-player joined sets (`:15`), members per channel (`ArcaneCore.Game/Channels/Channel.cs:15`). Built-in channels use fixed ids (`ArcaneCore.Game/Channels/ChannelTypes.cs:105-109`). Nothing is stored, so a channel and its owner or password vanish with the process. | `social` lease, in intent: the scope is documented for "whisper/group/guild/channel routing" (`RealmAuthorityLease.cs:9`). The router interface carries channel messages and subscribe/unsubscribe (`ArcaneCore.World/Cluster/Social/ClusterSocialRouter.cs:73-99`), but only the in-memory implementation exists (`:112`) and nothing in `World` or `Realm` constructs it (references are in tests only). In production each process keeps its own `ChannelManager`. | none (channels are keyed by name) |
| Who list | M, S | `CmsgWho` walks the local online set (`ArcaneCore.World/Handlers/ChatHandlers.cs:51,627,675`; `_online` at `ArcaneCore.Game/Maps/WorldRuntime.cs:27-28,119`); the name directory is `ArcaneCore.World/Characters/CharacterDirectory.cs:14`. | `social` lease, in intent: `IRealmPresenceDirectory` already has `QueryWho` (`ArcaneCore.Kernel/Cluster/Social/RealmPresenceDirectory.cs:60-110`, in-memory implementation at `:111`), but the handler does not call it, so a cluster's who list shows only the local node. | none |
| Group ids | M, S (membership also D) | `_nextId = 1` and `AllocateGroupId` (`ArcaneCore.Game/Groups/GroupManager.cs:28,36-50`); live groups `_memberOf`, `_invitedTo` (`:23-24`); membership is stored. | Cluster ids come from a durable block allocator (`IdRangeScopes.Group`, `ArcaneCore.Kernel/Characters/IdRangeReservation.cs:11`, wired at `ArcaneCore.World/WorldHost.cs:358-362`), so two processes do not collide on ids. The live group objects are still per process; one mutation order is the `social` lease's job. | Group id: block allocator when configured, else `_nextId`. Guild id: same pattern (`ArcaneCore.Game/Guilds/GuildManager.cs:49,54-58`, scope `guild`). |
| Playerbot group ids | M | `PlayerbotGroupCoordinator._nextId = 1` (`ArcaneCore.Playerbots/Groups/PlayerbotGroupCoordinator.cs:232`), used at `:1122,2007` and `PlayerbotGroupCoordinator.Handoff.cs:184`. These are the bots' own planning groups, not server group ids; a hand-off links one to a server group by `ServerGroupId` (`PlayerbotGroupCoordinator.Handoff.cs:46-49`). | Per process by design; each coordinator owns its bots. The link to the server group id is the only realm-visible part, and a server group moved to another node loses it. | `_nextId` int, process-local, restarts at 1 |
| Corpses (world object) | M | `Corpse` world object while a ghost has a body, bones after an insignia (`ArcaneCore.Game/Combat/Corpse.cs:10,89,131`); GUID counter is the process-static `s_nextCounter` (`Corpse.cs:30`) | Per-map owner: the corpse is an object of one map. | `Corpse.s_nextCounter` (`Corpse.cs:30`, incremented at `:89,131`), process-static, restarts at 1: two nodes would mint the same corpse guid |
| Corpse record (ghost body) | D | `character_corpse`: one row per character with map, position, time, type, instance (`ArcaneCore.Data/Characters/Life/CharacterLifeDataModule.cs:78`, row at `:38-61`); loaded at login (`ArcaneCore.World/Characters/Life/CharacterLifeFeature.cs:49-54`) | Per character; follows the player. The record is the only part that crosses a handoff; the world object is rebuilt on the new node. | none (key is character id) |
| Graveyards | C, M | Catalog loaded from the world database (`ArcaneCore.Game/Graveyards/WorldGraveyards.cs:16,23,35`); chosen per death by the map owner, with a battleground override for players in a match (`ArcaneCore.World/Battlegrounds/BattlegroundSeams.cs:114-128`) | Content: identical on every node. The BG override follows the match's node. A ghost whose nearest graveyard is on a map another node owns needs the placement table to route it there. | none |
| Outdoor PvP | M, D (Silithyst maximum only) | Zone scripts per map and per-player presence (`ArcaneCore.World/OutdoorPvP/OutdoorPvPFeature.cs:33-35`); capture state lives in the script objects (`ArcaneCore.Game/OutdoorPvP/CapturePoint.cs`, `EasternPlaguelands.cs`, `Silithus.cs`); object ownership in `ArcaneCore.World/OutdoorPvP/OutdoorPvPWorldHost.cs:24`. Only the Silithyst maximum is stored, through a save queue (`OutdoorPvPFeature.cs:151-186`); capture progress and tower ownership are not. | Per-map owner: Silithus and Eastern Plaguelands are each one map, so the node that owns the map runs the script. Correct only if the placement table assigns each of those maps to exactly one node. | none |
| Game events (holidays) | D, M | The running set is the `game_event_status` table (`ArcaneCore.Data/Characters/WorldState/GameEventStatusDataModule.cs:25`, replace-all write at `:51`), written by `GameEventStatusWriter` (`ArcaneCore.World/WorldState/GameEventFeature.cs:294-297`). The state machine (`ArcaneCore.Game/WorldState/Events/GameEventService.cs:19-26`) is rebuilt from definitions and the status table at start; handlers and next-due times are in memory. | Realm singleton, undeclared: every process starts its own `GameEventService`, and the status write deletes every row and inserts, so two processes would overwrite each other with their own running sets and each would apply spawn and quest changes on its own maps. Needs one holder, or a clock-determined schedule on every node plus one writer. | none |
| Event timers and world progress (war effort, scourge invasion, Silithyst, Nightmare dragons, elemental invasion, fishing contest) | D, M | Tables `world_war_effort_phase`, `world_war_effort_counter` (`ArcaneCore.Data/Characters/WorldState/WarEffortDataModule.cs:28-29`), `world_scourge_invasion_state`, `world_scourge_invasion_zone` (`ScourgeInvasionDataModule.cs:35-36`), and the Silithyst, Nightmare dragons, elemental invasion and fishing contest modules in the same folder. Features in `ArcaneCore.World/WorldState/` (`WarEffortFeature.cs`, `ScourgeInvasionFeature.cs`, `NightmareDragonsFeature.cs`, `FishingExtravaganzaFeature.cs`) write through `StateSaveQueue.cs`. Working copies, counters and timers are in memory. | Realm singleton, undeclared: each feature loads the shared tables at start but keeps its working copy in memory and writes it back, so the last writer wins and the in-memory copies diverge. | none |
| Exploration | D | `ExploredZonesDataModule`; `ArcaneCore.World/WorldState/ExploredZonesWriteQueue.cs` | Per character; follows the player (stored). | none |

## 2. What the survey shows

- **Nothing in this list is lease-gated.** Only AutoBroadcast and the auction bot are (`ClusterRoleFeatureFilter.cs:88-120`).
  Every row marked S would run once per MapWorker, each with its own copy.
- **The state splits three ways.** (a) Stored and per character (corpse record, BG return point, exploration): safe across
  nodes. (b) Per-map and rebuilt from content (graveyards, weather, outdoor PvP scripts, match state): needs the per-map
  owner to be unique and nothing else. (c) Realm singletons kept only in memory or written as whole state (BG queues, LFG
  queue, channels, who, live groups, game events, war effort, scourge): each needs a holder.
- **Id counters that are not cluster-safe today**: `Corpse.s_nextCounter` (`ArcaneCore.Game/Combat/Corpse.cs:30`); the BG
  client-visible instance id, which is per manager and per (type, bracket), so two nodes would each hand out id 1 to different
  matches of one bracket (`BattlegroundManager.cs:333-352`); the BG fallback instance id (only when the instance feature is
  absent, `BattlegroundWorldHost.cs:31`); the playerbot `_nextId` (process-local planning ids, harmless unless shown or
  stored). Group, guild, instance, item, mail, auction and item-text ids already use durable block allocators
  (`ArcaneCore.World/WorldHost.cs:340-380`, scope names at `IdRangeReservation.cs:10-16`).
- **The social router and presence directory are contracts without a production holder.** Both have in-memory
  implementations (`RealmPresenceDirectory.cs:111`, `ClusterSocialRouter.cs:112`) referenced only by tests. S3e must not
  assume they are wired.

## 3. Not verified

- Instances lease: a MapWorker takes the `instances` scope (`RealmAuthorityLeaseKeeper.cs`) and it gates only the realm-wide
  durable writes of the instance reset schedule; the lease table and the `/cluster` command know the same three scopes
  (`RealmAuthorityLeaseDataModule.cs:33`, `ArcaneCore.World/Cluster/Tooling/ClusterCommands.cs:897`). See `docs/areas/instances.md`.
- Which world-state providers beyond the BG one are registered at runtime (named in `WorldStates.cs:26-29`, not traced).
- Behaviour with two live processes: nothing was run.
- Playerbot realm state other than group ids.

## 4. S3e lane list (one lane per feature)

Order is by dependency: the holder scopes and id counters first, then the features that use them. Each lane is one
feature with its own tests and touches the lease filter only through lane 2.

1. **s3e-corpse-guid**: make the corpse GUID counter cluster-safe (`ArcaneCore.Game/Combat/Corpse.cs:30,89,131`) by deriving it
   from a per-node range or the instance allocator instead of a process-static; test that two allocators never repeat. Size S.
2. **s3e-holder-scopes**: add lease scopes for the singletons that need one (BG queues, LFG, game events and world
   progress) in `RealmAuthorityScopes` and the lease table, gate the features in `ClusterRoleFeatureFilter.cs`, and make the
   keeper take them. Tests next to `ClusterSocialAndEconomyAuthorityTests`. Size M. Lanes 3, 4, 8 and 9 depend on it.
3. **s3e-game-events**: one holder for `GameEventService` (`ArcaneCore.World/WorldState/GameEventFeature.cs`); the status write
   becomes holder-only and other nodes read the running set from the table or the holder. Size M.
4. **s3e-world-progress**: war effort and scourge invasion first, then Nightmare dragons, fishing contest, Silithyst and
   elemental invasion, one lane per feature, same pattern as game events. Size M each.
5. **s3e-who**: route `CmsgWho` through `IRealmPresenceDirectory.QueryWho` (`ArcaneCore.World/Handlers/ChatHandlers.cs:627`),
   wire a presence implementation on the `social` holder, keep the local walk as the single-process fallback. Size M.
6. **s3e-channels**: custom channels through the social router (`ArcaneCore.Game/Channels/ChannelManager.cs`), membership and
   owner on the `social` holder, built-in channels unchanged. Depends on lane 5's presence implementation. Size L.
7. **s3e-groups-live**: live group objects (`ArcaneCore.Game/Groups/GroupManager.cs`) mutated on the `social` holder; map workers
   keep a versioned membership copy for combat and loot, as the design says (`docs/CLUSTERING_DESIGN.md:191`). Playerbot
   `ServerGroupId` links follow it. Size L.
8. **s3e-bg-queue**: one holder for `BattlegroundQueue` (`ArcaneCore.Game/Battlegrounds/BattlegroundQueue.cs`), match creation on
   the node that owns the new instance, client-visible instance ids allocated by the holder (`BattlegroundManager.cs:333-352`),
   invite and status messages routed to the player's node. Size L.
   Delivered as the battleground hub (`BattlegroundHub`, `ClusterBattlegroundLink`, presence ops 25 to 28). The node holding the Social lease runs only a mailbox;
   the queue's holder is a node the gateway sends battleground transfers to: it owns everything or its `OwnedMaps` list names all three battleground maps (instance-copy ownership alone does not count, because the transfer names instance 0), elected at the hub by asking for the commands:
   the first asker holds the role while it asks at least every 5 seconds, except that a node whose list names all three maps (the gateway's first choice) takes the role from a node that merely owns everything, and a node that merely owns everything does not claim it while a listing node has asked within the last 5 seconds. Every node sends its players' queue commands to the hub and drains its notices from it; an
   accepted invitation is a far transfer to the holder's node. Limits: the hub and the holder's manager live in memory, so a Social lease move or a holder change loses
   the waiting commands and the queue state (players queue again); a cluster with no node that owns everything or lists all three battleground maps has no holder, and queued commands
   wait at the hub (bounded) with one warning in the log; a holder cut off from the hub steps down after the 5 second window, and its notices are dropped. A holder runs its own players' commands without the hub, a worker keeps its Login and Logout commands for a resend while the hub is unreachable, and a worker the hub has not heard from for 30 seconds is reported to the holder, which logs out every player it served for it (a crashed worker leaves no queue slot behind unless it restarts under the same node id within 30 seconds; the hub refreshes every worker's stamp when it has seen no call at all for 30 seconds, so a hub stall or a Social lease that left and came back never reports live workers gone). A live worker cut off from the hub for over 30 seconds is treated like a crashed one: its players are dequeued with no status sent to the client, nothing re-sends their Login after the reconnect, and they queue again by hand. A plain worker's battlemaster list is deliberately empty (the client joins with instance 0, meaning the first free one): the holder's client instance ids are not delivered to workers, so players on a plain worker see no running matches in the list. Playerbots queue for battlegrounds only on the node that holds the queue state (the elected holder), because a plain worker's manager keeps none.
9. **s3e-lfg**: the meeting stone queue on its holder (`ArcaneCore.World/GameObjects/MeetingStoneFeature.cs:61`), party formation
   through the group authority from lane 7. Size M.
10. **s3e-outdoor-pvp-owner**: assert and test that Silithus and Eastern Plaguelands each have exactly one owner in the placement
    table; a player handed in a capture zone re-registers presence (`OutdoorPvPFeature.cs:34`); store tower ownership if it
    must survive a map move. Size M.
11. **s3e-playerbot-group-ids**: make the planning ids explicitly per process (node-qualified) so logs and stored links cannot
    clash; no behaviour change. Size S.
12. **s3e-graveyard-ghost-route**: a ghost released on one node whose graveyard map belongs to another is handed there with its
    corpse record (`character_corpse` already stores map and instance). Size M. Depends on the placement table.
