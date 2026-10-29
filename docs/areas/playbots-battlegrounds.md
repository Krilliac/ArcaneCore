# Playerbots in battlegrounds: queueing, playing, leaving

How autonomous managed playerbots queue for battlegrounds, play Warsong Gulch, Arathi Basin and Alterac Valley, and leave when the match is over, so
the world has battleground traffic and the bots exercise the server's battleground code. The bot itself (goals, movement, the brain)
is in `docs/areas/playbots.md`; bot-led groups in `docs/areas/playbots-groups.md`; the server's battlegrounds (queues, matches, flags,
nodes, graveyards) in `docs/areas/battlegrounds.md`. A bot in a real player's party (the party AI) or under a scripted controller is
never taken.

References: vmangos `Handlers/BattleGroundHandler.cpp` (hello 40-73, join 82-264, port 361-507, leave 510-525) for what a client
sends and how the server answers; vmangos `PlayerBots/BattleBotAI.cpp` (`SelectFollowTarget` 379-421, leaving on the ended
scoreboard 461-485) and `PlayerBots/BattleBotWaypoints.cpp` (`WSG_AtAllianceFlag` / `WSG_AtHordeFlag` 76-140, `AtFlag` and
`SPELL_CAPTURE_BANNER` 21651, 163-215) for what a battle bot does; `BattleGroundWS.cpp` 354-480 / 539-548 and `BattleGroundAB.cpp`
337-458 for the server side of the objectives. vmangos battle bots are server-side puppets queued by a GM command and cast through
`CastSpell` directly; here every step is a client packet through the ordinary world handler, and who queues is decided by this
server's own rules (below).

## Pieces

| File (`src/ArcaneCore.Playerbots/Battlegrounds/`) | What it does |
|---|---|
| `PlayerbotBattlegroundOptions.cs` | `World:Playerbots:Battlegrounds` (every key live through `.reload config`). |
| `PlayerbotBattlegroundBrackets.cs` | Which types and brackets a bot of a level may join; refuses Alterac Valley, a missing template, missing objective content and no battlemaster in reach. Unit tested. |
| `PlayerbotBattlegroundDemand.cs` | The demand snapshot and the caps: per (type, bracket, team) real players waiting and bots committed; the pick. Unit tested. |
| `PlayerbotBattlegroundQueueMachine.cs` | One bot's queue-to-leave life as a pure state machine (phases and the packet of each transition). Unit tested. |
| `PlayerbotBattlegroundRoles.cs` | Who attacks, escorts and defends in a match. Unit tested. |
| `PlayerbotBattlegroundCoordinator.cs` | The world feature: scans free bots, runs every followed bot's queue machine from the server's own state, sends the client packets, rebuilds the demand and the roles, the GM report. |
| `PlayerbotBattlegroundBuffs.cs` | The speed, restoration and berserking buff objects: the per-map board (a scan at most every 3 s, a claim per buff and team) and one bot's choice of which buff is worth a detour. Unit tested. |
| `PlayerbotBattlegroundAI.cs` | One per followed bot: the walk to the battlemaster, and the match (fighting enemy players, the objective, the wait for the spirit guide). |
| `PlayerbotWarsongTactics.cs` | The Warsong Gulch plan: every living bot's duty each think, and the idle check. Unit tested. |
| `PlayerbotArathiBasinTactics.cs` | The Arathi Basin team plan, free of world state: every living bot's order (assault, defend, retreat) and the buff choice. Unit tested. |
| `PlayerbotBattlegroundAI.ArathiBasin.cs` | One bot's Arathi Basin think: the plan read from the match, its order carried out, the buffs stepped on. |

Changes outside the folder: `ManagedPlayerbotFeature` calls the coordinator every tick after the group coordinator and lets it drive a
bot before the group AI, the party AI and the brain (`BattlegroundDriven`); a bot the coordinator holds is not free for bot groups and a
bot in or waiting for a bot group is not free for battlegrounds (`PlayerbotGroupCoordinator.Involves`); the group coordinator never
disbands a battleground raid; `PlayerbotMapPolicy` lets a bot walk on a battleground map only while it is a participant of the match
there; `PlayerbotCombatSpells.CastAtObject` (CMSG_CAST_SPELL at a game object); `PlayerbotGoalKind.Battleground` (persisted as its
number, appended); `.playerbot battlegrounds`.

## Who queues, and for what

