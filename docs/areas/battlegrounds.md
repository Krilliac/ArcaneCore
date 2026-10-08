# Area: Battlegrounds (framework, Warsong Gulch, Arathi Basin, Alterac Valley, the world lifecycle)

Branch `claude/vw5-battlegrounds` (wave 4, lane `battlegrounds`, based on `claude/vw4-integration` 7313b9e). Merge notes are in
[docs/integration/battlegrounds.md](../integration/battlegrounds.md). Directive: everything as close to retail 1.12.1 as possible,
mechanics and data, verified against the vmangos reference (`D:\refs\vmangos`, primary), mangos-classic, wow_messages and
classic-db. Nothing from those trees is copied: the code is a re-implementation, the tests carry hand-written wire bytes and a few
quoted constants, and every rule cites `file:line` in its doc comment.

## Delivered

All of it is pure, world-thread code in `src/ArcaneCore.Game/Battlegrounds` with an injected clock (time moves only through
`Update(diff)`; no test reads a wall clock). Every effect on a player, a game object, a spell, honor, reputation or a packet goes through
a narrow port (`BattlegroundPorts`) whose default is inert, so a lane that is not merged leaves its effect absent instead of faked.

| Slice | What it does | Reference |
|---|---|---|
| S1 `wsg-rules-core` | `Battleground` is the vmangos update machine: the start countdown (120/60/30/0 s, the one event per tick `else if` chain, doors opened at zero, status IN_PROGRESS, start sound 3439, players far from the start repop), doors removed once the start time exceeds 180 s (the start time counts the two minutes of preparation), the premature finish (a side below the minimum for the configured time ends the match in favour of the side still at the minimum; none when both are below; announced per minute and per 15 s), the end of the match (winner sound, WAIT_LEAVE with 120 s, everybody resurrected or out of combat and frozen, winner always gets the mark spell, the loser only after 10 minutes of start time, WIN/LOSE + frozen final scoreboard + the IN_PROGRESS status with the leave timer), removal two minutes later without "left" messages, invited counts, free slots, the empty-battleground deletion and the 2 minute queue nudge, kill credit (killing blow, honorable kill for the killer and the teammates within reward distance, death except in Spirit of Redemption), the scoreboard (at most 80 rows in guid order, rank 4 when unknown), the spawn-event table (`SpawnEvent` with the `std::map` zero-default quirk). `WarsongGulch` is the flag game: the four flag states, taking the flag from the base or the ground within 10 yd, the own team returning a dropped flag (a return score), captures through trigger 3646/3647 only when the capturer's own flag is on base and he is the carrier, `ForceFlagAreaTrigger` (a carrier already in the base captures when his flag returns), drops on death, leaving, aura removal and an offline carrier clearing the picker and respawning the flag, the 23 s respawn and 10 s drop timers (both fire on the first tick strictly past the limit, `timer < 0`), three captures win, honor per bracket (capture 48/82/136/226/378/396, win 24/41/68/113/189/198, weekend arrays gated on the calendar port), reputation 35 (45 on a weekend) with 889/890, the initial world states with the literal 1/2 icon values, graveyards 769/770 before the start and 771/772 after, exits 3669/3671, the power-up triggers. | `BattleGround.cpp:290-464, 651-769, 910-1000, 1254-1282, 1759-1790`; `BattleGroundWS.cpp:49-723`; `BattleGroundWS.h:27-114`; `BattleGroundDefines.h` |
| S2 `bg-wire-packets` | `BattlegroundPackets`: byte-exact builders for SMSG_BATTLEFIELD_STATUS (empty form, WAIT_QUEUE, WAIT_JOIN with one time, IN_PROGRESS), SMSG_BATTLEFIELD_LIST, WIN/LOSE (empty), MSG_PVP_LOG_DATA, MSG_BATTLEGROUND_PLAYER_POSITIONS, PLAYER_JOINED/LEFT, GROUP_JOINED, PLAY_SOUND, UPDATE_WORLD_STATE, AREA_SPIRIT_HEALER_TIME and strict parsers (wrong size refused) for CMSG_BATTLEMASTER_JOIN, BATTLEFIELD_PORT, the single-map packets and the guid packets. | `Server/Packets/Battleground.cpp`; `BattleGroundHandler.cpp:266-326`; wow_messages 1.12 |
| S4 `bg-queue-manager` | `BattlegroundManager` + `BattlegroundQueue`: templates (map and type tables), per-type queues with premade and normal lists per bracket, the selection pools (`AddGroup`, `KickGroup`), `FillPlayersToBg`, `CheckNormalMatch` (balanced top-up and the "more than two apart" refusal), `CheckPremadeMatch`, creation of a match per queue run, the average wait of the last ten invited, invitations with the 60 s reminder and the 80 s lapse, offline players dropped after a minute, solo and group join with `Group::CanJoinBattleGroundQueue` (offline, faction, bracket exclusion, already queued, deserter, no free slot, tag rule, too many), the group size limit queueing individuals, the port (enter with deserter, level and ended-match refusals, leave queue), logout/login persistence of the queue place, the status poll, client-visible instance ids (lowest free from 1). | `BattleGroundMgr.cpp:49-975, 1217-1279, 1514-1530, 1731-1797`; `BattleGroundHandler.cpp:88-264, 361-576`; `Group.cpp:2077-2122`; `Player.cpp:19405-19468, 21927-21933` |

