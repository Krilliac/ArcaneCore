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
`MaxActionsPerTick` 4, `AllowedMaps` [0, 1], `AllowLocalLlm`, ...; off by default).

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

**Typed decoders** (`ScenarioDecoders`, mirroring the server writers): group list, party
command result, group invite, trade status, mail result and mail-list count, duel requested,
duel complete, duel winner, loot response, loot-money share, attacker state update, spell go,
cast result, spell failure, chat message, XP gain, quest kill update and quest complete. The proc engine's packets have their own
decoders (`ScenarioProcDecoders`: SMSG_SPELLDAMAGESHIELD, SMSG_PROCRESIST).
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
1-health finish; SMSG_DUEL_WINNER checked against server state). `IPlayerbotScenario` services
registered in DI are listed too. `ScenarioSteps` holds reusable blocks (form a group, open and
accept a trade, leave earlier groups).

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
The tests run the built-ins plus `proc-damage-shield` and `proc-reflect-duel` (`ProcScenarioTests`, docs/areas/procs.md), `group-loot` (group, free-for-all loot, kill, money split,
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