Every 2 seconds each **free** bot is looked over: autonomous (no controller), not driven by its party AI, not in or waiting for a bot
group, in no group and with no group invitation, alive and out of combat, on a map of `World:Playerbots:AllowedMaps` (not a dungeon or
battleground), in no battleground queue or match, without the Deserter debuff (26013).

The types it may join (`PlayerbotBattlegroundBrackets.Eligible`), each refusal logged once per type and reason:

| Check | Refusal |
|---|---|
| Warsong Gulch, Arathi Basin or Alterac Valley | `Unsupported` |
| with `RaiseCapToMatchMinimum` off: `MaxBotsPerBattleground` at least twice the template's `MinPlayersPerTeam` (bots alone could never start the match) | `CapBelowMinimum` |
| a `battleground_template` row for the type | `NoTemplate` |
| the bot's level within the template's minimum and maximum; the bracket is the manager's (`BracketOfLevel`, vmangos `Player::GetBattleGroundBracketIdFromLevel`) | `LevelOutOfRange` (not logged) |
| the objective content, decided once per content load: Warsong Gulch both flag stands (179830, 179831) on map 489 and both flag room triggers (3646, 3647); Arathi Basin node banners on map 529 (objects with a `gameobject_battleground` node event and a lock) where every node's neutral banner is one the match routes to the node click (`MatchRuntime.RoutesBannerUse`, the server's own rule), and the banner spell 21651 in the spell store, known by the bot (`playercreateinfo_spell` teaches every character "Opening" 21651) | `MissingContent` |
| a battlemaster of the type (a creature spawn whose entry has a `battlemaster_entry` row) on the bot's map within `MaxTravelYards`, whose faction is not hostile to the bot, whose spawn no game event holds back (the Warsong Emissary of "Call to Arms" while it is off), in a walking region the bot's own joins (by the navigation mesh's region graph, no path search; an indoor end is let through; a master that stands still is preferred to one that walks waypoints), whose route was not refused from the bot's zone in the last hour, and the type not set aside for this bot | `NoBattlemaster` (not logged) |

Then the pick (`PlayerbotBattlegroundDemand.Pick`): a (type, bracket) a real player waits in, whose team still needs bots to reach the
template's minimum per team, is taken at once (`PreferFillingRealPlayerQueues`); otherwise the bot rolls its per-minute chance
(`QueueChancePerMinute`, as `1 - (1 - p)^(scan seconds / 60)`) and takes a random one of the open choices. A choice is open while the
bracket holds fewer than `MaxBotsPerBattleground` committed bots, the bot's team fewer than `min(MaxPlayersPerTeam, ceil(cap / 2))`, and
no running match of the bracket with room already holds the cap in bots. With `RaiseCapToMatchMinimum` (on) a type whose template
needs more than the cap (2 x `MinPlayersPerTeam`, Alterac Valley's 20 per team against the default cap of 20) uses that minimum as its cap
(never above 2 x `MaxPlayersPerTeam`), so bots alone can start it; with it off such a type is refused (`CapBelowMinimum`, logged once).

The snapshot behind these numbers is rebuilt at every scan from the battleground manager itself: the real players (sessions that are
not managed) waiting in each queue and not yet invited, the bots this coordinator committed (walking, joining, queued, invited,
porting), and the managed participants of every running match that still has free slots (a new queuer would be put into it).

## The queue, like a client

`PlayerbotBattlegroundQueueMachine` decides every tick from what the server's state says about the bot: its queue slot for the type,
its invitation (the instance it is invited to), the match it is bound to and whether the match counts it on a team. The coordinator
reads this state directly where a client reads SMSG_BATTLEFIELD_STATUS (which the bot also receives); every request is the ordinary
client opcode through its world handler and never waits for the shared action budget.

| Phase | Driven by | What happens |
|---|---|---|
| `travel` | the coordinator | walk to the chosen battlemaster until within 4 yards of the master as it stands now (a fight on the way is answered first); at most the walk's timeout (10 minutes, longer by the walk's estimated length up to 60), and given up (set aside for that bot alone, which also leaves that master alone for an hour (longer than the set-aside, so it does not walk to it again): a stall is about the bot, so the master is not refused for its zone, unlike a refused route or the timeout, which refuse it for the zones the walk started and failed in) when the bot gets no 10 yards closer in 3 minutes (longer for a long way; the clock restarts on every tick the bot is attacked by a unit it may hit back; the goal follows the live master, and the best distance the watch holds moves with the goal's change of distance while its clock runs on, so a master pacing away and back from a bot that stands still is still a stall) |
| `join` | the coordinator | CMSG_BATTLEMASTER_HELLO (the server answers SMSG_BATTLEFIELD_LIST), then CMSG_BATTLEMASTER_JOIN (map, instance 0, not as a group); no queue slot within 15 seconds sets the type aside for this bot for 10 minutes |
| `queued` | the brain | the bot carries on with what it was doing; after `MaxQueueWaitMinutes` it leaves the queue (CMSG_BATTLEFIELD_PORT action 0) and sets the type aside for 10 minutes |
| `invited` | the brain | an invitation the bot cannot answer yet (dead, between maps); answered when it can |
| `entering` | the coordinator | CMSG_BATTLEFIELD_PORT action 1, the far teleport acknowledged like any other (MSG_MOVE_WORLDPORT_ACK); 30 seconds at most |
| `inmatch` | the coordinator | the match AI (below) |
| `leaving` | the coordinator | CMSG_LEAVE_BATTLEFIELD once the match is over (WAIT_LEAVE); sent again every 5 seconds while the server still has the bot in the match |

**Taken.** Every tick the coordinator reads the group manager itself: a bot in a group that is not a battleground raid, or holding a group
invitation, is taken (as is one a controller, its party AI or a bot-led group drives). Walking, joining or queued, it leaves the queue
(CMSG_BATTLEFIELD_PORT action 0); invited, it declines; in the rare tick where its port already went out, it leaves the match again
(CMSG_LEAVE_BATTLEFIELD, sent until the server acts on it). That leave is a desertion like a real client's: in a match that is waiting to
start or in progress the server casts the ordinary Deserter debuff (26013, 15 minutes in the 5875 Spell.dbc; `BattlegroundFeature.LeaveBattleground`,
vmangos Player::LeaveBattleground), and the bot queues for nothing until it wears off. The party AI's own flag follows only at the bot's next
think, too late for a port sent the same tick, so it is not what decides.

An invitation into a match that already holds `MaxBotsPerBattleground` bots (participants plus bots porting in) is declined
(CMSG_BATTLEFIELD_PORT action 0). A bot that a controller, a real player's party or a bot group takes while it walks, joins or waits
leaves the queue. Back in the world (the server teleports it to where it queued, the battleground entry point) the brain resumes with its
quests, suspensions and memories; only its route, target and rest belong to another place (`PlayerbotBrain.ResumeAfterGroup`).

A free bot that is already queued for, or bound to, a match of a type the bots play (a world restart, a controller that let it go) is
taken on where it stands. A bot taken by a controller is forgotten where it stands: the server keeps its queue or match.

## In the match

`PlayerbotBattlegroundAI.Play`, once per think (`ThinkIntervalMs`), the movement acknowledgements every tick:

* **Before the gates open and after the end** the bot stands still (the start countdown, the frozen end).
* **Fighting.** The enemy is a living participant of the other team on the bot's map; teammates never (the match's teams decide, not the
  faction templates). In order: the enemy carrying the bot's own flag within 80 yards (Warsong Gulch), an enemy attacking the bot, the
  enemy it already fights, the nearest enemy within 30 yards (in Warsong Gulch the enemy its order names comes first: the enemy carrier for
  a hunter wherever it is, an intruder of the flag room for a defender). Whether a player is an enemy is the one hostility rule of the playerbots
  (`PlayerbotHostility.IsHostilePlayer`: the combat hooks, so a PvP-flagged player of the other team) plus the match's team check. The fight itself is
  `PlayerbotPvpFight`, shared with the brain (a bot hits back a player who attacks it) and defend mode: the class rotation
  (`PlayerbotCombatSpells`), the position (`PlayerbotBrain.DecidePosition`), facing the victim, the melee swing. A ranged bot with nothing
  to cast for 8 s (`RangedIdleLimitMs`), or below 10% mana, closes in and swings (vmangos `CombatBotBaseAI::BeginChasing`). A flag
  carrier does not stop to fight.
* **Off the match's map** (bound to it but elsewhere) the bot stands: the objectives are coordinates of the battleground map.
* **Idle.** A living bot out of combat that stood still for 15 s although its Warsong Gulch order sends it somewhere, or an enemy its
  order names is near, is idle: `bot-bg/idle-in-match`, once per match (`PlayerbotWarsongTactics.IsIdle`).
* **Death.** The bot releases (`PlayerbotRecovery`); the server casts Waiting to Resurrect on release and puts the ghost at the match's
  graveyard (in Arathi Basin the nearest node its team occupies, else its start: vmangos `BattleGroundAB::GetClosestGraveYard`). A
  battleground ghost waits within 15 yards of the graveyard's spirit guide for its resurrection wave instead of running to its body (the
  recovery's `AwaitAreaResurrection`; `PlayerbotArathiBasinPlayWorldTests.Ab_DeadBot_WaitsAtTheSpiritGuide_InsteadOfRunningToItsCorpse`).
  Nothing happens while the match is not in progress.
* **Roles** (`PlayerbotBattlegroundRoles`, per match and team over the coordinator's bots, healers escort first): Warsong Gulch 40%
  attackers, 30% escorts, 30% defenders; Arathi Basin 70% attackers, 30% defenders. These shares are this server's design.

**Warsong Gulch.** The static roles are not used: every think the coordinator plans the team once (`PlayerbotWarsongTactics.Plan`, per
match, team and tick, after mangosbot's `BGTactics::selectObjective` and `atFlag`, cmangos/playerbots BattleGroundTactics.cpp) and each
living bot gets a duty. In order: the carrier walks into its own flag room trigger (3646 Alliance, 3647 Horde; its motion reports
CMSG_AREATRIGGER like a client, and the match captures when the carrier's own flag is home) and waits there while its own flag is away;
the nearest bot to the own dropped flag returns it and the nearest to the enemy dropped flag picks it up (CMSG_GAMEOBJ_USE); while the
own flag is home, defenders hold the flag room (none below three bots, one, two from eight; at least as many as enemies in the room,
which they fight); half of the rest hunt the enemy carrier of the own flag; escorts follow the own carrier (healers first, vmangos
`SelectFollowTarget`); the rest hunt too while there is an enemy carrier, else take the enemy flag from its stand (CMSG_GAMEOBJ_USE within
4 yards; waiting at the stand's spawn while the flag is away or respawning) or hold the middle of the field.

**Arathi Basin** (`PlayerbotArathiBasinTactics`, after the shape of mangosbot's `BGTactics::selectObjective` for `BATTLEGROUND_AB`,
cmangos/playerbots `BattleGroundTactics.cpp`). A node may be clicked by a team when it is neutral or its status belongs to the other team
(vmangos `EventPlayerClickedOnFlag`: `status == 0 || teamIndex == status % 2`): it is *attackable*; a node contested or occupied by the
team is *held*. Every node is always one or the other, so a bot always has a node to go to. Every think each bot computes the plan of its
whole team (the coordinator's bots of that team and match; real players count as friends, never get orders), ties by guid, and takes its
own order:

1. **Retreat.** A bot under 25% health with more enemies than friends within 20 yards falls back to the nearest node its team occupies
   (where its graveyard is), else its start.
2. **Defenders**, limited: 30% of the living bots from three bots up (more while enemies stand at held nodes), only while something is
   held and never all of them while something is attackable; defender-role bots first. A held node nobody defends is covered before a
   second defender goes anywhere, the node with enemies within 40 yards first, then the nearest. A defender stands within 8 yards of its
   banner and fights the enemy nearest the node within 40 yards (beyond the 30 yards a bot engages on its own); when the enemy assaults
   it, the defender casts at it (retakes it).
3. **Attackers**, the rest, spread over the attackable nodes by distance and team size: each node takes at most `ceil(attackers / nodes)`
   plus one per enemy at it, the cheapest (bot, node) pair first, where a node the enemy contests counts 60 yards nearer (its capture
   timer runs: an own node assaulted is retaken before it falls) and a neutral one 25 yards nearer. An attacker clears an enemy at its
   node (within 45 yards of it) before the cast a hit would cancel, walks to the banner, stops within 4 yards and casts 21651 at it
   (CMSG_CAST_SPELL with TARGET_FLAG_OBJECT; Spell.dbc: OPEN_LOCK lock type 17, 10 second cast, 0-5 yards, interrupt flags 0x1F, so a hit
   cancels it and the bot fights back), standing still while it casts; a node a teammate is already casting at is covered, not cast at
   again (vmangos `AtFlag`).
4. **Buffs.** Out of combat, a bot steps on a battleground buff within 35 yards (the buffs stand 20-30 yards from their banners): speed and
   berserking always, restoration under 80% health or mana. They are traps the server fires within 3 yards (`MatchRuntime.UpdateBuffTraps`).

The server's open-lock effect turns the opening of a banner button with `noDamageImmune` into the click, and the opening of a neutral
goober banner into its trigger spell cast at the bot, which clicks (docs/areas/battlegrounds.md).

**Buff objects** (`PlayerbotBattlegroundBuffs`; Warsong Gulch's speed, restoration and berserking buffs, the Arathi Basin node buffs).
They are traps (classic-db 179871/179904/179905 and alternates: type 6, data2 radius 0, data3 the spell 23451 / 23493 / 23505, data5
cooldown 3): a living player within 3 yards sets one off, the trap casts its spell on the player and the match uses it up (vmangos
`GameObject::Update`, GameObject.cpp:476-551, and `BattleGround::HandleTriggerBuff`, BattleGround.cpp:1713-1757; here
`MatchRuntime.UpdateBuffTraps`). A bot takes one as a player does, by walking onto it; nothing applies the aura from the bot side. Each
think, before it fights or plays its objective, the bot checks the map's board (one scan of the map's objects every 3 seconds at most,
shared by all bots of the match): a buff within 60 yards is worth it when it is speed and the bot carries the enemy flag or chases the
enemy carrier (anyone else only one within 10 yards), restoration and the bot is under 60% health or mana, berserking and the bot fights
(never the carrier); a buff whose aura it has is skipped. Its detour (against the flag room for the carrier, the chased carrier for a
chaser, else its distance) must stay under 40 yards, 20 while an objective is urgent (carrying, chasing, a flag on the ground) and 15 in a
fight. The bot claims the buff for its team (renewed each think, lapsing after 3 s), so a teammate goes for another; it gives up after 15
s (and leaves that buff alone for 30 s) and forgets a buff that despawned or someone else took. Activity `taking-buff`.

## Configuration (`World:Playerbots:Battlegrounds`, live)

| Key | Default | What the code does with it |
|---|---|---|
| `World:Playerbots:Battlegrounds:Enabled` | `true` | Off: no bot starts a queue; bots already walking, queued or in a match finish their business. |
| `World:Playerbots:Battlegrounds:QueueChancePerMinute` | `0.05` | 0..1. The chance per minute that a free, eligible bot starts to queue; each 2-second scan rolls `1 - (1 - p)^(seconds / 60)`. A real player's queue that needs bots is filled without a roll. |
| `World:Playerbots:Battlegrounds:MaxBotsPerBattleground` | `20` | 0..80. Unless `RaiseCapToMatchMinimum` raises it for a type whose template minimum needs more, bots committed to one (type, bracket) never exceed it, a team never more than `min(MaxPlayersPerTeam, ceil(cap / 2))`, a running match holding it blocks its bracket, and an invitation into a match holding it is declined. 0 queues no bot. |
| `World:Playerbots:Battlegrounds:PreferFillingRealPlayerQueues` | `true` | A (type, bracket) a real player waits in gets bots at once, up to the template's minimum per team, before any random choice. |
| `World:Playerbots:Battlegrounds:RaiseCapToMatchMinimum` | `true` | A type whose template minimum needs more bots than `MaxBotsPerBattleground` (2 x `MinPlayersPerTeam`, e.g. Alterac Valley's 20 per team) gets that minimum as its cap, never above 2 x `MaxPlayersPerTeam`, so bots alone can start it. Off: the type is refused for bots (`CapBelowMinimum`). Template values are never changed. |
| `World:Playerbots:Battlegrounds:MaxTravelYards` | `1500` | 100..20000. The farthest (straight line, same map) a battlemaster may stand for a bot to choose it. |
| `World:Playerbots:Battlegrounds:MaxQueueWaitMinutes` | `30` | 1..240. A bot leaves a queue after this long and does not queue for that type again for 10 minutes. |

## Where it shows

`.playerbot battlegrounds` (GameMaster): enabled or not, the chance and the cap, the totals (queued, entered, left, declined, queues
given up), one line per bot with battleground business (type, bracket, team, phase, its time in it, its role and activity in a match),
the refused types with the reason, the last events. `.playerbot status` and BOTINSPECT show `bg=WarsongGulch:inmatch:attacker:taking-flag`
where the group would be, the goal `Battleground` (or `Recover` on a corpse run) and `decision=battleground` for the risk.

## Tests

* Unit (`tests/ArcaneCore.World.Tests/Playerbots/Battlegrounds/`): brackets and refusals (Alterac Valley `Unsupported` at every level
  with a template and content), the demand and caps, the queue machine, the roles, the options and their validation;
  `PlayerbotMapPolicyTests.Allows_ABattleground_OnlyToItsParticipants`.
* World (`PlayerbotBattlegroundWorldTests`, the real handlers and the manual clock, `BattlegroundBotTestContent`): two autonomous bots
  walk to their battlemasters, receive SMSG_BATTLEFIELD_LIST, queue, port into one Warsong Gulch match, and leave through
  CMSG_LEAVE_BATTLEFIELD when it ends (no Deserter, back where they queued, the brain driving again); a lone bot leaves the queue after
  `MaxQueueWaitMinutes`; a cap of two holds against the manager's own queue and participant counts; an invitation into a match at the cap
  is declined; a real player beside the battlemaster is never queued or moved; a real player waiting gets a bot for the other team and is
  invited but never ported by the coordinator; a scripted managed bot waiting in a queue brings no bot (it is no real player); a real player's
  party bot at a battlemaster never queues, even when the scan runs before its party AI noticed its master; a queued bot that joins a real
  player's party in the tick its invitation arrives declines it and is never ported; an autonomous bot takes the Horde flag and captures it at 3646
  (`Wsg_AutonomousBot_TakesTheEnemyFlag_AndCapturesItAtItsOwnFlagRoom`); an autonomous bot claims the stables with its own 21651 cast
  (`Ab_AutonomousBot_ClaimsTheStables`: SMSG_SPELL_START and SMSG_SPELL_GO of 21651, the node contested by the Alliance, the bot's
  bases-assaulted score); with the real goober neutral banner (classic-db 180087: lock 1479, data10 23932) Arathi Basin is accepted and an
  autonomous bot claims the stables through 21651 and the trigger spell
  (`Ab_WithTheRealGooberNeutralBanner_AutonomousBotClaimsTheStables_ThroughTheTriggerSpell`: SMSG_SPELL_GO of 21651 and of 23932, the node
  contested by the Alliance); a goober banner whose spell is no banner trigger is refused and never queued
  (`Ab_WithANeutralGooberWhoseSpellIsNoBannerTrigger_IsRefused_NoBotQueues`); Alterac Valley is refused and never queued.
  With the coordinator's group check, its banner routing rule and its real-player filter removed, the party, race, unrouted-goober and
  managed-bot tests go red.
* Buffs (`PlayerbotBattlegroundBuffTests`): the classic-db templates are recognised; a hurt bot prefers restoration over a closer
  berserking buff; the carrier prefers speed within the detour cap and never takes berserking; a far buff is ignored while an objective is
  urgent; two bots of a team never head to the same buff (an enemy may); a despawned buff is dropped and forgotten on the board; an overdue
  pursuit is given up. End to end (`Wsg_HurtBot_WalksOntoTheRestorationBuff_TheTrapCastsIt_AndTheBuffIsUsedUp`, `StartWithWsgBuffsAsync`):
  a hurt bot in a running Warsong Gulch match walks onto the restoration buff, the trap casts 23493 on it, the object is used up and the
  bot lets it go, the berserking buff beside it stays, and the board scanned at most once per 3 s.

The test world's battleground maps are flat with open paths, laid out compactly, with one-player-per-team templates; the battlemasters of
both factions stand beside the human start, where the scenario world logs every race in; the ids (maps, triggers, flag and banner entries,
spell 21651 and lock 1479 from the 5875 client's DBCs) are the real ones. The Arathi Basin test needs a skill catalog (an empty one):
without it the 21651 cast went through and nothing was claimed, which is how the test was seen to depend on the real spell effect.

## Limits

* **Alterac Valley needs 20 bots a side.** The template minimum is 20 players per team (retail, not changed). Bots alone start a
  match only when 20 eligible bots of each faction (level 51-60, within `MaxTravelYards` of a friendly Alterac Valley battlemaster) queue
  within `MaxQueueWaitMinutes`; the cap is raised to that minimum for the type (`RaiseCapToMatchMinimum`), the match is 20 against 20
  bots unless real players join, and up to 40 a side is never reached by bots alone.
* **Arathi Basin banners: only what the server routes.** classic-db and vmangos make the five neutral banners (180087-180091) goobers
  whose use casts a trigger spell (23932, 23935-23938) that vmangos scripts into the click (`BattlegroundBannerTriggerScript`); the server
  routes them (`MatchRuntime.RoutesBannerUse`: a button with `noDamageImmune`, or a goober whose data10 is a banner trigger spell), so with
  the real data the coordinator accepts Arathi Basin. A direct use (CMSG_GAMEOBJ_USE) of a routed banner is refused, so the match AI only
  ever casts 21651 at a banner; the open-lock uses the goober, which casts its trigger spell at the bot, which clicks the node. The
  coordinator decides with that same rule: content whose neutral banners the rule does not route (a goober with another spell) is
  `MissingContent` and no bot queues for it, and the match AI never casts at a banner the rule does not route. Only the stables node is
  exercised end to end with the goober shape (one bot, one node); the other four neutral banners are covered by the match tests
  (docs/areas/battlegrounds.md).
* **Opening a banner needs the skills feature.** The open-lock effect of 21651 is installed by the skills feature only when it has its skill
  catalog (`Skills:*DbcPath`, docs/areas/skills.md); without one the cast completes and nothing opens, for bots and players alike.
* **Navigation needs the battleground maps' mmaps** (489, 529; 30 for Alterac Valley would be needed if it were in scope). Without a
  navigation mesh the bot's routes fall back to the terrain stepper, which refuses steep and unknown ground: the bots stand at the start.
* **The battlemaster walk is on one map**, a straight-line `MaxTravelYards` check: no flight paths, boats, elevators or portals to a
  battlemaster. The server's join needs the battlemaster in interaction reach (vmangos HandleBattlemasterJoinOpcode through
  GetNPCIfCanInteractWith; cmangos/mangosbot queue with the guid from anywhere, which this server refuses), so a bot walks. The Undercity's
  battlemasters are reached by its elevators, which the navigation mesh does not join: the route is refused, and that battlemaster is
  left alone from the bot's zone for an hour (live: Gitrol in Silverpine Forest, 62 refusals before).
* **Waypoint-walking battlemasters.** The walk's goal follows a living master that moved, and a master that stands still is preferred to one that
  walks. In classic-db z2815 (ClassicDB_1_12_1_z2815.sql.gz, table creature joined with battlemaster_entry), 2 of the 34 spawns of the Warsong
  emissary 14991 (game event 20) have MovementType 2 (waypoint): spawn 190500 (map 1, 9952 / 2089) and spawn 190516 (map 0, -5007 / -1178); the
  other 32 have MovementType 0. 15 of the 228 battlemaster spawns have MovementType 2. A walking master is judged where it stands (its distance and walking region are taken from the creature when the map has it, not from its spawn point), and a bot at its goal does not take an empty spot for a missing walker (the 5 second refusal is for masters that stand still; a walker missing from the map for a minute is refused like one that stands still, so a missing walker costs a bot up to about a minute at the empty spot (often about 5 s)). The preference and the follow cover those spawns and any other that walks.
* **The walk's progress watch is approximate in two corners.** (1) The watch moves its best distance by the change in the goal's distance from
  the bot's current position, not from the position where the best was recorded. A bot stuck and bouncing between two points while a
  waypoint-walking master moves in step can in theory gain up to about twice the bounce width per cycle, enough over many cycles to count as
  10 yards of progress and hide the stall; the walk's timeout (10 to 60 minutes) is the backstop. (2) The best distance is clamped at zero when
  the goal moves far toward the bot's old position; after that the watch can no longer register progress (it would need the distance to fall
  below zero), so only arrival or the stall ends that walk. The same holds after a fight pulls the bot far off the road. Both need a bot far
  from a master that moved a long way.
* No premade groups: every bot queues alone (CMSG_BATTLEMASTER_JOIN with the group flag off).
* **A real player's party bot in a group queue.** A real player who queues as a group (CMSG_BATTLEMASTER_JOIN with the group flag) queues
  the party bot with it (the server's rule). That bot is not free, so the coordinator neither follows nor ports it: its invitation lapses after
  the server's two minutes and it leaves the queue then. The party AI has no battleground behaviour of its own.
* The shares of the roles, the Warsong Gulch defender counts and the engagement distances (30 yards, 80 for the flag thief without an
  order, 40 for the flag room) are this server's design,
  not vmangos values; vmangos battle bots follow recorded waypoint paths instead.