The options (`BattlegroundOptions`, section `Battleground`) default to retail and clamp like vmangos: `CastDeserter` on, premature
finish 300000 ms (0 = off), `InvitationType` 1, premade wait 0, premade minimum group 6, `QueuesCount` 0 meaning 3 (client patch 1.9+),
`TagInBattlegrounds` on, `GroupQueueLimit` 40. The server binding of the section and the `CastDeserter` use belong to the lifecycle slice
(not delivered, below).

## Wave 2 (lane `battlegrounds`, branch `claude/w2-battlegrounds`)

The battlegrounds are live in the world daemon: queue at a battlemaster, port into a per-match map instance, play, leave.

| Slice | What it does | Reference |
|---|---|---|
| Arathi Basin (`ArathiBasin`) | Five nodes clicked through their banner (`EventPlayerClickedOnFlag`, event1 = node): a neutral node is claimed, an enemy node assaulted, an own node the enemy contests defended; a contested node is occupied after 60 s; occupied nodes tick 10/10/10/10/30 resources every 12/9/6/3/1 s (the tick fires once the accumulated time is strictly above the interval); 2000 wins; the near-victory text once above 1800; honor per 330 resources (200 on a weekend) and 10 reputation per 200 (150) with 509/510; the four/five-base quest spells 24061/24064; the banner events `(node, status)` with the 1 s / 5 s banner delay; node credits 15001-15005; node graveyards by squared 2D distance (entrance 890/889 before the start, start graveyards 898/899 without a node); the initial world states (icons, node states with the `{0,2,3,0,1}` offsets, occupied bases, resources, the unknown `0x745 = 2`); the 15 buff objects of the match, re-rolled to a random type when one is taken (180 s); exits 3948/3949. | `BattleGroundAB.cpp`, `BattleGroundAB.h` |
| Alterac Valley (`AlteracValley`) | Seven graveyards and eight towers/bunkers with Alliance, Horde or neutral (Snowfall) owners; assault and defend through the banner's `(node, owner * 2 + state)` event; a node assaulted for 5 minutes is taken (graveyard) or destroyed (tower: -75 reinforcements to its old owner, reputation and bonus honor to the destroyer); 600 reinforcements, -1 per death (not in Spirit of Redemption), -100 for a captain (once), +1 per 45 s per owned mine, a mine reclaimed by the neutral team after 20 minutes; the captains' buffs (22751/23693) every 2-6 minutes; the general's death ends the match (vmangos removed the reinforcement-zero win, BattleGroundAV.cpp:782-787); the end bonuses for surviving towers, owned graveyards (the vmangos loop also counts the towers), owned mines and a living captain, the weekend 1584/396; the cave or the nearest controlled graveyard; the world states (nodes, mines, scores, the score display only while running); exits 2608/2606; no premature finish. | `BattleGroundAV.cpp`, `BattleGroundAV.h` |
| Base objectives | `FlagCarrierShownTo` (the MSG_BATTLEGROUND_PLAYER_POSITIONS carrier: the viewer's own team's carrier, vmangos sends an Alliance viewer `GetHordeFlagPickerGuid`), `EventPlayerDroppedFlag` and `EventPlayerClickedOnFlag` as the single entries every trigger reaches, `HandleKillUnit`, `HandleTriggerBuff` and `BuffChange`, the position-aware `ClosestGraveyard`, `BonusHonorFromKill` (`GetHonorGain(max, max, rank 1)`), `HonorModifier` (`60^(hours-1)` under an hour), `CastSpellOnTeam`. The factory builds all three types. | `BattleGround.cpp:546-564, 771-783, 1713-1757`; `BattleGroundHandler.cpp:266-326` |
| World tables (world schema 40) | `battleground_template`, `creature_battleground`, `gameobject_battleground` and `battlemaster_entry` (classic-db layout; vmangos patch rows and mark spells are read too) through `IBattlegroundContentStore`, imported by the content CLI. With an empty template table the classic-db rows are built in; the marks default to the 1.12 marks of honor (`BattleGroundMarks`). | `BattleGroundMgr.cpp:1332-1399, 1631-1729` |
| Lifecycle (`ArcaneCore.World/Battlegrounds`) | `BattlegroundFeature` runs the manager on the world tick, registers the templates (start locations from WorldSafeLocs), creates one map instance per match (ids from the dungeon counter, `InstanceManager.AllocateInstanceId`), resolves battleground maps (`InstanceManager.BattlegroundMaps`), lets only a match's players onto its map (`TeleportService.BattlegroundEntryAllowed`), adds an arriving player to its match and sends the initial world states, makes a far teleport out of a match leave it, removes a participant who logs out and moves a login on a battleground map to the entry point (or the bind point). `MatchRuntime` is a match's `IBattlegroundHost`: battleground system messages (broadcast texts; `mangos_string` English rows), sounds, world states, statuses, the end packets, resurrections, client control, the 100 yd return, doors (event 254: opened, then removed), the event spawn gate (an `IWrappingSpawnGate` in front of the game-event gate, with the 1 s / 5 s banner delays), the flag stands and dropped flags, the AB/AV banners (opening a button with `noDamageImmune`), the dropped flag summon, the match objects, the buff traps, the kills, the herald yells and the quest credits. `BattlegroundHandlers`: battlemaster hello and join (a battlemaster in reach, or the portal within 50 yd of the entry point), battlefield list and port, leave (not in combat in a running match), the scoreboard and the map positions; CMSG_BATTLEFIELD_STATUS answers through the feature. | `BattleGroundHandler.cpp`; `BattleGroundMgr.cpp:1432-1473`; `Player.cpp:1861-1864, 2036-2043, 14775-14788, 18675-18702` |
| Flag drops on every trigger | The flag aura's removal (any cause: a right click, a positive school immunity on a player who is not charmed or an unattackable aura on any player, which strip the auras carrying `AURA_INTERRUPT_INVULNERABILITY_BUFF_CANCELS`, mounting through the aura's own mount flag), death, leaving and a far teleport all reach `EventPlayerDroppedFlag`; the aura's removal does not while the carrier is possessed (a live MOD_POSSESS aura). An accepted summon would too, but this server has no summon acceptance. | `SpellAuras.cpp:4066-4083, 4112-4121, 5689-5695`; `Player.cpp:19665-19669` |
| Buff traps | A spawned trap whose radius (data2) is 0 and cooldown (data5) 3 is a battleground buff: a living player within 3 yd gets its spell (data3) and the trap cools down 3 s; then, only when that player is in the match (`InBattleGround`), `HandleTriggerBuff`: a database buff (Warsong Gulch) is despawned and respawns on its spawn timer, an Arathi Basin buff re-rolls. A game master or anyone else on the map gets the buff but leaves the object. | `GameObject.cpp:476-552` |
| Spell 2584 | Releasing the spirit as a participant casts Waiting to Resurrect before the ghost form (`IBattlegroundPresence.OnSpiritReleased`); leaving a battleground removes it. | `Player.cpp:4586-4589, 18681` |
| WorldDefense with honor disabled | The rank-15 speak gate applies only while an honor rank source is registered; with `World:Honor:Enabled` false the channel is open and carries rank 0. | `Channel.cpp:636-648` |
| Bot scenario | `WarsongGulchScenario` (`wsg`) and the harness extensions in `ScenarioBattlegrounds` (docs/areas/playbots.md). | |

