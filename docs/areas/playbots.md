# Playerbots and playtest clients

ArcaneCore has two kinds of bot. **Managed playerbots** are server-owned P0 players inside the
world daemon (`src/ArcaneCore.World/Playerbots/`); they run autonomously or are driven by a
script, which is the **scenario harness** for automated testing of game systems. The
**MockClient playbot** (`arcane-mock playbot`, last section) is an external build-5875
protocol client.

## Managed playerbots

`ManagedPlayerbotFeature` (`IPlayerbotService`) owns persistent bot characters on random,
password-less Player accounts and logs them in through an ordinary socketless `WorldSession`
(`Net/ManagedWorldSession.cs`): every bot action is a real CMSG run through the real world
handler (`TryManagedAction`), and server replies are captured into a bounded outbound queue.
Configuration is `World:Playerbots` (`Enabled`, `MaxBots` 8, `MaxRegisteredBots` 1000, `ThinkIntervalMs` 500,
`MaxActionsPerTick` 4, `AllowedMaps` [0, 1], `FaultBackoffSeconds` 30, `MaxFaults` 3,
`FaultWindowSeconds` 3600, `AllowLocalLlm`, ...; off by default).

**Caps.** `MaxBots` (0..1000) bounds the bots **running** at once: a stopped, faulted or quarantined bot holds no slot, and a
start beyond it is refused with `playerbot-capacity` (a startup restore or quarantine retry waits for a slot). `MaxRegisteredBots`
(0..10000) bounds `.playerbot create` (`playerbot-registry-full`); every registered bot is a real account and character. Both
are live through `.reload config`: a raise admits more starts at once; lowering `MaxBots` below the running count stops nobody,
it refuses new starts until enough bots are stopped. Until 2026-10-08 `MaxBots` was capped at 64 and creation counted stopped
bots too; the ceiling of 1000 and its measurement are in `docs/integration/perf-limits-20261008.md` (a ceiling, not a
recommendation: watch `.server info` on a real world).

GM commands: `.playerbot create|start|stop` (Administrator), `.playerbot status|list|inspect`
(GameMaster), `.playerbot scenario list|run` (Administrator, below). The autonomous brain's
behaviour is described in `docs/integration/playerbot-*.md`.

Before a pull the brain weighs each candidate's risk against its reward, and in a fight it retreats past the creatures' leash when
it is losing (`World:Playerbots:Risk`, live): `docs/areas/playbots-risk.md`. The risk decision ends each `.playerbot status`
line and has its own `BOTINSPECT` line.

Bots also use the faction auction house and the mailbox: they list loot they have no use for, buy one gear upgrade within a budget
and collect what sold, expired or was won (`World:Playerbots:Economy`, live): `docs/areas/playbots-economy.md`.

Content one bot cannot do (elite, dungeon and raid quests, objectives inside instances, quest targets too strong alone) becomes a
"needs a group of N" goal: `PlayerbotGroupCoordinator` matches bots by goal, team, level and role, the leader invites the others
through the ordinary group packets, and `PlayerbotGroupAI` drives the members until the content is done (`World:Playerbots:Groups`,
live; `.playerbot groups`): `docs/areas/playbots-groups.md`.

A target number of bots kept online, spread over level bands and zones and held back by the world's tick time, is the population
manager (`World:Playerbots:Population`, off by default, live; `.playerbot population`): `docs/areas/playbots-population.md`.

Bots found guilds, fill and rank them, say a short line in guild chat and (off by default) wave at nearby real players
(`World:Playerbots:Social`, live): `docs/areas/playbots-social.md`.
Free bots near a battlemaster of their faction queue for Warsong Gulch and Arathi Basin (Arathi Basin where the server routes its neutral
banners, as it does the classic-db goobers; never Alterac Valley), fill the queues real players wait in, play the objective (the enemy flag, the node banners with spell 21651) and leave when the match ends:
`PlayerbotBattlegroundCoordinator` and `PlayerbotBattlegroundAI` (`World:Playerbots:Battlegrounds`, live; `.playerbot battlegrounds`):
`docs/areas/playbots-battlegrounds.md`.

### Autonomous and scripted mode

By default a running bot is **autonomous**: each world tick `PlayerbotBrain` chooses quest,
town, trainer, combat, loot and recovery goals within the shared per-tick action budget.

Movement is separate from both: `PlayerbotMotion` advances every bot's current route every
world tick (before any think, outside the action budget) and reports it the way a 1.12 client
does — START, heartbeats every 500 ms and at turns, STOP — so observers see smooth running
whatever the think interval. Brain, goals and scripted controllers only choose routes
(`PlayerbotNavigation.TryPlan` / `TryAdvance`). It also gives up navigation loops. Details and
the 2026-10-07 root causes: `docs/integration/playerbot-movement-and-tick-health.md`; whether
real terrain/collision/navmesh data is needed: `docs/integration/maps-vmaps-mmaps.md`.

**Game events.** A bot respects the server's game-event state (`World:GameEvents`, `game_event_quest`, `game_event_creature`): a quest
of an event that is not running is never offered to it (the server's `CanTakeQuest`, vmangos `Quest::IsActive`) nor turned in
(`IsRewardable`, vmangos `PrepareQuestMenu`), and one already in its log is kept but its objectives are not hunted (alone or as a group
goal) until the event runs again; a quest giver or ender whose spawn is listed under an event is not a destination while the spawn gate
keeps it out of the world (`PlayerbotWorldDestinations.IsPresent`), so a complete quest whose only ender is a holiday NPC is not walked
to off season. This needs the world's game-event tables: a world migrated from the Codex-line schema has them empty (the live world of
the wave-8 rehearsal: bots took "Winter's Presents" 8827, "Dearest Colara," 8898 and the Lunar Festival and Midsummer quests in October)
until `arcane-content-importer refresh` fills them (docs/areas/content-import.md).

The live snapshot replay (`PlayerbotLiveSnapshotReplayTests`: `ARCANECORE_TEST_BOT_REPLAY_DB`, `ARCANECORE_TEST_WORLD_DB`,
`ARCANECORE_TEST_TERRAIN_DIR`, optional `ARCANECORE_TEST_BOT_REPLAY_SETTINGS` without a `Database` section, `..._NAMES`, `..._TRACE`,
`..._TIME`) runs the game-event clock from the snapshot's moment (`ARCANECORE_TEST_BOT_REPLAY_TIME`, ISO 8601, else the characters
file's last write time) with game time, as it already ran the death clock: before, events followed the real date while the replay ran
its game time about 30 times faster, so a replay in December would have turned Winter Veil on for an October snapshot. It waits for each
step's quest reward writes (on the fast clock a bot's 15-second exchange deadline ran out while the write was on its way, and the reward
counted as refused) and prints each bot's quests taken, rewarded and still open.

