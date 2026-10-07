# Area: Battlegrounds (framework and Warsong Gulch)

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

## Limits (documented, not hidden)

Not delivered, and what each needs. Nothing below is stubbed inside the delivered scope; the code that depends on it is behind a port with an
inert default.

- **Slice S3 (content tables).** `battleground_template` (vmangos columns: id, patch, min/max players per team, min/max level, the four mark
  spells, the two start locations, player loot id), `battlemaster_entry`, `battleground_events`, `gameobject_battleground`,
  `creature_battleground`, `areatrigger_bg_entrance` and the importer are a World schema change (hosted-CI provider theories required:
  MariaDB DDL is not transactional, PostgreSQL DDL is, quoting and case folding differ). Also needed: areatrigger rows 3646, 3647, 3669,
  3671 (AreaTrigger.dbc-derived, not in classic-db) and WorldSafeLocs 769-772 (DBC). `BattlegroundTemplate` is the record the loader will
  fill; `BattlegroundManager.RegisterTemplate` validates the map. classic-db's WSG row `(2, 5, 10, 10, 60, 769, 770, 75, 0)` is the cross-check
  (it has no mark-spell columns; the vmangos column set is the schema to import).
- **Slice S5 (instances, entry points, persistence).** `WorldRuntime.MapResolver` is one slot held by `InstanceManager`, which returns the
  shared map for every non-dungeon template, so a battleground map needs a composite resolver and an instance id allocator shared with
  `InstanceManager` (`IBattlegroundManagerHost.AllocateInstanceId` is that seam). `TeleportService.Check` (line 118) and
  `TeleportCommands.cs:130` refuse every battleground map and must become a per-player assignment check (Player.cpp:1863), with the far-teleport
  `LeaveBattleground` rule (Player.cpp:2036-2043) and the entry-point return (Player.cpp:2144-2150, 18624-18672). `character_battleground_data`
  (guid, instance, team, x, y, z, o, map; Player.cpp:20950-20982) and the login recovery to the entry point (Player.cpp:14775-14788) are a
  Characters schema change (hosted-CI rule). Shared files, so it serialises against other lanes.
- **Slice S6 (the daemon lifecycle).** The `IWorldFeature` that owns one `BattlegroundManager`, the handlers (BATTLEMASTER_HELLO/JOIN, BATTLEFIELD_LIST/JOIN/PORT/STATUS,
  LEAVE_BATTLEFIELD, PVP_LOG_DATA, PLAYER_POSITIONS, area spirit healer 738-740), the `IBattlegroundManagerHost`/`IBattlegroundHost`
  implementations, flag stand/drop game-object handlers (type 24 and 26 through `RegisterUseHandler`; the dropped flag's pickup spell effects
  23383/23384), the `IWorldStateProvider` for the initial states, the battlemaster gossip option 12, the 50 yd portal rule and
  `Player::LeaveBattleground` (Deserter 26013 gated on `CastDeserter`). `CmsgBattlefieldStatus` stays with
  `World/Handlers/InactiveQueueHandlers.cs` until then; that file and its two test files must be retargeted by this slice, not before, because
  both handler groups would otherwise register one opcode.
- **Slice S7 (BG raid groups).** `Group.MinMemberCount` is a static 2, `GroupType` has no battleground raid; the per-team raid group, the
  original-group restore and the "do not send teammates when the player's group is the BG raid" rule of MSG_BATTLEGROUND_PLAYER_POSITIONS are
  not modelled (the builder always takes the teammate list the host passes).
- **Spawn gating and doors.** `IBattlegroundHost.EventStateChanged/OpenDoors/DespawnDoors` are called with the right events at the right time; the
  creature and game-object spawn system must consult the active events when it loads BG spawns (BattleGround.cpp:1337-1400). That is the
  `creature-movement-spawns` lane's loader.
- **Auras and spells.** The flag auras 23333/23335 and the dropped flag 23334/23336 are cast through `IBattlegroundSpellPort`; the carrier drops the
  flag on aura removal only when the aura engine reports it (the `aura-engine-completeness` lane). The mark spells and the deserter debuff go
  through the same port. Honor (`IBattlegroundHonorSink`, `IHonorRankSource`), reputation (`IBattlegroundReputationSink`) and the weekend
  (`IBattlegroundCalendar`) map to the honor, reputation and game-events lanes; until they merge the scoreboard honor stays 0 and the rank
  shows 4.
- **Other battlegrounds.** Alterac Valley, Arathi Basin and arenas are out of scope; `BattlegroundFactories.Default` builds Warsong Gulch only, an AV
  group join is rejected as vmangos does, and the AV/AB branches of vmangos (AV queue minimum, AV initial maximum, AV randomization) are not ported.
- **Not ported from vmangos.** The queue announcer (`Battleground.QueueAnnouncer.*`), `BattleGround.RandomizeQueues`, the debug "testing"
  mode, the accurate-PvP reputation values of patches before 1.10, `BattleGround::HandleCommand`, the item reward by mail for a full bag
  (marks are cast spells here, as in vmangos for 1.12).
- **Resurrection.** The 30 s spirit-guide wave is not battleground code (vmangos `RESURRECTION_INTERVAL` is unused): it is the spirit-healer channel
  spell 22011 and the "Waiting to Resurrect" aura 2584. Nothing here models it; `BuildAreaSpiritHealerTime` is only the packet.

## Open questions

1. SMSG_BATTLEFIELD_STATUS status width (u32 here, u8 in wow_messages). Resolve with a real 1.12.1 client run of the status packet.
2. `InvitationType` default 1 (shipped config) versus 0 (code default) is a judgement call; both are supported.

## Tests

`tests/ArcaneCore.Game.Tests/Battlegrounds`: `WarsongGulchTests` (flag rules, timers, honor, reputation, world states, exits), `WarsongGulchPropertyTests`
(60 seeded random games: captures never exceed 3, one winner who owns the third capture, a flag is carried exactly while it has a carrier),
`BattlegroundCoreTests`, `BattlegroundPacketTests` (hand-written wire bytes) and `BattlegroundManagerTests` (queue, invitations in time, port, login).
No store or schema changed in this lane, so there is nothing that only ran on SQLite.