Data a live realm needs: the world import of the four tables above (classic-db z2815 has 3 templates, 24 battlemasters and the event rows),
`WorldSafeLocs.dbc` (the start locations and graveyards), `AreaTrigger.dbc` rows 3646/3647 and the exits, the map rows 30/489/529 and the
battleground game objects and creatures. Without the event rows nothing is gated: flags and banners stand from the start and doors stay shut.

## Deviations from vmangos (each is deliberate and small)

- **SMSG_BATTLEFIELD_STATUS carries the status as a u32.** vmangos (`Battleground.cpp:95`) and mangos-classic
  (`BattleGroundMgr.cpp:103`) both write `uint32(statusId)`; `smsg_battlefield_status.wowm` (1.12) types it u8 (and its size range
  8..22 only fits a u8). Two servers that ran live 1.12 clients agree, so the packet follows them. Open question below.
- **A match with no winner announces no winner.** vmangos `GetWinnerText` returns the Alliance text for `TEAM_NONE`, so a premature
  finish with both sides below the minimum says "The Alliance wins". Nothing is announced here (the sound is also silent, as in vmangos).
- **Kill credit compares the match teams, not faction templates** (`BattleGround.cpp:1767`); the results differ only for a mind-controlled player.
- **The dropped-flag guid is cleared when the flag is picked up or returned** (vmangos leaves the stale guid until the next drop
  overwrites it), so a later respawn never tries to delete an object that is already gone.