A far goal (a quest giver or ender, a corpse) is approached a step at a time (`PlayerbotNavigation.TryPlanToward`): the point
`MaxPathPoints - 2` yards along the straight line (at most 90% of `MaxRouteYards`), or the walkable point the mesh reaches nearest to it.
Navigation-mesh tiles load with the map's grids round the players, so that point may lie on a tile not loaded yet, where the mesh
answers a straight line (vmangos `PathFinder`'s `HaveTiles` shortcut) that the terrain stepper refuses over any hill; the step is then
halved (down to 24 yards) until it stays on the loaded mesh, and walking it loads the next tile. Before, the bot stood at such a tile edge
for good: the wave-8 rehearsal's quest-35 stall (Dawnrover 107 yards north of the Elwynn tile boundary, 610 yards from Guard Thomas).

Every bot route excludes the mesh's steep polygons (`NavTerrain.SteepSlopes`). A bot standing on steep ground, or on a patch of walkable
polygons among them, got no route at all (no path, or a partial path ending at the patch's edge) and stood there for good: Ironwander on
the mountainside north of Coldridge Valley (-5605.6, 155.8, 453.3), live 2026-10-09, `stalled 7960s: goal=Grind target=1196` (the goal
and target were the last ones it had chosen; every plan since had failed). When the ordinary query fails and the bot is not on walkable
ground with an off-mesh destination, `PlayerbotNavigation` asks the mesh again with steep polygons allowed and takes that path up to its
first corner on walkable ground from which the ordinary query is a route, refusing any way that leads more than
`SteepEscapeClimbYards` (1.5) above the bot (`PlayerbotSteepGroundTests`; on the real terrain
`FromIronwandersSlope_TheBotWalksDownToTheIceClawBear`). The stall watch also gives the goal up again after every further stall bound
without progress (`PlayerbotStallWatch.GiveUps`, `StallGiveUps` on the brain), not only when the stall begins; the stall is still counted
and logged once.

#### Travel by the Deeprun Tram

Stormwind and Ironforge are 3,960 yards apart in a straight line with no road a bot can walk between them (the Elwynn and Dun Morogh
regions are walled apart by mountains). Before, a bot with a destination on the other side walked the straight line until it stuck:
live 2026-10-11, 117 dwarf and gnome bots with Dun Morogh, Ironforge and Loch Modan turn-ins (400, 2160, 353, 1679, 1879, 2997, 2039,
2218; reverse 1338) stood at the Stormwind tram entrance (-8285, 554) and on the Elwynn ridge. Now they ride the tram as a player does
(`PlayerbotTram`):

* **When.** `PlayerbotWorldDestinations` asks `TramNetwork.Choose` before it plans a walk: the bot takes the tram when its destination
  lies on the other platform's side (nearer that platform's way out than this platform's entrance) and walking to this entrance plus
  walking from the other exit to the destination is shorter, in straight lines, than the straight line itself. A failed trip is not tried
  again for 10 minutes (`RetryAfterFailureMs`); the destination then falls back to the walk and the ordinary stall watch.
* **The tram, from the data.** `TramNetwork.Build` reads the area triggers: the two entrances on the continent whose
  `areatrigger_teleport` leads to map 369 (classic-db 2173 Stormwind, 2175 Ironforge), each paired with the exit on map 369 nearest its
  landing whose teleport lands back beside it (2171, 2166). Anything else (one entrance, an exit missing or landing elsewhere) is no
  tram, and no bot rides. The cars are the map-wide elevators of map 369 (the six "Subway" objects 176080-176085 and their
  TransportAnimation.dbc paths; docs/areas/transports.md).
* **The trip** (`TramStage`): walk into the entrance trigger (the bot may take that one teleport only while its trip heads there,
  `PlayerbotAreaTriggers.AllowTeleportsTo(369)`); on the tram map, pick the car that stops at this platform soonest at a time the bot can
  be beside it (`PlayerbotTram.NextDock` samples the car's animation every 250 ms over two cycles), walk to the platform spot beside
  where it will stand (8.6 yards off the track's centre line, the car's half width from its model bounds plus two, where the floor is
  level with the landing), wait, and step aboard when it stands there. Aboard, the bot reports what a client standing on a transport
  reports: MSG_MOVE_HEARTBEAT with MOVEFLAG_ONTRANSPORT, the car's GUID and its offset on the car, at the world position the car's
  animation and the offset give, every 500 ms (`PlayerbotMotion.SendAboard`); observers' clients place it on their own car by the offset.
  When the car stands at the other platform the bot steps off on the side nearest the exit (a heartbeat without the transport block,
  `SendStepOff`) and walks into the exit trigger, which puts it on the continent beside the other city's entrance. The destination then
  plans again from there.
* **Map 369 stays off `AllowedMaps`.** The brain drives a trip before anything else (`PlayerbotTram.Drive`), so nothing else moves the bot
  meanwhile and the stall watch does not judge a bot waiting for its car; a fight on the way to the entrance ends the trip. The bot may
  plan and walk on the tram map only while its trip is under way (`PlayerbotMapPolicy.MayMoveOn`); a bot there without one (logged in
  after a save on the way: the login gate admits the tram map when the tram joins a continent of AllowedMaps,
  `PlayerbotMapPolicy.MayLogInOnTram`) walks nowhere but into the nearest platform's exit.
* **Fails closed** with a stall reason (logged as `Playerbot <id> (<name>) tram: ...; it gives up that goal`, the goal set aside for the
  bot itself as for any stall, but never for every bot, see below): no car runs on the tram map, no car stops at the platform within two cycles (about five minutes), no way to the side of a car,
  no way to the entrance (no progress for 60 s), or a car that did not stop at the other platform within a cycle. On the tram map the bot
  then leaves by the exit of the platform it is at; when it cannot walk into that exit within 60 s without progress, the server sends it
  where the exit would (that trigger's own teleport).
* **Beside the home, the shared set-asides and death.**
  * *Home return.* A homed bot on a trip is on "another map" (369) or far from its home, which an Auto return teleports from at once. The trip
    runs before the home return (`PlayerbotBrain.Update`), so no home teleport cuts a ride short; once the bot is out on the continent with no
    errand left, the home return applies as usual. A completed turn-in that began the trip runs before the home return too.
  * *Stalls.* The stall watch never judges a bot on a trip. A failed trip is the bot's own set-aside only (`GiveUpStalledGoal(shareable:
    false)`): it says the tram did not carry the bot, not that the goal beyond it is unreachable, and it is reported at the exit the bot left
    by. Shared, three bots behind one stopped car would set every bot's quest on the other side aside, and bot QA would read it as that
    quest's fault. Its line is never marked `outside its home` (only `stalled` lines are).
  * *Death.* A death ends the trip and its entrance consent (`PlayerbotTram.Cancel` when the brain records the death). The ghost's corpse
    run may walk the tram map while its body lies there (`PlayerbotTram.AllowsWalkingOn`), reclaims it as anywhere else (no spirit healer
    stands on map 369, so the body is the way back), and the revived bot, on the tram map without a trip, leaves by the nearest platform's exit.

Tests: `PlayerbotTramRouteTests` (the brain drives a bot from the Stormwind tram exit to Ironforge with the classic-db trigger and car
rows, a synthetic animation and flat floors, with the anticheat judging the bot as a client: no fake transport, no speed; a destination of
`PlayerbotWorldDestinations` on the Ironforge side begins the trip; a bot on the tram map without a trip plans nothing there and leaves by
the nearest exit; no car fails the trip closed and the bot goes back out; the entrance is not taken without a trip),
`PlayerbotTramNetworkTests` (the network from the rows, `Choose`, `NextDock`), and `PlayerbotTramRealTerrainTests` (needs
`ARCANECORE_TEST_TERRAIN_DIR` and `ARCANECORE_TEST_DBC_DIR`: the same ride on the real map 369 mesh and models and the client's
TransportAnimation.dbc; the bot boards an Ironforge-bound car 49 s after arriving and is out at Ironforge 140 s after it started), and
`PlayerbotTramInteractionTests` (a bot homed in Westfall rides to Ironforge without a home teleport and is taken home only after the trip; a
failed trip sets the quest aside for the bot but not for every bot; a bot killed on the platform drops its trip, walks back to its body on the
tram map as a ghost, is revived there and leaves by the Stormwind exit).

Not done: only quest destinations (`PlayerbotWorldDestinations`) begin a trip; trainer, vendor and home-area walks
(`PlayerbotTrainerDestinations`, `PlayerbotTownGoals`, `PlayerbotHome`) do not use the tram yet, and party followers follow their leader
only through triggers the leader took (`PlayerbotFollower`). `Choose` compares straight lines, so a bot well east of both cities (Redridge
to Thelsamar) still walks. Only a death on the platform is covered by a test; a death aboard a car (its body in the tunnel, or a revival
from the transport when the release finds a graveyard, `GraveyardRepopService.RepopAtGraveyard`) is not.

**Death recovery bounds are judged on attempts, not on world time alone.** `PlayerbotRecovery` gives a corpse run up for the spirit
healer when the walk stopped closing for `StuckMs` (10 s), faults a fallback that stalls the same way
(`playerbot-recovery-spirit-healer-failed`), and faults a body never released for `NoProgressMs` (60 s)
(`playerbot-recovery-stalled`). A bound now trips only once the recovery has also acted in more than `MinBoundThinks` (4) thinks
since the progress it measures from; a think refused for lack of action budget is no attempt. A bot is thought for only when the
shared budget and the 8 ms per-tick cap reach it (`PlayerbotThinkRotation`), while `PlayerbotMotion` carries the ghost on every tick,
so a think gap longer than a bound used to be judged a stall at the first think after it. Live 2026-10-10 (200+ bots, a boost storm
with world updates of up to 8.7 s): Graveweaver's ghost faulted with `playerbot-recovery-spirit-healer-failed` three times in 3.5
minutes, each time at its first think after a gap, and was disabled. At the ordinary 500 ms think interval a bound spans 20 or more
thinks, so nothing changes there (`PlayerbotRecoveryThinkGapTests`).

**An unsafe body is given up for the spirit healer at once.** A bot playing alone (the brain sets `PlayerbotRecovery.UnsafeBody`; party,
group and battleground bots keep their body run) asks at every think of a ghost whose body lies on its map whether the body is worth
reclaiming (`PlayerbotRecovery.JudgeBody`, in this order): `DiedHere`, the bot already died within `DeathLoopYards` (60) of the body
inside the death watch's window (600 s; `PlayerbotStallWatch.DeathsNear`, this death and an earlier one; the third is a death loop,
which takes the healer already); `TooStrong`, with the risk estimate on, a creature that would attack the revived bot and whose aggro
reach covers part of the reclaim disc is one the bot could not take even at full health (`PlayerbotRisk.TooStrongNear`: the creature
and its assistance weighed by `PlayerbotRiskModel.Assess` at the half health and mana a reclaim gives, strength only: no quest credit,
loot or danger memory; `Avoid`, not `Rest`); `OutsideHome`, the body lies outside the bot's home (`PlayerbotHomeArea.Contains`, the
zone clip included) and the spirit healer leaves the bot inside the home or nearer its centre than the body
(`PlayerbotRecovery.SpiritHealerLanding`: the graveyard of the body's position, where vmangos `SendSpiritResurrect` teleports the
revived bot, else the healer the ghost sees; a prediction through `GraveyardSelector.FindClosest`, which a battleground's own
graveyards, a run-time outdoor PvP link or `World:Death:GraveyardFallbackToDefaults` can only move), by at least
`OutsideHomeGainYards` (50) when it does not leave it inside the home. The body is given up only when a spirit healer can be had at
all, one in sight or a graveyard for the ghost's position (looked up once per body and 50-yard cell): with neither, the ghost would wait
for a healer until the fallback faulted (`playerbot-recovery-spirit-healer-failed`), and vmangos leaves a spirit with no linked
graveyard where it died, so it reclaims its body as before. The same gate holds for a death loop's request
(`PlayerbotRecovery.TakeSpiritHealer`, `SpiritHealerRequested`): with no healer to be had the looping bot keeps its corpse run instead
of faulting, and the loop's errand set-aside and the home return take it from there. A repeat death also sets aside the errand that led there, as a death loop
does. Any of them takes the spirit healer for this death (resurrection sickness and 25%
durability, vmangos NPCHandler.cpp:430-471) and logs `Playerbot X takes the spirit healer instead of its body at (...): reason (detail)`
at Information; the home return then takes the revived bot home as after any resurrection (Auto: a teleport when the way is far or
crosses a higher-level area). A first death in a safe spot inside the home is reclaimed as before, and so is a body round which only
creatures the bot can take stand. Live 2026-10-11 05:45-06:00 UTC: the Westfall-homed level-14 Wfbotaz died among Raven Hill's level 20+
skeletons and spiders, reclaimed its body there and died again, two death loops in fifteen minutes (`attackers=202/531/569`, then
`202/217/569`); the healer came only with the third death of a loop (`PlayerbotUnsafeBodyRecoveryTests`).

**Tightened (live 2026-10-11 08:04-08:10 UTC: 80 bodies given up in six minutes, 31 too strong, 10 died there, 39 outside the home).**
`DiedHere` counts only while a creature that would attack the revived bot (`PlayerbotRecovery.Threats`, or a hazard) still stands in
reach of the body; a cleared spot is reclaimed (the errand that led there is still set aside). `TooStrong` is strength, not worth: a
creature counts only when `Assess` avoids it and `PlayerbotRiskModel.BeyondStrength` holds (one that kills outright, an elite 3+ levels
above, or a risk at full health and mana above `Tolerance x 0.9`, the highest bar any pull is taken at). Before, the pull bar of a fight
that gives no quest credit or loot refused level 9-13 Defias and gnolls to level-14 bots (entries 95, 474, 478, 504, 1118 in the log).
`OutsideHome` counts only when the home return cannot teleport the revived bot home (`ReturnMode` `Walk`, or a return teleport less
than `TeleportCooldownMs` ago): otherwise the body is reclaimed and the Auto return walks or teleports the bot home for nothing. A
remaining source of over-firing is outside this change: the level-14 Westfall bots' `TooStrong` details imply an observed damage per
second (`PlayerbotFightTracker.ObservedDps`) of about 5-6 against the level estimate of 27.

**The healer's landing is judged too.** The spirit healer revives the bot at the graveyard of its body (vmangos `SendSpiritResurrect`),
which can be as unsafe as the body: Raven Hill's graveyard stands among its level-24 Skeletal Horrors. Before the body is given up (an
unsafe body or a death loop's request), the landing (`SpiritHealerLanding`) is asked of `PlayerbotRecovery.LandingUnsafe`
(`PlayerbotRisk.TooStrongAt`: a creature beyond the bot's strength whose aggro reach covers the landing); when it is unsafe and a spot
within the reclaim radius of the body is clear of every threat, the body is kept (`BodyKeptOverLanding`, logged once: `keeps its body
... the spirit healer would revive it among creatures too strong for it`) and the corpse run revives the bot on that spot. When the
healer is taken and revives the bot on an unsafe landing, the bot leaves in the same think, still out of combat (the activation is
handled synchronously; the creatures aggro only in their own update): a homed bot teleports home (`PlayerbotHomeTeleportReason.UnsafeLanding`,
past the 5-minute cooldown), an unhomed one (or one whose home teleport is refused) to its bind point (`TeleportToHomebind`, as the
tram's way out). Live: Wfbotfe (homed, 08:07-08:09) and Dawnrover (unhomed, death loops at 08:23:32 and 08:27:47) were revived at
that graveyard, attacked before their next think, and died there again and again; the home teleport was refused in combat.
The landing is judged where the resurrection sends the bot: when the body's graveyard differs from the ghost's, `SendSpiritResurrect`
teleports the revived bot there (`GraveyardRepopService.TeleportToCorpseGraveyard`), a near teleport still pending in that think, so
the escape judges `TeleportService.DestinationOf` and replaces that teleport (an urgent `TryTeleport` never counts the server's own
pending teleport as its escape). The creatures are those round the landing itself, seen by the bot or not
(`PlayerbotRecovery.ThreatsAround`). A death loop's request for the healer is never overridden by a kept body: the healer is the way out
of a body death loop and the escape covers its landing, so at most two bodies are kept per death-loop window. The keep-body answer is
kept per body for 2 s. The unhomed bind-point escape is not rate-limited: it happens at most once per resurrection, and a bot that
goes back to the same place has its errand set aside by the death watch.

**Why the spirit-healer step faulted.** Not a server refusal: in the world test that replays Wfbotfe's sequence (revived at the healer
among the skeletons, retreat, death, three times) the motion's loop watch (`PlayerbotMotion`, A, B, A within 30 s) counted the ghost's
walks to the same healer and the living bot's retreats between them as one errand going back and forth, refused the healer's place for
60 s, the ghost could plan no route to it, and the stuck bound (`StuckMs`, 10 s) faulted the recovery; live, the fault came 21 s after
the death loop. The first think of every death now forgets the loop watch (`PlayerbotMotion.ForgetLoops`). A refused
CMSG_SPIRIT_HEALER_ACTIVATE (the bot still dead after the synchronous handler) is logged at Information with the handler's rule
(`QuestNpcServices.SpiritHealerRefusal`: alive, NPC state not ready, not interactable with the reason from `InteractableNpc`, not a
ghost, a settlement pending), once per reason and death, and the fault itself is preceded by an Information line with the goal, its
distance, whether a route existed and whether the goal is refused after a movement loop.

**Waiting to recover without food.** After a retreat, or before a pull it is too hurt for, the bot waits for
`World:Playerbots:Risk:RecoverHealthPct` (goal `Rest`, `PlayerbotRisk.KeepWaiting`, at most `MaxWaitMs` 90 s). With food or drink it
eats or drinks; without, it now sits (`CMSG_STANDSTATECHANGE`; vmangos `Player::RegenerateHealth` gives half as much again per tick
when not standing) and stands up when the wait ends. Health or mana rising during a rest is progress for the stall watch
(`Fingerprint.RestMark`). A wait in which neither rose for `FruitlessWaitMs` (20 s, ten regeneration ticks) ends, and no new wait
begins for `MaxWaitMs` unless health or mana rises meanwhile: under resurrection sickness (spell 15007, every stat -75%) a warrior,
rogue, hunter or shaman regenerates no health out of combat at all, and the wait could only run out and be begun again by the next
pull verdict. Live 2026-10-10: Wfbotcx (level 14), revived by the Darkshire spirit healer after a death loop, stood there with
`stalled 120s: goal=Rest` (`PlayerbotRestStallTests`).

`World:Playerbots:MovementPackets` (default `true`, live through `.reload config`) chooses how
those moves reach the world. `true`: each one is a client MSG_MOVE_* packet, encoded and dispatched
through the opcode table to `MovementHandlers` like any client's. `false`: the server applies the
same move itself (`WorldSession.TryManagedMovement` -> `MovementHandlers.MoveManagedBot`): the
same admission (not while teleported or on a taxi, a valid block, the bot moves itself), the same
relocation with the locomotion observers and the same MSG_MOVE_* relay to observers, without the
dispatch or the packet encode/decode. Observers receive byte-identical streams in both modes
(`PlayerbotMovementPacketsTests`); `PlayerbotMotion` reads the option at every move, so a reload
switches running bots at their next move. Acknowledgements (speed, root, teleport, far transfer)
and `CMSG_AREATRIGGER` stay client packets in both modes.

In **scripted mode** an `IPlayerbotController` replaces the brain.
`ManagedPlayerbotFeature.StartScriptedAsync(idOrName, controller)` starts a bot that way;
`SetControllerAsync(botId, controller)` switches a running bot (null returns it to autonomous
mode). While scripted, the brain is never updated (no goals, no packets drained, no actions)
and the shared action budget does not apply; the controller's `Tick` runs on the world thread
every tick with a `PlayerbotControllerContext` (`TryAction`, `AcknowledgeServerOrders`).
Stopping a bot detaches its controller; `IsScripted(botId)` reports the mode. Switching back
to autonomous mode resumes the brain with whatever state it had before.

### Home area

A managed bot can be anchored to an area. Without one it follows its own goals anywhere on the map (its starting-zone quest log, trainers,
corpse runs, risk-weighted pulls); 200 bots teleported into Westfall spread over five zones within ten minutes. This is an ArcaneCore feature:
no reference server anchors a bot, so every number here is an ArcaneCore choice. Code: `Playerbots/PlayerbotHome.cs`, the brain's
`TryReturnHome`, `PlayerbotQuestGoals` and `PlayerbotWorldDestinations`; the return mode in `Playerbots/PlayerbotHomeOptions.cs`. Tests:
`PlayerbotHomeTests`, `PlayerbotHomeZoneTests`, `PlayerbotHomeTeleportTests`,
`ManagedPlayerbotHomeTests`, `ManagedPlayerbotHomeStoreTests`.

* **Setting it.** `.playerbot home $name|$prefix* <$tele|here> [#radius] [zone|nozone]` and `.playerbot home $name|$prefix* clear` (Administrator). A selector is one
  bot by name or id, or every bot (running or not) whose name starts with a prefix of 2 to 11 letters and one trailing `*` (`Wfbot*`), so a stray `a*`
  cannot anchor the fleet. `$tele` is a `game_tele` name (its map and position are the centre); `here` is each running bot's own position. The map must be
  one bots may enter (`World:Playerbots:AllowedMaps`). The reply is `accepted` at once and the result line (`N set ... M skipped ... K failed`) follows.
* **Radius.** In yards, two-dimensional distance, **50 to 5000, default 600** (`PlayerbotHomeBounds`). A value outside is refused before anything changes.
* **Zone clip (default on).** A circle cannot follow a zone border: homed at Sentinel Hill with radius 750 (or Westfall with 1200) it reached Raven Hill
  in Duskwood (creatures 202/213/539/565/569, level 20 and more), and level-14 bots died there again and again (QA death-loop findings, map 0 near
  -10650,450). So a home is also clipped to the **zone of its centre**: a point is inside only when it is inside the circle *and* in that zone. The zone
  is the AreaTable zone (the top-level parent area) at the position, from the terrain's area flag: `PlayerbotZoneLookup` answers exactly what
  `Map.GetZoneAndAreaId` answers (vmangos `TerrainInfo::GetZoneAndAreaId` through `AreaTable.GetZoneAndAreaId`, i.e.
  `TerrainManager::GetZoneAndAreaIdByAreaFlag`), with the flag-to-zone answer cached. The home's zone is taken from its centre when the brain first uses
  it (world thread), so it follows the terrain data, not a stored number. **Fail closed:** a position whose zone the terrain does not know is outside; a
  clipped home whose centre has no known zone contains nothing (every creature, quest and destination is rejected; logged once, `zone=unknown`), and
  `.playerbot home` refuses to set one (`... refused (no zone known ...; add nozone)`, or for a tele place a one-line refusal). `nozone` (before or after
  the radius) turns the clip off and gives the plain circle; `zone` states the default.
* **Storage.** `managed_playerbot_home` (characters schema 56), one row per bot, separate from the bot revision; the zone clip is the column `IgnoreZone`
  (characters schema 58, `ManagedPlayerbotHomeZoneDataModule`), stored inverted so the rows that existed before it read as **clipped**. A home that cannot
  be stored is not applied (fail closed) and is counted as failed. It is re-applied when the bot starts.
* **Staying inside.** "Inside" means inside the circle and, for a clipped home, in its zone. Goal selection ignores creatures outside the area (`IsSkipped`; a
  bot still fights back against whatever attacks it), new quests are taken only from a giver inside the area and only if every creature that ends the quest
  and every creature it asks to kill has a spawn inside (`QuestInside`), the destinations of the world walks (quest givers, enders, kill spawns) are the
  ones inside, and an exploring bot whose next leg would end out of the zone aims it at the centre instead.
* **Walking home.** A bot on the home map outside the radius, or inside it but out of a clipped home's zone (a bot that wandered over to Raven Hill), with
  no errand, drops its intent and walks to the centre through the ordinary route planner
  (`PlayerbotNavigation`), in route-sized legs with the same hazard guard and risk model as every other walk. **Hysteresis:** it starts at 1.0 of the
  radius and stops at **0.75** (`ReturnFraction`) and back in the zone, so it does not oscillate at the edge; the way back itself may cross other zones (it
  is never restricted). A clipped home whose zone is not known is walked by the circle alone. An exploring bot beyond **0.8** of the radius (`ExploreFraction`)
  aims its next leg at the centre instead of further out; when that leg is refused (on the motion's loop blacklist, through a hazard, no path) the
  next ones turn 25, 50 and 75 degrees either side of it (`PlayerbotBrain.InwardFanDegrees`, `InwardLeg`), instead of asking for the refused one again on
  every think. A turned leg is at most the bot's distance × cos of the turn long, so it always ends closer to the centre than the bot (inside the home,
  also in a small one); a turn whose leg would end out of a clipped home's zone is skipped for the next one (live 2026-10-11, bot QA #154: 74 of 204 stalls 460-480 yards from the Sentinel Hill centre). A stall reported by `PlayerbotStallWatch` sets the walk aside for 30 s at once (state
  `blocked`); the brain's own no-movement stalls (the bot stands still for the stuck time on a route) and failed route plans count instead, and the
  third in a row sets it aside. The goals carry on while it is set aside.
* **Errands override the walk home.** A vendor or trainer visit that has something useful to do (`PlayerbotTownGoals.HasCandidate`,
  `PlayerbotTrainerDestinations.HasCandidate`), a quest exchange already under way, and the turn-in of a completed quest (the way to its ender is never
  restricted) may take the bot anywhere, in any zone; the bot walks home afterwards. The zone clip does not change this: a capital trip for a trainer or a
  vendor is allowed as before (decided: these NPCs stand in towns, and starving a bot of training and repairs is worse than the walk). What the clip does
  keep closed is combat by choice out of the zone: while the bot is on the home map the grind filter rejects every creature outside the area wherever
  the bot is, so a bot on its way to Stormwind picks no fight on the road (it still defends itself). Combat, looting, rest, retreat and the ghost
  recovery run before the home check and are not touched; a dead bot recovers its corpse and walks home after the resurrection.
* **Return mode (`World:Playerbots:Home:ReturnMode`, live).** `Walk`, `Teleport` or `Auto` (default). Westfall-homed level-14 bots that had drifted
  to the Darkshire road in Duskwood (about -10703,-918) and to Loch Modan (about -5886,-2951) walked home across higher-level zones and died in death
  loops on the way (world.log 2026-10-11: `Wfbotfk ... death loop ... attackers=202/531/569`), until the operator sent them home by hand with
  `.playerbot send Wfbot* SentinelHill force`. **Walk** is the walk above, always (and a bot on another map is left `off-map`). **Teleport** makes every
  return a teleport to the centre. **Auto** teleports when the bot is on another map, farther than `TeleportDistanceYards` (default **1500**, 100..20000;
  the Darkshire road stray stood about 2000 yards from Sentinel Hill), when the straight line home crosses an area whose AreaTable level (`AreaLevel`, the 5875
  AreaTable.dbc exploration level of the area the terrain's area flag names, sampled every 20 yards, `PlayerbotAreaLevelLookup`) is more than
  `DangerLevelMargin` (default **5**, 0..60) above the bot's level (Raven Hill is a level-24 area 630 yards from Sentinel Hill: a level-14 bot
  teleports, a level-20 one walks; an area with no known level is never dangerous), or when its walk was set aside as blocked (failed plans, no
  progress, a stall); otherwise it walks. The rules are `PlayerbotHomeRules.Decide`; the level check is made once per return. The points of the line
  inside the home area itself are left out, and a level no higher than the area at the home's centre never counts (`PlayerbotHomeReturn.DangerLevelOnLine`):
  the operator homed the bot there, so a bot homed above its level does not teleport at every return. Indoors the level is the outdoor one: the
  terrain's area flag is the ADT cell's only (`TerrainInfo.GetAreaFlag`, vmangos `TerrainInfo::GetAreaFlag` without the WMO area info), so a cave or
  building sub-area with a higher level of its own reports the outdoor area's level and its danger can be under-reported. The teleport is the one
  the follower's catch-up (`PlayerbotFollower`, `World:Playerbots:Follow:CatchUpMode`) and `.playerbot send` use: the ordinary
  `TeleportService.TeleportTo` (vmangos `Player::TeleportTo`), its near or far acknowledgement answered by `PlayerbotMovementControl` as a client's,
  to the home's centre. **Never** in combat, in a duel, dead or as a ghost, in a group, on a battleground, to a home map `PlayerbotMapPolicy.Allows`
  refuses (`AllowedMaps`), or into a dungeon or raid the bot is not already inside: `.playerbot send` summons nobody into an instance without its
  group (`PlayerbotFleetCommands.SkipReason`, vmangos `HandleGroupgoCommand`, `LANG_CANNOT_SUMMON_TO_INST`) and a lone bot has none, so a home on an
  instance map is reached only by a near teleport inside it. At most **one return teleport per 5 minutes** per bot (`PlayerbotHomeReturn.TeleportCooldownMs`):
  a bot that is out again sooner has its return set aside until then, so a home it cannot stay in costs one teleport per 5 minutes, not one per
  think. A party AI, a bot-led group, the battlegrounds coordinator or a scripted controller drives
  the bot instead of the brain, so its home and the teleport are suspended then. A refused teleport is not retried for 30 s; on the home map the return
  is set aside (`blocked`) rather than walked, off the map the bot stays `off-map`. Each return teleport is logged at Information
  (`Playerbot <Name> teleports home to <place> (<reason>; <distance> yd) from (...) map M to (...) map H`).
* **Off-map policy.** In `Walk` mode a bot on another map than its home does not move itself: it is reported `off-map` and left alone, and an
  operator brings it back (`.playerbot send <Name> <place>`). In `Auto` and `Teleport` it is teleported home when the home map is allowed.
  While off-map the in-area filters are **off** (`PlayerbotBrain.SyncHomeFilters`): the circle has no meaning on another map, and applying it would skip
  every creature and take no quest, starving the bot. It behaves as a bot with no home (normal autonomous grinding, questing and exploring on the map
  it is on) and the filters apply again the moment it is on the home map.
* **Scripted and party bots.** A scripted controller, a party AI or a bot-led group drives the bot and ignores its home (shown as `suspended`).
* **Seeing it.** `.playerbot status` appends `home=inside:120`, `returning:810`, `off-map`, `blocked:810` (distance rounded to 10 yards) or
  `zone-unknown` (a zone-clipped home whose centre has no known zone, so it contains nothing; re-home the bot or set it with `nozone`), followed by
  `,teleports=N` once the bot has teleported home (`home=inside:0,teleports=1`); `.playerbot inspect` prints the place, map, centre, radius, zone
  (`zone=40`, `zone=off`, `zone=unknown`), exact distance, state, the return mode, the return teleports so far and why the last one was made
  (`... state=inside mode=auto teleports=1 last-teleport=distance`). The set reply names the zone of a tele place (`... radius 750, zone 40`) or
  `no zone clip`.
* **Provisioning.** `tools/ops/provision-bots.ps1 -Home` sets each bot's home to the place it is teleported to ([bot provisioning](../ops/bot-provisioning.md)).
* **Stalls outside the home.** A goal on which `PlayerbotSharedSetAsides.BotsToShare` (3) different bots stalled within 30 minutes is set aside for
  every bot (30 min, doubling to at most 2 h). Only the stalls of bots **inside their home area** (or with none) count
  (`PlayerbotBrain.GiveUpStalledGoal`, `StallOutsideHome`): a homed bot outside it (on another map, on its way back, on a turn-in or capital
  errand) stalls where its home does not keep it, which says nothing about the goal for the bots whose home it is. It still sets the goal aside
  for itself (10 min), and its stall line ends `it gives up that goal outside its home` (bot QA: cause `OutsideHome`, never published). The
  shared line is logged as soon as the set-aside is made (`ManagedPlayerbotFeature.LogStall`; before, only beside the bot's next new stall line,
  so one completed by a further give-up of an old stall waited, and was logged beside a stall on another goal) and names where the bots behind it
  stalled: `Playerbots: 3 bots stalled on quest 109 for 30 min; every bot sets it aside (stalls at x,y,z map M; ...)`. Live 2026-10-11 (build
  f5bc89a5, 200 bots homed at Sentinel Hill with the 550-yard circle, no zone clip, no return teleport): 46 of the 49 stalls behind the 50 shared
  set-aside lines were made outside the home, and quest 109, ended by Gryan Stoutmantle at Sentinel Hill, was set aside for every bot for up to two
  hours by bots stalled in Redridge and at Raven Hill (`PlayerbotSharedSetAsideTests`).
* **Known travel gaps (backlog).** Measured on the same run (`character_queststatus` of the 200 `Wfbot` bots, world.log stall lines):
  * *Turn-ins across the Deeprun Tram.* Dwarf and gnome bots homed in Westfall hold completed Dun Morogh, Ironforge and Loch Modan quests
    (65 bots complete-unrewarded on 400 "Tools for Steelgrill", 79 on 2160, 28 on 353, 19 on 2218, 16 on 1679, 9 on 1879 and 2997, 7 on 2039;
    the other way, quest 1338 "Stormpike's Order" ends in Stormwind for bots in Loch Modan). A completed quest's turn-in is never restricted by
    the home, so these bots walk from Westfall towards Ironforge-side enders: on foot that side is out of reach, they stop at the Westfall/Elwynn
    ridge (-9540,1015; -9488,820) or at the Stormwind tram entrance (-8285,554), stall, set the quest aside for 10 min, and try again. The tram
    is map 369, which an autonomous bot's area triggers walk through (`PlayerbotAreaTriggers`, not in `AllowedMaps`), and the brain has no
    transport step (riding the tram's GAMEOBJECT_TYPE_TRANSPORT, `GameObjectMapSystem.Elevators`). Two ways out, both open: (1) teach bots
    the tram (allow map 369 for that trigger pair, ride the car, leave by the far trigger), or (2) decide that a homed bot does not walk out of
    its home for a turn-in whose ender is not reachable (needs a reachability rule: the navigation mesh alone answers per tile; a zone
    connectivity graph would have to be sourced). Way (1) is done ([Travel by the Deeprun Tram](#travel-by-the-deeprun-tram)); bots still on
    foot there are logged `outside its home` and set nothing aside for other bots.
  * *Terrain stuck points on the way home.* Several bots stalled at the very same coordinates: Raven Hill in Duskwood (-10607.0,294.0,32;
    quests 783, 2158, 6181), the hill west of Lakeshire in Redridge (-9471.9,-1792.0,132; quests 106, 109), north Westfall (-9540.1,1015.0,52;
    quests 400, 2160, 2997). These are navigation obstacles, not quest faults; the zone clip and the return teleport bring homed bots back from
    the first two, the third is on the Westfall-to-Ironforge line above.

### Faults and quarantine

An exception out of a bot's update (brain, scripted controller or `PlayerbotMotion`) is an
**action fault**. It is logged with the whole exception (type, message and stack), the bot's
session closes and the next checkpoint (every 5 s) quarantines it: the character is saved and
the `managed_playerbot` row turns `Faulted` with the fault as `ErrorCode`
(`quarantined (fault 1/3): action: <message>`), but **keeps `DesiredEnabled`**. After
`FaultBackoffSeconds` (30; doubled for every further fault, at most an hour) the bot logs in
again, autonomous (a scenario controller that faulted is not reattached). The `MaxFaults`-th
fault (3) within `FaultWindowSeconds` (3600 s of world time) disables it for good:
`DesiredEnabled` off, `ErrorCode` `disabled after 3 faults: ...`, no retry. A failed retry
login counts as a fault, and so does a failed restore at startup (for example a bot saved on a
map outside `AllowedMaps`): before, that cleared `DesiredEnabled` too. A failed operator
`.playerbot start` is still reported and not retried. `.playerbot start` or `.playerbot stop` clears the quarantine and the
fault history, and wins over a retry already scheduled: the retry re-checks under the operation lock that the bot is still
desired and still in that quarantine, and otherwise ends (`quarantine-cleared`) without logging the bot in. The stored
`ErrorCode` (echoed by `.playerbot` status as `error=`) has control characters of the exception message replaced by spaces
and is cut to 128 UTF-16 units without splitting a surrogate pair. A world restart restores every desired bot, quarantined ones included (the
fault history is per process). Before 2026-10-07 one fault set `DesiredEnabled` off, so a
single transient bug removed a bot until an operator noticed:
`docs/integration/playerbot-faults-20261007.md`. Ordinary session closes that are not faults
(a GM kick) still stop the bot and clear `DesiredEnabled`, as before.

### Party bots (a real player's group)

`Playerbots/Party/` (vmangos `src/game/PlayerBots/PartyBotAI.cpp`; the player-facing actions of mangoszero's
`modules/Bots/playerbot/strategy/actions`). A player can invite a bot; it then follows, assists, obeys its master and goes
where the master goes.

- **Intake.** Every world tick for a grouped bot, and once per think interval for an autonomous bot in no group (a scripted
  controller drains its own queue), `PlayerbotPartyAI.Intake` drains only SMSG_GROUP_INVITE, SMSG_MESSAGECHAT,
  SMSG_LOOT_START_ROLL and SMSG_RESURRECT_REQUEST from the bot's capture queue (a filtered drain; an unfiltered one would steal
  other components' packets). The answers are a client's own replies and do not wait for the shared action budget. A pending
  invitation is also read from the group state, so one whose packet the 128-entry drop-oldest capture queue evicted is still
  answered; an evicted SMSG_LOOT_START_ROLL is not (that roll waits out its timer).
- **Invitations** are accepted (CMSG_GROUP_ACCEPT) or declined (CMSG_GROUP_DECLINE, which tells the inviter) by
  `World:Playerbots:Party:InvitePolicy`: `None` (only the `Allowlist`), `GuildOrFriends` (default: the allowlist, the bot's guild
  mates and players on the bot's own friend list) or `Anyone`. A player's own friend list does not count: anyone can put any bot
  on it with one CMSG_ADD_FRIEND, so it shows no consent. `.playerbot invite <bot>` (GameMaster; the
  vmangos `.partybot add` analogue) invites a running autonomous bot into the GM's group through the ordinary invite and makes it
  accept whatever its policy says; the invite keeps every ordinary rule (faction, full group, leader or assistant).
- **Master.** The group's leader when it is a real player online (a socket client, never a managed bot), otherwise the first
  real player online in member order (vmangos `GetPartyLeader`). While the bot has one, `ManagedPlayerbotFeature` ticks its
  party AI instead of `PlayerbotBrain` (a scripted controller still wins). Out of the group the brain drives it again, a fresh
  one (the old one's routes and targets belong to another place). When every real player of the group is offline or gone, the
  bot waits `MasterTimeoutSeconds` (60, 1..3600), then leaves the group (CMSG_GROUP_DISBAND, vmangos `requestRemoval`).
- **Out of combat** the bot follows at 2-5 yards at a random angle (`PlayerbotNavigation.TryPlan`/`TryAdvance`), eats and drinks
  through `PlayerbotConsumables` while its master is within 30 yards, and catches up with a master it cannot simply walk to as
  [Following and catch-up](#following-and-catch-up) describes: by default it teleports (vmangos `.goname`) to a master more than 100
  yards away or on another map, and to one it has no reasonable way to, through the teleport service the GM `.goname`/`.namego`
  commands use, so the map resolver's instance rules and the group's instance bind decide where it lands (into the master's
  instance). It lands on the master's spot, not 5 yards above as `.goname` puts a GM: it has no client to fall. A master on a map
  the bot may not be on (outside `AllowedMaps` and no dungeon or raid, `PlayerbotMapPolicy`), on a taxi flight (vmangos idles
  then), or in another instance of the bot's own map id (the service teleports within one map id by a near teleport, which keeps
  the bot's instance) is waited for, not followed. A refused teleport is retried after 10 s.
- **Combat** (vmangos `SelectAttackTarget`/`SelectPartyAttackTarget`): the target the master ordered, else the master's
  victim, else whoever attacks the bot, else whoever attacks another member within 50 yards; `PlayerbotCombatSpells`, then a
  chase to 4 yards and CMSG_ATTACKSWING.
- **Round-robin loot.** vmangos PartyBotAI.cpp:565-585 unassigns the bot's round-robin loot on every party kill
  (SMSG_PARTYKILLLOG, broadcast to the group there). ArcaneCore sends that packet to the killer alone, so at every think out of
  combat the bot looks over the corpses it can see (within 74 yards, the group reward distance) for loot it holds
  (`LootBag.Owner`), whoever made the kill and whatever the bot was doing (fighting another mob of the pack, passive, staying).
  It walks up, opens each and releases it untouched (CMSG_LOOT, CMSG_LOOT_RELEASE), and the loot service opens the leftovers to
  the group. A staying bot walks back to its place afterwards. A release the shared action budget held back is tried on the
  next think; a corpse it could not reach or open three times is left.
- **Loot rolls** are answered at once with `LootRoll` (`Pass` by default, or `Greed`; never need), so a roll never waits out its
  timer for a bot (mangoszero `LootRollAction`).
- **Dead:** a group member's resurrection is accepted (CMSG_RESURRECT_RESPONSE; a stranger's declined). With `AutoRevive`
  (default on) the bot revives in place at half health when vmangos `ShouldAutoRevive` allows it (a released ghost; or nobody
  fighting, no living healer class to resurrect it, and a living member within 15 yards), and otherwise waits up to 2 minutes
  (vmangos would wait for ever and never releases a party bot outside battlegrounds); then, or without `AutoRevive`, it
  releases and runs back to its body like the brain (`PlayerbotRecovery`), to the end: the released ghost is not revived at the
  graveyard.
- **Commands**, by whisper or party chat and only from the master, one word, any case: `follow`, `stay` (hold this place: no
  following, no teleport, fight back only what attacks the bot), `attack` (the master's current target), `stop`/`passive`
  (stop fighting, follow without attacking), `come` (walk to the master, then hold there), `status` (a whisper back: level,
  health %, mana % and the current activity) and `leave`. A command sent right after the bot joined counts even before the
  bot's first turn in the action budget. Each is acknowledged by whisper (CMSG_MESSAGECHAT in the bot's own
  language). With [bot chat](#bot-chat) on (the default) any other line is the chat's; with it off, a whispered word that is
  no command gets the command list and a whisper from anyone else gets one polite answer (once per sender a minute, at most 8
  senders a minute). Other bots' lines are always ignored.

`.playerbot inspect` prints `BOTINSPECT party=master:<name> mode:<follow|stay|passive>` (or `party=none`); while the party AI
drives a bot its goal is `Follow` or `Assist` (appended to the persisted `PlayerbotGoalKind`). A controller that takes over a
grouped bot (scripted mode) makes the party AI let go: no party goal, master or mode is reported while it drives, and when it
detaches a bot still grouped is engaged afresh. Brain bots acknowledge a
far teleport through `PlayerbotMovementControl.AcknowledgeTransfer`, pumped every tick from `PlayerbotMotion.Pump` (commit `c5c66cd9`),
so a cross-map summon or an instance entry no longer strands them; the party AI does the same when it follows its master into an instance.
The fleet commands are in [Bot commands](#bot-commands).

### Bot commands

`Playerbots/PlayerbotFleetCommands.cs` (selection and ring placement in `PlayerbotFleetSelection.cs` and
`PlayerbotFleetPlacement.cs`). Fleet control of the registered managed bots, under `.playerbot`. Every command works on the
bots in the service snapshot only, never on a real player.

| Command | Level | Effect |
|---|---|---|
| `.playerbot summon <sel> [#count] [force] [to $player]` | Administrator | Teleport running bots to you (or the named online player), in rings around the spot. With a count, the nearest bots on that player's map go first. |
| `.playerbot send <sel> <$tele\|here> [force] [to $player]` | Administrator | Teleport running bots to a `game_tele` place, or to your position (`here`, or the named player's), in rings. |
| `.playerbot follow <sel> [to $player]` | Administrator | The bots follow you (or the named player) without a party and fight back. |
| `.playerbot stay <sel> [to $player]` | Administrator | The bots hold position (they fight only what attacks them) and obey the player's whispered orders. |
| `.playerbot dismiss <sel>` | Administrator | End a follow or stay; the bots go back to autonomous play. |
| `.playerbot near [#radius]` | GameMaster | List the running bots within the radius (default 50, at most 500 yards) of you: level, class, health, state, goal, distance (at most 20 lines). |
| `.playerbot revive <sel>` | Administrator | Revive the dead bots among the selection, as `.revive` does. |
| `.playerbot start <sel> [force]`, `.playerbot stop <sel> [force]` | Administrator | Start or stop every registered bot the selector names; one summary comes back when the worker finishes. |

**Selector `<sel>`.** `all`, a name **prefix** with one trailing star (`Wfbot*`; letters only, a bare star or a star inside a
word is refused), a bot id or one character name. Matching is case-insensitive and by registered name; a selector that
matches nothing answers `No playerbot matches '<sel>'`. Any other argument shape shows the command's help.

**Bulk cap.** `World:Playerbots:MaxBulkCommand` (default 50, live; see [configuration](../reference/configuration.md)) is the
most bots one command moves or starts. A selection larger than the cap, with no smaller `#count`, is refused with
`give a count <= N or add 'force'`; `force` lifts the cap. `all` plus `force` is the explicit "everyone" form.

**Console.** The interactive server console ([server-console](server-console.md)) has no invoking player. `summon`, `send ...
here`, `follow`, `stay` and `near` mean "to me", so from the console they refuse unless `to $player` names an online player
(`near` takes no target and always refuses). `send $tele`, `dismiss`, `revive`, `start` and `stop` need no player.

**Instances.** A bot is moved into an instanceable map only when it is in the target player's group or already in that very
instance (vmangos `HandleGroupgoCommand`, `TeleportCommands.cpp:181-266`); otherwise it is skipped as `instance`. A bot
already on the same map id but in a different instance than the target is skipped as `other-instance` (a near teleport would
keep its own instance). Into a non-instance map the destination must pass `World:Playerbots:AllowedMaps`, else
`map-not-allowed`. Battlegrounds are refused for `send`.

**Far teleports.** A summon or send may cross maps (map 1 to map 0). An autonomous bot answers it through
`PlayerbotMovementControl.AcknowledgeTransfer`, pumped every tick from `PlayerbotMotion.Pump` (commit `c5c66cd9`): it sends the
MSG_MOVE_WORLDPORT_ACK a client would after its loading screen, so the bot resumes on the new map.

**Reply and skip reasons.** `Playerbot <verb>: matched=N <done>=M skipped=K (reason=count ...)`, then up to 15 lines
`<name>: <reason>` and `... and N more.`. Reasons:

| Reason | Meaning |
|---|---|
| `is-target` | The bot is the player the command is relative to. |
| `dead` | The bot is dead or a ghost (`revive` it first). |
| `scripted` | A scenario or controller drives the bot. |
| `teleporting` | The bot is already mid-teleport. |
| `instance` | Instance target, bot neither grouped with the target nor inside it. |
| `other-instance` | Same map id, a different instance than the target's. |
| `map-not-allowed` | The destination map is not in `AllowedMaps`. |
| `security` | The bot's account outranks the invoker (never from the console). |
| `not-running` | The registered bot has no live in-world session. |
| `not-managed` | The session is not a managed bot. |
| `no-free-spot` | No ring slot with ground and line of sight was left for it. |
| `teleport-refused` | The teleport service refused the move. |
| `in-player-group` | `follow` or `stay`: a real player's group already leads the bot. |
| `alive` | `revive`: nothing to do. |
| `not-leashed` | `dismiss`: the bot was not following or staying. |

`start` and `stop` list the operation's own failure code instead (for example `playerbot-capacity`) under `failed=`.

#### Following and catch-up

`Playerbots/Party/PlayerbotFollower.cs` (rules in `PlayerbotFollowRules.cs`, the drop in `PlayerbotFollowDrop.cs`), configured under
`World:Playerbots:Follow` (every key live through `.reload config`). The party AI drives it; it takes a session and a leader, so any
other follow of a managed bot can drive it the same way.

The live report behind it (2026-10-10, build a88f6135): the master dropped into the Moonbrook mine just short of the Deadmines portal
(landing at -11138.0, 1506.9, 24.4) and his bots, following, ran round the mine through its Defias and died or looped. On the real
mmaps the follow planner's way from the mine ledge 11.9 yards above him is 89.5 yards for 16.4 yards of straight distance, past an
elite Defias Conjurer; from the Moonbrook surface above it is 320.6 yards for 22.6; no off-mesh (drop) connection exists in the nine
navigation tiles round the mine. The 100-yard teleport never fired, so the bots walked; and a party bot whose master went into the
Deadmines waited outside, because the master's map had to be in `AllowedMaps` ([0, 1] on the live server).
`PlayerbotFollowDeadminesTests` replays both places.

Each think the follower first takes the reference decision (`PlayerbotParty.DecideFollow`, vmangos `PartyBotAI::UpdateAI`
:795-817 and :884-891): hold within 5 yards; wait for a master between maps, on a taxi flight, in another instance of the bot's map
id or on a map it may not be on; teleport to a master on another map or, in `Teleport` mode, more than `TeleportDistanceYards` (100,
vmangos `IsWithinDistInMap(pLeader, 100.0f)`) away; otherwise walk. A walking follower plans to the point beside its master and judges
every fresh plan (`PlayerbotFollowRules.Judge`). The way is **no way** when:

- the navigation mesh has no route (`NoRoute`);
- the master is within 50 yards across and 30 yards or more above or below (`Vertical`: mangoszero's playerbot
  `MovementAction::Follow`, `AiPlayerbot.SightDistance` 50 and `SpellDistance` 30, teleports then);
- the route passes inside the aggro radius of a hostile `DangerLevelMargin` (3) levels or more above the bot, or of an elite (rank 1
  to 3) at or above its level (`Danger`; the radius is the server's own, `PlayerbotRecovery.Threats`);
- the route is longer than `MaxPathRatio` (3) times the straight distance and more than `MinDetourYards` (40) longer than it
  (`Detour`);
- the follower made no progress towards a master standing still for `NoProgressSeconds` (8) twice in a row (`Stalled`). The first
  such window re-plans from where the bot stands (and forgives the motion's loop blacklist round the master); progress is the way
  left shrinking by 2 yards, and a master who moves more than 5 yards starts a new window, so trailing a running master never
  stalls.

A good way is walked. Otherwise a **drop** is taken in every mode when the master is at least 3 yards below and within 40 yards
across (`PlayerbotFollowDrop`): the floor is followed towards him (and in a fan up to 90 degrees either side) to its edge; past it the
floor beneath must lie at least 3 yards lower, the way over the edge and the column down must be clear, the vmangos fall damage
(`Player::HandleFall`: nothing under 14.57 yards; gravity `Movement::gravity`, vmangos `spline/util.cpp:24`) must cost less than `MaxFallHealthPercent` (50) of the bot's current health, and
the floor it lands on must lead to the master by the mesh within twice the straight distance plus 10 yards. The bot walks to the edge,
reports the step off it as a client does (MSG_MOVE_HEARTBEAT with MOVEFLAG_FALLINGFAR one step out) and, the free-fall time later
(vmangos gravity 19.29 yd/s²), MSG_MOVE_FALL_LAND with that fall time: the server applies the fall damage. In the Deadmines it drops
12.4 yards from the ledge (no damage) and stands beside its master 2.2 s after he landed. A fall always ends on the ground or is
forgotten: an order that stops the follow mid-air (stay, a fight, leaving the group) lands it first; a bot that dies or is
teleported away mid-air is not landed afterwards. A drop searched for in vain is searched for again only after 2 s. Then by
`CatchUpMode`:

- `Teleport` (default, the reference behaviour: vmangos PartyBotAI and mangoszero's playerbot both teleport a follower): teleport for
  any of the reasons above. From the Moonbrook surface the follower teleports at once instead of running the 320-yard mine.
- `FightThrough`: never teleport, on the bot's map or to another: walk the way it has (a partial way towards the master when there is
  no route), drop where a drop leads down, engage the hostiles whose aggro reaches the next 40 yards of the way (after the ordinary
  targets: the ordered one, the master's, the bot's and the group's attackers; never when passive), and, with the master on another
  map, walk into the area trigger on the bot's map that leads there (a dungeon entrance or exit; the bot may take the triggers that
  lead to its master's map, and only those, whatever `AllowedMaps` says); with no such trigger it waits. A blocker the bot failed
  to reach three times is left alone. Stalls re-plan for ever. A follower that keeps dying stays a corpse-run
  follower.
- `Auto`: teleport only for `NoRoute` or `Stalled` (and to another map); otherwise as `FightThrough`.

A catch-up teleport needs `World:Playerbots:Party:TeleportToLeader` (off: never), a server-owned session with no teleport pending (the client half
acknowledges near and far teleports every tick: `PlayerbotMovementControl.Update`, `PlayerbotMotion.Pump`), a master on the ground
(not in the air), the bot not duelling (vmangos `PartyBotAI.cpp:808`, `!IsInDuel()`), and the bot out of combat (vmangos teleports only inside its `!IsInCombat()` block) unless its master left the
fight: out of combat himself and farther than `LeaderCombatRangeYards` (50, vmangos' party assist range) or on another map. The
master's map counts as one the bot may be on when `AllowedMaps` lists it (an empty list allows every map) or when it is a dungeon or
raid (`PlayerbotMapPolicy`; the login gate lets a bot stay in the instance it is in). Each catch-up teleport and drop is logged at
Information by `PlayerbotFollower` with its reason and the measured way.

| Key | Default | Range | Meaning |
|---|---|---|---|
| `CatchUpMode` | `Teleport` | `Teleport`, `FightThrough`, `Auto` | How a follower catches up (above). |
| `TeleportDistanceYards` | 100 | 10..1000 | `Teleport` mode: a master farther away on the bot's map is teleported to (vmangos). |
| `MaxPathRatio` | 3 | 1.5..20 | A way longer than this times the straight distance (and `MinDetourYards` longer) is a detour. |
| `MinDetourYards` | 40 | 0..500 | A way at most this much longer than the straight distance is always walked. |
| `DangerLevelMargin` | 3 | 0..60 | A hostile this many levels above the bot (or an elite at its level) on the way makes it no way. |
| `LeaderCombatRangeYards` | 50 | 0..1000 | A bot in combat teleports only to a master out of combat and farther than this. |
| `NoProgressSeconds` | 8 | 2..120 | The no-progress window of the loop detection. |
| `MaxFallHealthPercent` | 50 | 0..99 | A drop is taken when the fall costs less than this % of the bot's health (0: only harmless falls). |

### Bot chat

`Playerbots/Chat/`, configured under `World:Playerbots:Chat` (every key live through `.reload config`). Bots answer players who
talk to them, in character, from their real state. It is on by default with only the **built-in** provider: no network, no key,
no cost. Language-model providers are opt-in additions in front of it.

- **Triggers.** A whisper to the bot; a party (or raid) line that names the bot as a whole word ("Bob, where are you?"); a
  `/say` line within hearing that names it, from a player of the bot's own faction. `Channels` (`Whisper, Party, Say` by default)
  narrows this. Lines from other bots and from the bot itself are never answered. The one-word party commands (`follow`,
  `stay`, ...) keep their meaning and are never sent to a provider.
- **Order.** `PlayerbotPartyAI` builds the bot's facts on the world thread (name, race, class, level, zone and subzone from the
  area table, the brain's or party AI's goal and the current quest's title, the party master, and recent events seen between
  looks: a level gained, a death, a return to life, money picked up) and hands the line to `PlayerbotChat.TryAsk`: cheap checks
  and a bounded queue (`MaxQueuedRequests`, 16), never I/O. Two background workers try the providers in order and put the reply
  on a queue that `ManagedPlayerbotFeature` drains on the world thread; the bot says it through its own CMSG_MESSAGECHAT (a
  whisper back, the party line, or `/say`), so observers get the ordinary SMSG_MESSAGECHAT. A line taken by the chat but not
  answered (the cooldown, every cap spent, a full queue, every provider failing) gets no reply: the fixed replies above apply
  only with chat off or on a channel it does not answer.
- **Failover.** A provider is skipped while its key variable is unset, while it cools down, past its `MaxRepliesPerHour`, or,
  if priced, once the day's spend estimate reaches `MaxDailySpendUsd`. A failure moves on to the next provider: a 429 waits out
  its `retry-after` (at most 10 minutes), a 5xx/529, timeout (`TimeoutSeconds`, 10) or connection failure backs off 15 s
  doubling to 5 minutes, a 401/403 waits 10 minutes, another 4xx (a bad model id) 5 minutes; a refusal or an unreadable answer
  just moves on. The built-in provider is always last, so a bot whose model providers all fail still answers. No failure reaches
  the bot's behaviour: a reply that cannot be said is dropped and logged, never a fault.
- **Limits.** `PerPlayerCooldownSeconds` (8): one reply per player across all bots in that time. `MaxRepliesPerHour` per provider
  (120). `MemoryExchanges` (4): the earlier exchanges with the same player sent with a model request (at most 512 conversations
  are kept). Replies are one line of at most 255 characters with `|` (the client's link and colour escape) replaced.
- **Orders in plain language** (`NaturalLanguageCommands`, on): from the master only, "follow me", "wait here", "attack my
  target", "stop attacking", "come here" run the same party command as the one-word form (acknowledged the same way). A model
  provider returns `{"reply", "intent"}` JSON whose intent is one of `follow`, `stay`, `attack`, `stop`, `come` or `none`;
  anyone else's order gets a polite no.
- **GM.** `.playerbot chat status` (GameMaster): on/off, channels, queued, answered, unanswered, the spend estimate and cap, and
  per provider its kind, model, key variable and whether it is set (`key=ANTHROPIC_API_KEY:present`), replies this hour against
  the cap, errors, the last error class (`rate-limited`, `unavailable`, `unauthorized`, `rejected`, `refused`, `timeout`,
  `network`, `empty-reply`, `moderation-failed`) and the remaining cooldown, then a `Bot chat safety:` line (below).
  `.playerbot chat flags [player]` and `.playerbot chat pardon <player>` (GameMaster) are described under Safety and provider
  policies.

**The built-in provider** (`PlayerbotBuiltinChat`, templates in `PlayerbotChatTemplates`) reads the line's intent with a few
patterns, in this order: severe abuse (ignored), an insult (a short brush-off), "are you a bot?" (the honest answer: a bot run
by this server), an order, help, grouping ("want to group?": a yes when the bot's `InvitePolicy` would accept the player's
invitation, otherwise a polite no; already grouped says so), what it is doing or where it is going (its goal, quest title and
zone), where it is (zone and subzone, or that it does not know), its level, race and class, thanks, goodbye, a greeting, and
otherwise a short in-character line. Each answer has several phrasings plus race, class and level variants (`greeting.race.Orc`,
`who.class.Mage`, `who.level.novice` below 10, `who.level.veteran` at 60); a phrasing whose placeholder has no value is
skipped, and the same line is never said twice in a row to the same player. It follows the same triggers, cooldown and hourly
cap as the models. To add a line, add a string to the table; to add a variant, add a key. Lines say only what the bot knows of
itself and plain game mechanics, never invented lore.

**Model providers** (`Providers`, at most 8, tried in order; a listed `Builtin` entry must be last and sets its own cap):

| Key | Meaning |
|---|---|
| `Kind` | `Anthropic` (Messages API: `POST {BaseUrl}/v1/messages`, headers `x-api-key` and `anthropic-version: 2023-06-01`), `OpenAICompatible` (`POST {BaseUrl}/chat/completions`, `Authorization: Bearer` when a key is set: OpenAI, OpenRouter, Ollama, LM Studio), or `Builtin`. |
| `BaseUrl` | Anthropic: the origin (empty = `https://api.anthropic.com`). OpenAICompatible: the API base with its version (`https://api.openai.com/v1`, `https://openrouter.ai/api/v1`, `http://localhost:11434/v1`). A key is never sent over plain `http` to a host other than this machine (the configuration is refused). |
| `Model` | Empty = `claude-haiku-4-5` for Anthropic; required for OpenAICompatible. |
| `ApiKeyEnvironmentVariable` | The variable that holds the key. Unset: `ANTHROPIC_API_KEY` for Anthropic, none otherwise. `""`: a keyless local server. Keys are read only from the environment, at each request: never from configuration, never logged or shown. |
| `Headers` | Extra headers, e.g. OpenRouter's `HTTP-Referer` and `X-Title`; they cannot replace the key or version headers. |
| `MaxRepliesPerHour` | 1..100000 (120). |
| `MaxTokens` | The reply limit, 16..1024 (150). |
| `TokenLimitParameter` | OpenAICompatible: `max_tokens` (default) or `max_completion_tokens` (newer OpenAI models). |
| `InputUsdPerMillionTokens`, `OutputUsdPerMillionTokens` | Prices for the spend estimate. Unset: the built-in Anthropic price of a known model (Claude Haiku 4.5 $1/$5), otherwise unpriced. Set 0 for a local model. |
| `Moderation`, `ModerationBaseUrl`, `ModerationModel`, `ModerationApiKeyEnvironmentVariable` | The optional moderation step before a line is sent to this provider: `None` (default), `Endpoint` or `Classify` (see Safety and provider policies). |

The prompt's system part comes in two pieces: the rules (1.12 setting, short in-game chat style, nothing outside the game, no
claim of being an AI unless asked directly, a brush-off for abuse, the JSON answer format), identical for every bot and request,
then the bot's own facts. Anthropic gets the rules as a separate system block with `cache_control`; OpenAI-compatible endpoints
get one system message that starts with them, so providers that cache prompt prefixes can. Claude Haiku 4.5 caches only a
prefix of 4096 tokens or more, and the rules are far shorter, so in practice nothing is cached there today; the breakpoint costs
nothing.

A local model first, the cloud as a fallback, the templates last (a local model costs nothing; `OPENROUTER_API_KEY` set in the
server's environment; the OpenRouter prices are an example, check the provider's current price):

```json
"Chat": {
  "Providers": [
    { "Kind": "OpenAICompatible", "BaseUrl": "http://localhost:11434/v1", "Model": "qwen3:4b",
      "ApiKeyEnvironmentVariable": "", "InputUsdPerMillionTokens": 0, "OutputUsdPerMillionTokens": 0, "MaxRepliesPerHour": 1000 },
    { "Kind": "OpenAICompatible", "BaseUrl": "https://openrouter.ai/api/v1", "Model": "openai/gpt-4o-mini",
      "ApiKeyEnvironmentVariable": "OPENROUTER_API_KEY", "InputUsdPerMillionTokens": 0.15, "OutputUsdPerMillionTokens": 0.6,
      "Headers": { "HTTP-Referer": "https://github.com/Krilliac/ArcaneCore", "X-Title": "ArcaneCore" } },
    { "Kind": "Anthropic", "MaxRepliesPerHour": 60 }
  ]
}
```

**Cost control.** Every model reply is one request of roughly 400-800 input tokens (the rules, the facts, up to four remembered
exchanges) and at most `MaxTokens` (150) output tokens. On Claude Haiku 4.5 ($1 input, $5 output per million tokens) that is
about $0.0015 a reply at most, so the default cap of 120 replies an hour costs at most about $0.18 an hour per provider, and the
default `MaxDailySpendUsd` of $1 stops the priced providers after roughly 650 replies in a UTC day. The estimate uses the token
counts each response reports (cache writes at 1.25x and reads at 0.1x the input price) and is an estimate, not a bill; a provider
without a known price is held back by its `MaxRepliesPerHour` alone, so give OpenRouter and OpenAI entries their prices. The
per-player cooldown keeps one player from spending the budget, the bounded queue drops rather than piles up, and the built-in
templates answer once the money or the providers run out. Tests never touch the network: `IBotChatClient` is faked, and the four
live checks in `PlayerbotChatLiveTests` (one tiny request each to Anthropic, OpenAI, OpenRouter and a local server) run only
with `ARCANECORE_TEST_LIVE_LLM=1` and that provider's key or `ARCANECORE_TEST_LOCAL_LLM_URL` set.

#### Safety and provider policies

`Playerbots/Chat/PlayerbotChatSafety*.cs`, configured under `World:Playerbots:Chat:Safety` (every key live through `.reload config`,
range-checked; see the configuration reference). A public server that sends players' lines to an external model provider is
responsible for what it sends: Anthropic, OpenAI and OpenRouter each have usage policies that apply to the requests made with the
server's key, whoever typed the words. These safeguards keep the obvious violations away from the provider and limit players who
keep trying; they reduce the risk, they do not remove it, and they are no substitute for reading the policies of the providers you
configure. Nothing here applies to the built-in provider, which sends nothing anywhere.

- **Screening before a provider sees a line** (`Enabled`, on). Every line is screened on the chat worker before the first model
  provider is tried:
  1. the **local filter** (`PlayerbotChatSafetyFilter`), by category (`Categories`, all by default): `SexualMinors`, `SelfHarm`,
     `Threats` (real-world: "I know where you live", doxxing, swatting), `Hate` (slurs, calls for violence against protected
     groups), `Harassment` ("kys"), `IllegalGoods` (making explosives, buying hard drugs) and `PersonalData` (email addresses,
     phone numbers, street addresses). The term and pattern lists ship in `PlayerbotChatSafetyTerms.json` (an embedded resource);
     `TermsFile` names an operator file of the same shape that is added to them (or replaces them, `ReplaceDefaultTerms`) and is
     read again within 10 seconds of a change. The shipped list is deliberately conservative: it aims at what provider policies
     forbid, not at rudeness ("noob" and "idiot" stay the built-in brush-off's business), and stays clear of words common in WoW
     chat ("kill", "die", "bomb", "gun", "naked", a "suicide pull"). Matching folds case, accents, leetspeak (`k1ll y0urs3lf`),
     spaced-out letters (`k y s`) and repeated letters (`kiiill`); terms match whole words, and a trailing `*` matches longer words.
     Patterns are compiled with `RegexOptions.NonBacktracking` (matching time linear in the line, whatever the pattern: an
     operator's pattern with a back-reference or lookaround is skipped with a warning) and a 100 ms timeout; a line the filter
     cannot judge in time goes to no model (the built-in provider answers) but costs no strike. The timeout is wall-clock time, so a
     process pause during a match (a garbage collection, a starved CPU) can trip it on a harmless line. A pattern's first match is
     the slow one (its matcher is built then), so every pattern is run once on the thread pool when the filter is loaded, at start
     and after a reload, and a line waits for that warm-up rather than paying it. A timed-out screening is counted
     (`timed-out=` in the status, metric `arcanecore_playerbot_chat_screening_timeouts_total{kind}`) and logged as a warning at
     most once a minute, naming the pattern (`Harassment pattern #2`, `personal data (phone)`), never the line.
  2. each provider's optional **moderation step** (`Moderation` on the provider entry, `None` by default): `Endpoint` posts the
     line to an OpenAI-compatible `/moderations` endpoint (`ModerationBaseUrl`, default the provider's own `BaseUrl`;
     `ModerationModel`, default `omni-moderation-latest`; `ModerationApiKeyEnvironmentVariable`, default the provider's key), so an
     Anthropic entry can use OpenAI's moderation endpoint; `Classify` sends the provider a one-word SAFE/UNSAFE classification
     request (at most 16 output tokens, counted in the spend estimate) before the reply request. A flag keeps the line from every
     provider; a failed step skips that provider (`moderation-failed`) and the next one is tried, so a line is never sent
     unchecked.

  A flagged line reaches no model provider. `OnFlagged` (`BuiltinReply`) answers it with the built-in brush-off (for `SelfHarm`, a
  short line suggesting a trusted person or a local crisis line; for `PersonalData`, a caution), or nothing (`Ignore`). It is not
  remembered either, so it is never sent later as conversation memory.
- **Screening what a model says** (`ScreenOutput`, on). A model's reply is run through the same local filter before the bot says
  it; a hit is never said: the built-in provider answers the player's line instead (`OnOutputFlagged`: `BuiltinReply`) or nothing
  (`Drop`). The flag is recorded with the reply's excerpt and is no strike against the player. A reply the filter cannot judge in
  time fails closed: it is never said either and takes the same path (`OnOutputFlagged`), with no flag record (nothing was found,
  even when a personal-data pattern ran out of time, which on a player's line counts as personal data) and no strike; it is
  counted and warned as an `output` timeout. If the filter's warm-up meets an unexpected error, one warning names the error's
  type and lines are screened without it.
- **Flags, strikes and cut-off.** Every flag is kept in an in-memory ring of `FlagLogSize` (200) records and logged as a warning:
  the character's name and GUID, the account id, the bot, the category, the source (local filter, moderation or output
  screening), the time, and, while `StoreExcerpt` is on, at most 60 characters of the line with emails, phone numbers and addresses
  replaced. There is no persistent flag table (that would need a schema change): the log warnings are the durable record, and the
  ring, strikes, cut-offs and players' choices are lost at a restart. A flag of `SexualMinors`, `Threats`, `Hate`, `Harassment`,
  `IllegalGoods` or the moderation step is a strike (`SelfHarm` and `PersonalData` are not); strikes older than
  `StrikeWindowMinutes` (60) fall away. `StrikesBeforeCutoff` (3) strikes within the window cut the player off from the model
  providers for `CutoffMinutes` (60): built-in replies only. With `AutoMute` (off) the cut-off also mutes the player's chat for
  `AutoMuteMinutes` (30) through the account mute of `.mute` (persisted, set by "Bot chat safety" at GameMaster level, lifted by
  `.unmute`; a longer mute already in force is kept).
- **GM.** `.playerbot chat flags [player]` lists the newest 15 flag records (one player's when named, after a line with their
  strikes, remaining cut-off and AI choice); `.playerbot chat pardon <player>` clears a player's strikes and cut-off (not a mute).
  `.playerbot chat status` ends with `Bot chat safety: screening=on screened=.. flagged=.. moderation-flagged=..
  output-flagged=.. builtin-only=.. timed-out=.. cut-offs=.. cut-off-now=.. auto-mutes=.. opted-out=.. opted-in=.. disclosed=..` and the
  filter's size.
- **Disclosure and choice.** While a model provider is configured, a player who talks to a bot gets, once per login and before
  the first reply, a system message: `DisclosureText` ("Bot replies on this server may be written by an AI service, which
  receives what you say to bots.") plus how to opt out (`Disclosure`, on). With `AllowOptOut` (on), whispering any bot `ai off`
  limits that player to built-in replies, `ai on` undoes it and `ai` tells them where they stand; these whispers are answered by
  the safety itself and never sent to a provider. `RequireOptIn` (off) turns it around: the model providers answer only players
  who whispered `ai on`, and the disclosure says so. A player's choice lasts until the server restarts.
- **What is sent.** A model request carries the bot's facts, the bounded memory (`MemoryExchanges`) and the current line, with
  players named only by character name: never an account name, an address or a GUID. With `StripPersonalData` (on), email
  addresses, phone numbers and street addresses in the line and the memory are replaced by `[email]`, `[phone]` and `[address]`
  (this matters when `PersonalData` is left out of `Categories`; otherwise such a line is flagged before it gets that far). If
  one of those patterns runs out of time, the whole text is sent as `[removed]` (fails closed), counted and warned as a `strip`
  timeout. The excerpt of a flag record is stripped the same way, and its timeouts count as `strip` too.

**Operator responsibilities.** Read and follow the usage policies of every provider you configure, and keep the server's own
rules in line with them. Keep `Enabled` on whenever a model provider is configured (the start log warns when it is off). Review
`.playerbot chat flags` and the log warnings, extend the list through `TermsFile` for what your players actually say, and consider
a provider's moderation step on a busy public server. Tell your players, in your server's own terms, that bot chat may be
processed by a third-party AI service. The screening is a set of word lists and patterns: it will miss some lines and flag a few
harmless ones, and it says nothing about what a provider will accept.

## Scenario harness

Namespace `ArcaneCore.World.Playerbots.Scenarios`. A scenario is an `IPlayerbotScenario`
(`Name`, `Description`, `RunAsync(ScenarioContext)`) that logs managed bots in scripted mode
and drives them step by step against the real world handlers, asserting server state.
`ScenarioRunner.RunAsync(scenario, bots, world, services, clock, options)` runs it and returns
a `ScenarioReport`; afterwards it stops (logs out and saves) the bots, or returns them to
autonomous mode (`ScenarioRunOptions.StopBotsAfterRun`).

**Bots.** `ScenarioContext.LoginAsync(name, race, class)` reuses the bot of that name or
creates one, and starts it scripted. `ScenarioBot` is the controller: it records every packet
it sends and every packet its session captures (`ScenarioPacketLog`, run-wide sequence
numbers; the tap sits before the session's bounded drain queue, so bursts cannot evict a
reply) and, by default, acknowledges server teleports and movement orders like a client.

**Typed client actions** (`ScenarioBot`; each runs one CMSG through its handler and the bool
is transport admission only, outcomes come from replies or state): `TargetAsync`,
`AttackAsync` (selection + swing), `StopAttackAsync`, `CastAsync`; `LootAsync`,
`LootMoneyAsync`, `LootItemAsync`, `ReleaseLootAsync`, `UseGameObjectAsync`; `InviteAsync`,
`AcceptInviteAsync`, `DeclineInviteAsync`, `LeaveGroupAsync`, `SetLootMethodAsync`;
`InitiateTradeAsync`, `BeginTradeAsync`, `SetTradeItemAsync` (item GUID; bag and slot are
resolved), `SetTradeGoldAsync`, `AcceptTradeAsync`, `CancelTradeAsync`; `SendMailAsync`,
`GetMailListAsync`, `TakeMailItemAsync`, `TakeMailMoneyAsync`; `RequestDuelAsync` (spell 7266),
`AcceptDuelAsync`, `CancelDuelAsync`; `SayAsync`, `PartyAsync`, `WhisperAsync`, `ChatAsync`
(in the bot's team language: the server refuses Universal outside AFK/DND); `QuestHelloAsync`,
`AcceptQuestAsync`, `CompleteQuestAsync`, `RequestQuestRewardAsync`, `ChooseQuestRewardAsync`;
`AreaTriggerAsync` (instance entry: the far teleport is then acknowledged automatically);
`SendAsync(opcode, payload)` for anything else. Payload layouts are in `ScenarioPackets` and
follow the server's own handler parsing. The MockClient keeps its own independent encodings
on purpose (it is a second oracle), so the builders are not shared with it.
Ships (`ScenarioTransports`, docs/areas/transports.md): `context.ShipAsync(entry)` finds a route's ship,
`BoardAsync(ship, x, y, z)` sends a heartbeat standing on it at that offset, `LeaveShipAsync()` one without it,
`TimeSkippedAsync(ms)` a CMSG_MOVE_TIME_SKIPPED; `ScenarioTransports.TransferPending` and `NewWorld` decode the
map-change packets.

**Typed decoders** (`ScenarioDecoders`, mirroring the server writers): group list, party
command result, group invite, trade status, mail result and mail-list count, duel requested,
duel complete, duel winner, loot response, loot-money share, attacker state update, spell go,
cast result, spell failure, chat message, XP gain, quest kill update and quest complete. The proc engine's packets have their own
decoders (`ScenarioProcDecoders`: SMSG_SPELLDAMAGESHIELD, SMSG_PROCRESIST), and so have the class scripts' (`ScenarioClassDecoders`:
SMSG_SPELLNONMELEEDAMAGELOG, SMSG_PERIODICAURALOG).
`ScenarioBot.WaitForPacketAsync(opcode, decoder, match, since)` waits for a decoded reply
received after a `Mark()`.

**Setup helpers** (`ScenarioContext`, world thread, harness only): `PlaceAsync` (an ordinary
teleport, waited until acknowledged and arrived), `PlaceFacingAsync` (two bots face to face,
two yards apart), `GiveItemAsync`, `GiveMoneyAsync`, `LearnSpellAsync`, `SetHealthAsync`. They
exist only on the harness object, which only tests and the Administrator-only, config-gated
scenario command construct; no opcode or player command reaches them.

**Steps, waits, assertions, report.** `StepAsync(name, body)` records each step's wall and
game time; the first failing step ends the run. `WaitUntilAsync(what, condition, timeout)`
evaluates the condition on the world thread and is always bounded. `Expect`, `ExpectEqual`,
`ExpectAsync(bot, fact)`, `ExpectMoneyAsync`, `ExpectItemCountAsync`, `ExpectGroupAsync` (server
roster) and `QuestStateAsync` assert server state. `ScenarioReport` lists the steps (ok/FAIL,
wall ms, game ms) and, on failure, the failing step, the message and each bot's last relevant
packets (movement and object-update noise filtered).

**Deterministic clock.** `WorldRuntime.UseManualClock(stepMs = 50)` is a code-only test seam
(chosen before `Start`, never configuration). Game time (`NowMs`, `Uptime`, tick diffs) then
advances only through `AdvanceClockAsync(ms)` or `AdvanceClockUntilAsync(max, condition)`, which
run ticks of at most the step back to back and check the condition after every tick. Posted
commands keep running every tick interval with a zero diff, so sessions and `InvokeAsync` work
while the simulation stands still. `ScenarioClock.Manual(world, time)` drives it; a
`ScenarioTimeProvider` registered as the host's `TimeProvider` follows game time tick by tick
(mail delay, trade anti-scam window, duel countdown) and can jump ahead (`Advance`). On the
manual clock a wait's timeout is game time, after which it keeps polling without advancing until
the same timeout has also passed in wall time (and at least `ManualWallGrace`, 3 s, after the
game budget ran out): the game budget burns in well under a second, while asynchronous I/O such
as a database commit runs on real time and can take seconds on a loaded machine. Code that reads
`DateTime` or `Stopwatch` directly, rather than the world clock or `TimeProvider`, does not
follow the manual clock. `ScenarioClock.Real` (live server) polls in wall time.
A production wall-clock budget shorter than that wait defeats it: the quest reward, economy and
dungeon chest loot settlements give up after 5 s and answer with their failure reply (a loaded
SQLite file made the AV scraps turn-in and an auction cancel miss it), so `ScenarioTestWorld` raises
all three settlement budgets to its 30 s step timeout.

**Built-in scenarios** (`PlayerbotScenarioCatalog`; bots `Scnalpha` and `Scnbeta`, created on
first use): `smoke` (login, hear own /say), `group-chat` (invite, accept, both group lists and
the server roster, party chat, leave), `trade` (Linen Cloth 2589 for 75 copper through the
trade window; both inventories and purses), `duel` (spell 7266, accept, countdown, melee to the
1-health finish; SMSG_DUEL_WINNER checked against server state). After them come the other public
scenarios this assembly ships (`PlayerbotScenarioCatalog.Shipped`, discovered: `dungeon`, `ship`, `wsg`), then
`IPlayerbotScenario` services registered in DI; a name belongs to its first entry. Shipped content
scenarios need content a live world may not have and then fail at a named step. `ScenarioSteps` holds reusable blocks (form a group, open and
accept a trade, leave earlier groups).

**Battlegrounds** (`ScenarioBattlegrounds`, kept out of the shared harness files): bot actions `BattlemasterHelloAsync`,
`JoinBattlegroundAsync` (CMSG_BATTLEMASTER_JOIN), `PortBattlegroundAsync`, `LeaveBattlefieldAsync`, `BattlefieldStatusAsync`, `PvpLogDataAsync`,
`PlayerPositionsAsync`, `CancelAuraAsync`; decoders for SMSG_BATTLEFIELD_STATUS, MSG_PVP_LOG_DATA, SMSG_UPDATE_WORLD_STATE and
MSG_BATTLEGROUND_PLAYER_POSITIONS; lookups of a battlemaster spawn of a type (`battlemaster_entry`), a game object spawn and an area trigger. The
scenario `wsg` (`WarsongGulchScenario`, listed by `.playerbot scenario list` through the shipped-scenario discovery) logs in a human and an
orc warrior, queues each at a battlemaster of its continent, ports both into one match, waits out the two-minute start, captures the Horde flag,
drops the Alliance flag by cancelling the flag aura, returns it, captures twice more (SMSG_BATTLEFIELD_WIN / _LOSE, the final scoreboard) and
waits until both bots are back at their entry points. `WarsongGulchScenario.EnterMatchAsync` is the reusable opening. On a live
world it needs the Warsong Gulch content (map 489, its triggers, safe locations, flag objects and battlemasters), a battleground
template of one player per team (the retail minimum is five, so two bots never start a match otherwise) and
`World:Playerbots:Scenarios:MaxDurationSeconds` of about 600 (two 2-minute waits plus the captures).

**Dungeons** (`ScenarioDungeons`): `RaiseLevelAsync` (ordinary level-up to an entrance's level) and `TakeAreaTriggerAsync` (stand
in a trigger, send CMSG_AREATRIGGER, wait for the far teleport). The scenario `dungeon` (`DungeonEntryScenario`) groups `Scnalpha`
and `Scnbeta`, raises them to level 10, sends the leader through The Deadmines entrance (trigger 78, map 36) into a new instance
that the group is bound to (non-permanent), sends the member through the same trigger into the same instance, checks party chat
inside, and, when `AllowedMaps` lists map 36, logs the member out inside and back in into the same instance (a managed bot may
only log in on an allowed map). Both leave through the exit trigger 119 and the group is disbanded. It needs map 36 and triggers
78 and 119 with their teleports (`map_template`, `areatrigger_template`, `areatrigger_teleport`) and fails at its first step
naming what is missing. On the live content it passes once the world database has been refreshed with Map.dbc and AreaTable.dbc
(`DungeonImportedMapsTests` runs it on the importer's own output; docs/integration/instance-maps-20261008.md). If a step fails after the entrance (the member refused, party chat, the exit), the scenario still
brings every bot that is inside back to where it stood before the entrance and disbands the group (`cleanup: ...` steps, run
under their own bound even after the run's deadline): otherwise the shared bots would stay saved on map 36, the default
`AllowedMaps` [0, 1] would refuse their next login (`login-refused`), and every pair scenario would stop working. A bot that is
offline at that point (a failed relog inside, only tried when `AllowedMaps` lists 36) cannot be moved, and its login there is
allowed. It leaves both scenario bots at level 10 or more. Tests: `DungeonScenarioTests` (including a dungeon that admits one
player, so the member is refused inside the run).

**Ships** (`ScenarioTransports`): `ShipAsync`, `BoardAsync` (a heartbeat carrying ONTRANSPORT, the ship and an offset),
`LeaveShipAsync`, `TimeSkippedAsync`, and the SMSG_TRANSFER_PENDING / SMSG_NEW_WORLD decoders. The scenario `ship`
(`ShipCrossingScenario`) waits for the Ratchet - Booty Bay boat (`gameobject_template` 20808) at one of its ports, places
`Scnalpha` on the dock, boards it at a fixed deck offset, waits for the map change (SMSG_TRANSFER_PENDING naming the ship and the
old map, SMSG_NEW_WORLD carrying the offset, the bot aboard on the other continent), waits for the other port (told apart by its
TaxiPathNode row), checks the bot is at its deck offset, and steps it off within 30 yards of the port. A cleanup step takes the
bot off the ship and back to where it stood, also after a failure. It needs `World:Transports:Enabled` and the ship content
([transports](transports.md)); on the real clock it takes up to one round trip (about six minutes), so
`World:Playerbots:Scenarios:MaxDurationSeconds` must be 600 for `.playerbot scenario run ship`. Tests: `TransportScenarioTests`
(synthetic boat), `RealTransportContentTests.ABot_RidesTheRealBootyBayBoat_...` (real content and terrain, env-gated).

### Running scenarios on a live server

`.playerbot scenario list` and `.playerbot scenario run <name>` (Administrator) run a registered
scenario against the running world on the real clock and print the report lines to the GM.
Both are refused unless `World:Playerbots:Enabled` and `World:Playerbots:Scenarios:Enabled` are
true (default false). `World:Playerbots:Scenarios:MaxDurationSeconds` (120, 5..600) and
`StepTimeoutSeconds` (20, 1..300) bound a run; one run at a time. Scenario bots are real,
persistent characters (running, they count against `MaxBots`; registered, against `MaxRegisteredBots`); they are placed where `Scnalpha`
stands, and the setup helpers change their money, items and health. Enable it only on test
realms.

### Scenario tests

`tests/ArcaneCore.World.Tests/Playerbots/Scenarios/`. `ScenarioTestWorld` is a `WorldTestHost`
with the manual clock, a `ScenarioTimeProvider`, a SQLite character database (bots, items,
mail, quests and spells persist through the real EF stores) and small synthetic content
(`ScenarioTestContent`: a mailbox and a quest giver at the human start, a hostile wolf with
loot and a kobold quest target 60+ yards away, the Duel spell and flag, faction templates).
The tests run the built-ins plus `proc-damage-shield` and `proc-reflect-duel` (`ProcScenarioTests`, docs/areas/procs.md), `class-seal-judgement` and
`class-consecration` (`ClassScriptScenarioTests`, docs/areas/class-scripts.md), `control-possess`, `control-charm` and `spirit-of-redemption`
(`UnitControlScenarioTests`, docs/areas/unit-control.md; decoders and client packets in `ScenarioControlPackets`), `group-loot` (group, free-for-all loot, kill, money split,
item), `mail-item` (persisted letter with item, delivery delay, take), `melee-kill` (swing,
kill, XP credit) and `kill-quest` (accept, kill credit, turn in, settled reward row), and
check database rows after the run. Setting `ARCANE_SCENARIO_REPORT_DIR` collects every report.
`CombatStatScenarioTests` runs `combat-stat-auras` (duel, a damage taken curse and an attacker hit buff cast through CMSG_CAST_SPELL, white
swings that must all land for tenfold damage); its two spells are installed by swapping the spell store on the world thread.
`ScenarioTestWorld.StartAsync(configure)` registers extra services after the synthetic content (a later store registration
replaces it). `GameObjectScenarioTests` uses it for `meeting-stone` (a party queued at a meeting stone takes in a solo bot,
which later leaves) and `ritual-of-summoning` (a warlock's ritual, two helpers, a far bot that accepts the summon); the
meeting stone actions and decoders are in `ScenarioMeetingStones` (`JoinMeetingStoneAsync`, `LeaveMeetingStoneAsync`,
`MeetingStoneInfoAsync`, `SetQueue`, `MemberAdded`, `JoinFailed`). The scenario content only supports human warriors:
creating a human priest (5) or warlock (9) bot there fails with `create-failed`.
`ScenarioTestWorld.StartAsync(configure)` also lets a test register its own content and seams after
`ScenarioTestContent` (a later registration wins). `CreatureAiScenarioTests` uses it for creature AI across sessions: an
orc bot walks up to a CALLS_GUARDS townsman, a human bot hears the shout and the guard post's guard runs to the orc and
swings (`SMSG_ATTACKERSTATEUPDATE`); the human waves (`ScenarioCreatureActions.TextEmoteAsync`, CMSG_TEXT_EMOTE) at a herald
whose EventAI RECEIVE_EMOTE row greets it by name (`ScenarioCreatureDecoders.MonsterChat`).

Teleport and death lane (wave 2): `pet-teleport` (`PetTeleportScenarioTests`: a hunter bot's pet comes back at its side after a far
teleport to Kalimdor, the hunter gets its pet bar again and a watcher bot's client is sent the pet; the content creates warriors only,
so the bot's class byte is set to hunter on the world thread) and `raid-lock` (`RaidLockScenarioTests`: two bots form a raid group with
CMSG_GROUP_RAID_CONVERT, Molten Core is added to the map registry on the world thread, the leader is locked inside, the stored
`group_instance` row is read back from SQLite, and the member who was outside enters the same instance and is locked too).
`BattlegroundScenarioTests` and `BattlegroundWorldScenarioTests` start the scenario world with `WarsongGulchTestContent` (map 489, its safe
locations, flag stands with their event rows, flag room triggers, flag auras, a battlemaster per side) through the `StartAsync(configure)` hook.
`TransportScenarioTests` use the same hook for synthetic ship routes (`TransportWorldContent`): `ship-duel` (two
bots board a ferry, duel aboard while it sails away from the flag, and the duel ends fled when one steps off) and
`ship-crossing` (a bot rides a ship through its map change and arrives aboard on map 1).
`PartyScenarioTests` runs `party-master` (`Scenarios/ScenarioParty.cs`, `PartyScenario`): the master is a real socket client
(`IPartyScenarioMaster`, a `WorldTestClient` whose reader records every packet and acknowledges teleports like a game client) and
the bot `Scnfollower` runs autonomously. The master, on the test's `World:Playerbots:Party:Allowlist`, invites the bot and it
accepts; the bot follows a 40-yard walk;
the master targets the wolf and whispers `attack`, and the bot kills it; the group roll on the wolf's uncommon item gets the
bot's vote within 2 s of game time and resolves on the master's vote; after `stay` the bot holds while the master walks off;
`status` is answered; the master takes the Deadmines entrance (trigger 78) and the bot lands in the same instance, and comes
out with it through the exit (119); the master leaves the group and the brain drives the bot again. The test adds the
Deadmines content, an uncommon item to the wolf's loot, AllowedMaps [0, 1, 36] and flat ground at the start's height (the bot
plans its walks there). Without a master (the catalog's instance, `.playerbot scenario run party-master`) it fails at its first
step: a live run has no socket master to give it.

## MockClient playbot (external protocol client)

`arcane-mock playbot` runs one external build-5875 client against an owned numeric
loopback realm. It uses the real SRP, world authentication, character creation,
login, gameplay and logout handlers. Use a disposable Player account and an
explicit character name; the first repertoire creates/selects a Human Warrior.

```powershell
# Set ARCANE_BOT_PASSWORD privately before running; no password argument is accepted.
dotnet <verified-artifact-path>/arcane-mock.dll playbot --account BOTONE --character Botone --password-env ARCANE_BOT_PASSWORD --duration-s 120 --steps 100 --attack-entry 6 --report <new-report-path>.json
```

The deterministic selector queries nearby observed creatures, explores using
three-yard movement steps and approaches an explicitly allowed creature entry.
Combat is disabled when `--attack-entry` is omitted or zero. It waits when player
health/combat facts are unknown, stops its attack
when injured or its target disappears/dies, and releases spirit once after an
observed player death. A nearby configured NPC that is observed targeting this
character with its full GUID and combat flags can be answered defensively.
Below 60 percent health outside combat, an observed owned starter-food item117
with positive stack can be used through the normal sit/item-use flow. The client
waits for own spell433, food aura, one consumed stack and healing, then stands.
That action shares the same reader and the overall session traffic budget.
Heroic Strike requires spell 78 in the observed initial
spellbook and at least 150 raw rage. Loot comes from an observed nearby lootable
corpse and a decoded server loot window. Reports distinguish actions sent from
packet families received; movement sends alone do not establish terrain acceptance.

The action loop adapts to received state and chooses again after each observation
window. It does not run the fixed `starting-zone` quest script. Current coverage
does not include quest chains, terrain navigation, general food selection, corpse recovery,
general spell planning or arbitrary classes. Local exploration stays within 30
yards of the login origin and 20 exploration/approach movement sends. Approach
targets must be within 25 yards and two yards vertically. The runtime bounds
duration to 1..600 seconds, actions to 1..500, observed objects to 4096, and
post-authentication traffic to 10000 frames/eight MiB. Logout has a separate
30-second cleanup bound. Each process owns one connection and one frame reader.
Run a second client with a separate disposable account/character rather than
sharing a character across processes.

## Optional installed local model

Add `--llm true --model qwen3.5:4b`. The model provider defaults to
`http://127.0.0.1:11435/`; `--model-endpoint` accepts another literal loopback HTTP
origin. It uses the installed Ollama model and never calls a model download API.
Warm and decision requests use a 30-second idle lease. This reduces idle
retention; it does not lower peak loading memory. The observed qwen3.5:4b runner
committed about11.8GiB on this Windows host despite a2048-token context, so model
loading and builds need separate resource admission. The default selector works
without loading it.
Explicit loading has a 30-second bound; ordinary decisions have five seconds.
Generation disables thinking/streaming, uses a 2048-token context, limits output
to 96 tokens, and requests a JSON schema containing only supplied action IDs.
Responses are bounded to 16 KiB and validated locally. Credentials, character
names, chat, coordinates, proprietary DBC data and raw logs are excluded from
the model request. Proxying and redirects are disabled.

The model selects from deterministic candidates; it cannot introduce commands,
packets, targets or coordinates. The client drains incoming traffic and validates
the selected candidate again before sending. Provider failures, malformed or
stale selections use the deterministic fallback. Model loading failure also
uses the baseline. LLM operation is disabled by default. The existing eight-case
local selector benchmark establishes a useful candidate, not general gameplay
accuracy; actual client qualification and live reports are separate evidence.

Reports contain action kinds/providers/fallback codes, numeric target/health
facts, traffic counts, received packet families and logout status. They exclude
account/password values and server chat. `--report` creates a new file and refuses
an existing path, preserving earlier evidence. Standard output also receives the
JSON report; a run/traffic/protocol or logout failure returns a nonzero exit code.

Protocol layouts reuse `ScenarioConnection`, `ScenarioWire`, `StartingZoneProbe`,
`StartingZoneLoot` and the normal client protocol classes. Their pinned
vmangos/wow_messages citations remain authoritative for build 5875. WoWWiki
1.12.1 and Wowhead Classic are supplemental version-checked information sources;
later Classic mechanics are not automatically treated as 1.12.1 behavior.
