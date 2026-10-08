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
Configuration is `World:Playerbots` (`Enabled`, `MaxBots` 8, `ThinkIntervalMs` 500,
`MaxActionsPerTick` 4, `AllowedMaps` [0, 1], `FaultBackoffSeconds` 30, `MaxFaults` 3,
`FaultWindowSeconds` 3600, `AllowLocalLlm`, ...; off by default).

GM commands: `.playerbot create|start|stop` (Administrator), `.playerbot status|list|inspect`
(GameMaster), `.playerbot scenario list|run` (Administrator, below). The autonomous brain's
behaviour is described in `docs/integration/playerbot-*.md`.

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

In **scripted mode** an `IPlayerbotController` replaces the brain.
`ManagedPlayerbotFeature.StartScriptedAsync(idOrName, controller)` starts a bot that way;
`SetControllerAsync(botId, controller)` switches a running bot (null returns it to autonomous
mode). While scripted, the brain is never updated (no goals, no packets drained, no actions)
and the shared action budget does not apply; the controller's `Tick` runs on the world thread
every tick with a `PlayerbotControllerContext` (`TryAction`, `AcknowledgeServerOrders`).
Stopping a bot detaches its controller; `IsScripted(botId)` reports the mode. Switching back
to autonomous mode resumes the brain with whatever state it had before.

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
  through `PlayerbotConsumables` while its master is within 30 yards, and teleports to a master more than 100 yards away or on
  another map (`TeleportToLeader`, default on; vmangos `.goname`, only while the bot is out of combat, as vmangos does it inside
  its `!IsInCombat()` block; in combat it walks after a far master on its map) through the teleport service the GM
  `.goname`/`.namego` commands use, so the map resolver's instance rules and the group's instance bind decide where it lands
  (into the master's instance). It lands on the master's spot, not 5 yards above as `.goname` puts a GM: it has no client to
  fall. A master on a map outside `AllowedMaps`, on a taxi flight (vmangos idles then), or in another instance of the bot's own
  map id (the service teleports within one map id by a near teleport, which keeps the bot's instance) is waited for, not
  followed. A refused teleport is retried after 10 s.
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
  language). A whispered word that is no command gets the command list; a whisper from anyone else gets one polite answer
  (once per sender a minute, at most 8 senders a minute); other bots' lines are ignored.

`.playerbot inspect` prints `BOTINSPECT party=master:<name> mode:<follow|stay|passive>` (or `party=none`); while the party AI
drives a bot its goal is `Follow` or `Assist` (appended to the persisted `PlayerbotGoalKind`). A controller that takes over a
grouped bot (scripted mode) makes the party AI let go: no party goal, master or mode is reported while it drives, and when it
detaches a bot still grouped is engaged afresh. Brain bots do not acknowledge a
far teleport (the brain returns while the player is in no map, before `PlayerbotMovementControl`); the party AI does, so it can
follow its master into an instance.

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
manual clock a wait's timeout is game time, followed by a short wall-clock grace
(`ManualWallGrace`, 3 s) for asynchronous I/O such as database commits. Code that reads
`DateTime` or `Stopwatch` directly, rather than the world clock or `TimeProvider`, does not
follow the manual clock. `ScenarioClock.Real` (live server) polls in wall time.

**Built-in scenarios** (`PlayerbotScenarioCatalog`; bots `Scnalpha` and `Scnbeta`, created on
first use): `smoke` (login, hear own /say), `group-chat` (invite, accept, both group lists and
the server roster, party chat, leave), `trade` (Linen Cloth 2589 for 75 copper through the
trade window; both inventories and purses), `duel` (spell 7266, accept, countdown, melee to the
1-health finish; SMSG_DUEL_WINNER checked against server state). After them come the other public
scenarios this assembly ships (`PlayerbotScenarioCatalog.Shipped`, discovered: `dungeon`, `wsg`), then
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
naming what is missing. If a step fails after the entrance (the member refused, party chat, the exit), the scenario still
brings every bot that is inside back to where it stood before the entrance and disbands the group (`cleanup: ...` steps, run
under their own bound even after the run's deadline): otherwise the shared bots would stay saved on map 36, the default
`AllowedMaps` [0, 1] would refuse their next login (`login-refused`), and every pair scenario would stop working. A bot that is
offline at that point (a failed relog inside, only tried when `AllowedMaps` lists 36) cannot be moved, and its login there is
allowed. It leaves both scenario bots at level 10 or more. Tests: `DungeonScenarioTests` (including a dungeon that admits one
player, so the member is refused inside the run).

### Running scenarios on a live server

`.playerbot scenario list` and `.playerbot scenario run <name>` (Administrator) run a registered
scenario against the running world on the real clock and print the report lines to the GM.
Both are refused unless `World:Playerbots:Enabled` and `World:Playerbots:Scenarios:Enabled` are
true (default false). `World:Playerbots:Scenarios:MaxDurationSeconds` (120, 5..600) and
`StepTimeoutSeconds` (20, 1..300) bound a run; one run at a time. Scenario bots are real,
persistent characters (they count against `MaxBots`); they are placed where `Scnalpha`
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