- **Flag world states follow every return.** A player return and an offline carrier's respawn also write the flag's taken state 0 (vmangos
  only does it on the timeout return, `BattleGroundWS.cpp:157`; mangos-classic resets it on a return); the offline path also puts the
  carrier team's icon back to 1. A drop after the end leaves the flag's state at `OnBase` instead of `OnPlayer` with no carrier (nothing is sent).
- **The flag rules do not lean on the object layer.** A base flag is only taken while its stand is spawned (vmangos relies on the client being
  unable to use a despawned stand during the 23 s respawn), and `OnPlayerCapturedFlag` checks what the base trigger checks (the source carries
  the enemy flag, its own flag is home), since vmangos reaches `EventPlayerCapturedFlag` only from that trigger.
- **A late joiner into an ended match gets the frozen final board** (`FinalScore`), the one MSG_PVP_LOG_DATA answers during WAIT_LEAVE
  (`BattleGroundHandler.cpp:341`); vmangos rebuilds it (`BattleGround.cpp:1818`), which drops the players who left after the end.
- **The bracket clamp is `>= 5`** where vmangos tests `> 6` (`Player.cpp:19464`), which leaves bracket 6 for levels 70-79; identical for every
  level a battleground accepts.
- **The premade wait does not wrap** (`getMSTime() - wait` is unsigned in vmangos and would move a premade group at once during the first
  `wait` ms of uptime); only reachable with a non-zero `PremadeGroupWaitForMatchMs`.
- **The shipped config default is used for `InvitationType`.** `World.cpp:787` reads 0 when the key is missing, `mangosd.conf.dist.in:2932`
  ships 1 (the balanced, retail behaviour). Default here: 1.
- `Battleground.UpdatePlayerScore` and the reward ports are keyed by guid; vmangos scans `sObjectMgr.GetPlayer` per call.
- **The buff a trap gives is cast by the player on itself.** vmangos casts it from the game object; the spell system cannot cast from an object,
  and for a self-only aura the result differs only in the aura's caster guid.
- **Opening an Arathi Basin or Alterac Valley banner also runs the button's own activation** (its state flips) before the battleground hears of
  it; vmangos returns from the open-lock effect first. CMSG_GAMEOBJ_USE on a locked banner is refused by the lock as before; the click reaches the
  battleground through the opening spell. There is no cast-time check of `CanUseBattleGroundObject` in the spell's cast checks.
- **A participant who logs out is removed at once** (offline: nothing of it is touched); vmangos keeps it for `MAX_OFFLINE_TIME` and lets it
  rejoin. Its next login is at the entry point (the memory, else the `character_battleground_data` row), as vmangos does once the match is gone.
- **WorldDefense is open while honor is disabled** (above): vmangos cannot turn honor off.
- **The `mangos_string` texts are the English rows** of cmangos-classic `mangos.sql`; a broadcast text missing from `broadcast_text` falls back
  to a recorded English line.
- **The dropped flag is placed by the match**, not by the spell (23334/23336): the spell system has no SUMMON_OBJECT_WILD effect.

## Limits (documented, not hidden)

Not delivered, and what each needs. Nothing below is stubbed inside the delivered scope; the code that depends on it is behind a port with an
inert default.

- **Slice S3 (content tables)**, delivered in wave 2 (world schema 40). Not imported: `battleground_events` (descriptions only) and
  `areatrigger_bg_entrance` (the portal join, which vmangos refuses anyway). The flag room and exit triggers are `AreaTrigger.dbc` rows.
- **Slice S5 (instances, entry points, persistence)**, delivered in wave 2: `character_battleground_data` (characters schema 39; guid,
  instance, team, join position and map, Player.cpp:20950-20982) is written when a participant enters its match and removed when it leaves
  online or logs in again; a login on a battleground map reads it after the memory (Player.cpp:14775-14788).
  `TeleportCommands.cs:130` still refuses battleground maps to GMs (vmangos does too, Player.cpp:1863).
- **Slice S6 (the daemon lifecycle)**, delivered in wave 2. Not delivered: the battlemaster gossip option 12 (the hello opcode is answered),
  the area spirit healer opcodes 738-740 and the dropped flag's pickup spell effects 23383/23384 (the flag drop object's use is handled directly).
- **Slice S7 (BG raid groups).** `Group.MinMemberCount` is a static 2, `GroupType` has no battleground raid; the per-team raid group, the
  original-group restore and the "do not send teammates when the player's group is the BG raid" rule of MSG_BATTLEGROUND_PLAYER_POSITIONS are
  not modelled (the builder always takes the teammate list the host passes).
- **Spawn gating and doors**, delivered in wave 2: a battleground map's creature and game object systems ask the match whether every event of a
  spawn is active (`MatchRuntime` as the spawn gate). A creature of an event that turns off is removed at once; vmangos stops only its respawn
  (`RESPAWN_STOP`) when the despawn is not forced.
- **Auras and spells.** The flag auras 23333/23335 and the dropped flag 23334/23336 are cast through `IBattlegroundSpellPort`; the carrier drops the
  flag on aura removal only when the aura engine reports it (the `aura-engine-completeness` lane). The mark spells and the deserter debuff go
  through the same port. Honor (`IBattlegroundHonorSink`, `IHonorRankSource`), reputation (`IBattlegroundReputationSink`) and the weekend
  (`IBattlegroundCalendar`) map to the honor, reputation and game-events lanes; until they merge the scoreboard honor stays 0 and the rank
  shows 4.
- **Alterac Valley turn-ins and upgrades** (`AlteracValley.Upgrades.cs`, BattleGroundAV.cpp:58-275, 433-772; the collector and quartermaster
  scripts of battleground_alterac.cpp): a quest a participant is rewarded by a creature of the match (`QuestNpcServices.QuestRewarded`) is counted
  as vmangos HandleQuestComplete does: armor scraps (20 a turn-in; supply crate events 80-87 at 100-400 of each 500, cleared at 500; a line every
  hundred), the commander, boss, mine and stable quests (their reputation, the yells at 90/60/30 turn-ins and at the 200th offering, the tamed
  mounts of events 90-97 and the stable lines). The collectors' counters (`CollectorQuestComplete`: blood or crystals, hides, flesh or medals, mine
  supplies by the quest's first required item, tamed mounts by quest) fill the air, cavalry, ground and world-boss challenges with vmangos' goals,
  minimum reputations, timers, go flags and resets; a completed world-boss offering resets and sets the go flag. The match's part runs only while
  it is in progress (BattleGroundAV.cpp:505-506); the collectors' part, like vmangos' QuestComplete_npc_AVBlood_collector, only needs a
  participant, so it also counts before the start and after the end. Murgot Deepforge and Regzar get
  the quartermaster's gossip through `QuestNpcServices.GossipScript` (the quest list, "next upgrade" with the npc text by how close the scraps
  are, and the upgrade for a player honored with either faction); `UpgradeArmor` buys seasoned, veteran or champion troops at 500, 1000 or 1500
  scraps (the team spell 28418-28420, the quartermaster's lines, and at exactly those amounts every node the team controls repopulated), with the
  reference's quirk kept: below the threshold it resets the level to basic. After an upgrade choice the gossip window stays open, as in vmangos
  (GossipSelect_npc_AVBlood_collector sends no CLOSE_GOSSIP_MENU for Murgot and Regzar and nothing after UpgradeArmor,
  battleground_alterac.cpp:3370-3372, 3642-3653). The defender events of a captured, defended or repopulated node use
  the owner's scrap level (`DefenderType`), and `SetSpawnEventMode` is applied as vmangos does (RESPAWN_FORCED for the node's new defenders,
  RESPAWN_STOP for those of an assaulted node, the killed commanders and explosives experts; the world applies it only to creatures whose events
  are all active, respectively not all active, as BattleGround::SetSpawnEventMode does).
- **Alterac Valley landmines, shredders and yells**: a landmine layer's death sets its event to 1 without spawning (the mines stop coming back),
  an expert's death removes the event's mines; the world runs vmangos go_av_landmineAI on 179324/179325 (`MatchRuntime.AlteracValley.cs`, a
  `GameObjectMapSystem` object script): a mine only fires for a participant of the other team, then despawns, and while its layer is dead it
  keeps putting its respawn off. vmangos asks `me->IsHostileTo(user)` instead; the user is always a player there (an environmental trap only
  searches players, GameObject.cpp:520-529, as here), and classic-db's 179324/179325 rows have faction 0, for which GameObject::IsHostileTo
  answers hostile to everyone (GameObject.cpp:2092-2094): a literal port would let a mine go off under its own team, so the team rule stands
  in for the faction the reference's data would need. A dead layer's creatures and a RESPAWN_STOP defender stay dead for the match only: a
  battleground map writes no `creature_respawn` rows (vmangos MapPersistentStateMgr.cpp:84-86; docs/areas/creature-movement-spawns.md). `CheckSpellCast` (asked through a cast check after the range check) refuses a shredder summon (21544/21565)
  with SPELL_FAILED_SPELL_UNAVAILABLE while the team's last summoner still controls a shredder, else records the summoner; the summon and
  possess of the shredder itself are the spell system's (the AVCreateShredderScript fix-up is not ported). Snivvle yells 70 s in and the captains
  yell with their buffs (mangos_string 791-793; these rows are not in the string table here, so those yells are logged once and not sent).
  classic-db z2815 has row 790 but not 791-793, and vmangos' Language.h marks 791-799 as unused; the text has to come from a vmangos world
  database's mangos_string, which is not among the local references, so it is a content gap, not a code one.
- **Alterac Valley, not ported**: the assault invocations of the scripts (the escorted ground troops, beacons, war riders, cavalry and world
  bosses with their waypoints and AIs; their counters, goals and go flags are kept for them), the collectors' other gossip menus, and the
  start-time supply and tamed events (unreachable in vmangos itself). The AV queue minimum, initial maximum and randomization of vmangos are not
  ported; an AV group join is rejected as vmangos does.
- **Not ported from vmangos.** The queue announcer (`Battleground.QueueAnnouncer.*`), `BattleGround.RandomizeQueues`, the debug "testing"
  mode, the accurate-PvP reputation values of patches before 1.10, `BattleGround::HandleCommand`, the item reward by mail for a full bag
  (marks are cast spells here, as in vmangos for 1.12).
- **Resurrection.** The 30 s spirit-guide wave is not battleground code (vmangos `RESURRECTION_INTERVAL` is unused): it is the spirit-healer channel
  spell 22011, whose Spirit Heal effect (spell 22012, SPELL_EFFECT_SPIRIT_HEAL) resurrects the ghosts that wear Waiting to Resurrect (2584, now
  cast on release). The effect itself (`SpiritHealEffect`, with the pet re-summon) is in the death area; the guide's script that channels 22011
  and the area spirit healer opcodes (738-740) are not modelled (`BuildAreaSpiritHealerTime` is only the packet): a ghost in a battleground runs
  back to its body (a corpse reclaim restores it fully) or waits for the match end. The released body of a player in a battleground carries
  CORPSE_FLAG_LOOTABLE (insignia).
- **Not delivered in wave 2.** The battleground raid group (slice S7), the BG chat channel (lane ops-social), the vmangos debug "testing"
  mode, the queue announcer, and the honor weekend calendar (the `IBattlegroundCalendar` port stays inert).

## Open questions

1. SMSG_BATTLEFIELD_STATUS status width (u32 here, u8 in wow_messages). Resolve with a real 1.12.1 client run of the status packet.
2. `InvitationType` default 1 (shipped config) versus 0 (code default) is a judgement call; both are supported.

## Tests

`tests/ArcaneCore.Game.Tests/Battlegrounds`: `WarsongGulchTests` (flag rules, timers, honor, reputation, world states, exits), `WarsongGulchPropertyTests`
(60 seeded random games: captures never exceed 3, one winner who owns the third capture, a flag is carried exactly while it has a carrier),
`BattlegroundCoreTests`, `BattlegroundPacketTests` (hand-written wire bytes), `BattlegroundManagerTests` (queue, invitations in time, port, login),
`ArathiBasinTests`, `AlteracValleyTests` and `WarsongGulchCarrierTests` (wave 2). `tests/ArcaneCore.Game.Tests/Death/BattlegroundReleaseTests`
(the release hook runs before the ghost form), `Honor/WorldDefenseRankTests` and `Social/ChannelManagerTests` (the gate with and without honor).
`tests/ArcaneCore.Data.Tests/Battlegrounds/BattlegroundDataTests` (both schema steps, both dump dialects, both stores on every available provider).
`tests/ArcaneCore.World.Tests/Playerbots/Scenarios/BattlegroundScenarioTests` (the `wsg` bot scenario) and `BattlegroundWorldScenarioTests` (buff
trap, Divine Shield drop, own-team return, death drop and kill credit, Waiting to Resurrect and the battleground graveyard, the initial world
states, leaving with Deserter, a far teleport out, a logout and login inside a match, the same after a restart from the stored row) and
`BattlegroundGuardScenarioTests` (a buff touched by a player outside the match stays; a possessed carrier keeps the flag through an immunity and
the loss of the flag aura) run two managed bots against the real handlers on the
manual clock with the synthetic content of `WarsongGulchTestContent`; `ArathiBasinWorldScenarioTests` plays an Arathi Basin node.
`AlteracValleyUpgradeTests` covers the turn-ins, upgrades, challenges, respawn modes, landmines, shredders and yells, and
`AlteracValleyWorldScenarioTests` (the `av-upgrades` scenario, `AlteracValleyTestContent`) plays them over the wire: an armor-scraps quest turned
in at Murgot through the quest opcodes, the quartermaster's gossip and the upgrade, a Horde landmine sparing the Horde bot and going off under the
Alliance bot, and the mine staying away after its layer died. `Creatures/EventRespawnModeTests` covers the forced and stopped respawns.
