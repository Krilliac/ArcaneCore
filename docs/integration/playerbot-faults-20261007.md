# Playerbot faults: Dawnrover and Ironwander (2026-10-07)

Lane `claude/w3-bot-faults` (wave 3).

## What happened

On the live movement build (`D:/ArcaneCore-lanes/_deploy/claude-movement-r1/world.log`, lines 468 and 520) two managed bots logged
`Playerbot <id> action failed (InvalidOperationException)` with no message or stack:

| Bot | Character | GhostTime (unix) | Fault saved (UpdatedUnix) | Ghost to fault |
|---|---|---|---|---|
| Dawnrover `cce84f56-...` | 76, paladin 6 | 1791406791 | 1791406840 | 49 s |
| Ironwander `533c7b0c-...` | 77, warrior 4 | 1791407925 | 1791407972 | 47 s |

Afterwards both rows had `DesiredEnabled = 0`, so neither came back at the next restart.

## Root cause

Read from the saved state (`D:/ArcaneCore-lanes/_deploy/live-w2-r1/before-characters.db`, copied): both rows were saved with goal 8
(`Recover`), both characters were ghosts (`character_vitals.IsGhost = 1`) standing exactly on their corpses, and both had died more than once
within five minutes (`DeathExpireTime = GhostTime + 600`), so the reclaim delay was 60 to 120 s (vmangos `Player::GetCorpseReclaimDelay`, the
30/60/120 s steps).

`PlayerbotRecovery` capped a recovery at 360 calls and 180 s. It counted one call per think, and the live profile thinks every 100 ms
(`World:Playerbots:ThinkIntervalMs = 100`), so the cap ran out after 36 s. A ghost that had walked back to its body (about 11 to 13 s)
and was waiting out a 60 s delay hit the cap at 47 to 49 s and threw `InvalidOperationException("playerbot-recovery-stalled")`.
`ManagedPlayerbotFeature` caught it, logged only the type, kicked the session, and the next checkpoint stopped the bot with
`DesiredEnabled = false`.

RED: `PlayerbotRecoveryTests.GhostAtCorpse_WaitsOutAScaledReclaimDelay_AtAShortThinkInterval_ThenReclaims` threw exactly
`playerbot-recovery-stalled` before the fix (`D:/ArcaneCore-lanes/_logs/w3-bot-faults/red-bot-faults.log`).

End-to-end replay (`D:/ArcaneCore-lanes/_logs/w3-bot-faults/repro/`, `setup_repro.py`): copies of `live-w2-r1/before-*.db`, only the two
bots desired, their ghosts' `GhostTime` moved to the launch time and `DeathExpireTime` to launch + 900 s (a 120 s delay, as live),
private worlds on 127.0.0.1:18185-18187.

* `red/`: the live movement build (`_deploy/movement-r1`, source 6e6bd1fc) on the Codex-line copies: both bots logged
  `action failed (InvalidOperationException)` again and both rows ended with `DesiredEnabled = 0`.
* `green/` and `green2/`: this lane's build on copies upgraded to characters 40 / world 41: no fault, both rows stayed desired
  and Running. `green2/mock.log` holds `.playerbot inspect` every 20 s from a private GM: both ghosts waited at their bodies while
  `delay_remaining_s` counted down from 119, then reclaimed; Ironwander went on to fight and train. Dawnrover was killed again
  right after reclaiming (its third death, so another 120 s wait) and was again waiting without a fault when the run ended.
  Reclaiming beside whatever killed it is a gameplay gap, not a fault (follow-up: wait for nearby hostiles to leave, or use the
  spirit healer). **Closed** by lane `claude/tb-b4-dungeon-movement-recovery` (see "Follow-up closed" below).

## Fixes

1. **Recovery** (`PlayerbotRecovery`): progress-based bounds instead of a think count. Waiting at the body while
   `MapCombat.CorpseReclaimWaitSeconds` (the delay `TryReclaimCorpse` enforces, vmangos MiscHandler.cpp:589) is above zero counts as progress
   and sends nothing, like the client's disabled Resurrect button. The recovery gives up (`playerbot-recovery-stalled`) only after 60 s
   without progress, and a walk that stops closing on the body for 10 s is `playerbot-recovery-stuck`.
2. **Logging**: an action, controller or movement fault is logged with the whole exception (`LogWarning(ex, ...)`, type and message in the
   text). Start and checkpoint failures carry the exception too.
3. **Policy** (`ManagedPlayerbotFeature`, decided here): one fault does not disable a bot. It is quarantined with `DesiredEnabled` kept and
   logged in again after a backoff (30 s, doubled per fault, at most an hour); 3 faults within an hour disable it
   (`FaultBackoffSeconds`, `MaxFaults`, `FaultWindowSeconds` under `World:Playerbots`). Reason: a fault is usually a bug in one goal under
   one state (here a timing cap), not a broken bot. Disabling on the first fault turned every transient bug into a permanent loss that
   needed an operator. Repeated faults still stop a bot that cannot run, so a crash loop cannot spin forever. Details:
   `docs/areas/playbots.md`, "Faults and quarantine".
4. **Restore at startup**: a desired bot that cannot log in at startup (`login-refused`, for example saved on a map outside
   `AllowedMaps`) is quarantined and retried the same way. Before, the failed restore also cleared `DesiredEnabled`
   (`ManagedPlayerbotLifecycleTests.AFailedRestoreOnStartup_KeepsTheBotDesired`, RED: `D:/ArcaneCore-lanes/_logs/w3-bot-faults/red-restore.log`).

Tests: `ManagedPlayerbotLifecycleTests.OneActionFault_LogsTheException_AndKeepsTheBotDesired` (RED before: the bot was stopped and not
desired), `AQuarantinedBot_LogsInAgainAfterTheBackoff_Autonomous`, `TheLastAllowedFault_DisablesTheBot_AndItStaysOut`,
`FaultOptions_AreBounded`; `PlayerbotRecoveryTests.GhostAtCorpseAfterDelay_ReclaimsThroughOrdinaryHandler` now expects the bot to wait
silently within the delay.

## Review fixes (2026-10-07, second pass)

5. **An operator stop wins over a scheduled retry.** `RetryQuarantinedAsync` picks the due bots outside the operation lock; an operator
   `.playerbot stop` that took the lock in between cleared the quarantine and stored `DesiredEnabled = 0`, and the waiting retry then
   started the bot anyway and stored `DesiredEnabled = 1`. The retry now re-checks under the lock (the bot is still desired and still in
   the same quarantine entry) and otherwise ends with `quarantine-cleared`; it removes only its own entry. The startup restore keeps its
   own start kind. Test: `ManagedPlayerbotLifecycleTests.AnOperatorStop_BetweenTheRetrySnapshotAndItsStart_StaysStopped` forces the
   window (a second bot's checkpoint update is held inside the lock, the stop queues, the lock is FIFO) and checks the log shows the
   refused retry. RED: the bot was `Running` again (`D:/ArcaneCore-lanes/_logs/w3-bot-faults/rework/red.log`).
6. **ErrorCode text.** The stored code now carries exception text, echoed to chat by `.playerbot` status: control characters become
   spaces and the 128-unit cut never splits a surrogate pair (`ManagedPlayerbotFeature.Code`). Test:
   `AFaultMessage_IsStoredWithoutControlCharacters_AndCutOnACharacterBoundary` (RED: the CR LF of the message stored as is).
7. **The `dungeon` scenario cleans up after a failure inside.** Before, a failed step after the entrance released `Scnalpha` and
   `Scnbeta` saved on map 36 with the group bind in place; under the default `AllowedMaps` their next login was refused, which broke
   every pair scenario. The scenario now brings every bot inside back to its pre-entrance position and disbands the group in a
   `finally`, as `cleanup: ...` report steps under their own bound (`ScenarioContext.CleanupAsync`). Test:
   `DungeonScenarioTests.Dungeon_AStepFailingInside_BringsBothBotsOut_AndThePairScenariosStillRun` (a one-player dungeon refuses the
   member; RED: `Scnalpha is on map 36`).

## Live repair

The two rows still have `DesiredEnabled = 0` from the incident. Once this build runs, an Administrator brings them back with
`.playerbot start Dawnrover` and `.playerbot start Ironwander`. That is an ordinary start: it sets `DesiredEnabled` again, and the
ghosts reclaim their bodies after the delay. No schema change is involved.

## Follow-up closed: hostiles at the body, the spirit healer, deaths in dungeons (2026-10-08)

Lane `claude/tb-b4-dungeon-movement-recovery` (wave b, dungeon movement and recovery). Details: `docs/areas/playbots-dungeons.md`.

7. **Hostiles near the body.** After the reclaim delay a ghost at its body does not reclaim while a living hostile creature it can see
   stands within 25 yards of the body (`PlayerbotRecovery.HostileClearYards`; mangoszero playerbot `ReviveFromCorpseAction`). Waiting
   for hostiles is not progress, so a body camped for a minute is given up (next item). Test:
   `PlayerbotRecoveryDecisionTests.AHostileNearTheBody_MeansWaiting_ThenTheGhostReclaimsWhenItLeaves`.
8. **No more `playerbot-recovery-stalled` / `playerbot-recovery-stuck` throws for a ghost.** A ghost whose recovery made no progress
   for 60 s, whose walk stopped closing on its goal (or could not be planned) for 10 s, that has no body, or whose body is on another
   map with no entrance trigger here, takes the spirit healer: the nearest one it can see, else it walks to the graveyard of its
   position, and sends CMSG_SPIRIT_HEALER_ACTIVATE (vmangos NPCHandler.cpp:416) through the ordinary handler. Only when that
   fallback stalls the same way (or there is no healer and no graveyard) does the recovery fault, with
   `playerbot-recovery-spirit-healer-failed`. A body that cannot even be released for 60 s still faults with
   `playerbot-recovery-stalled` (a spirit healer cannot help an unreleased body). Tests:
   `PlayerbotRecoveryDecisionTests.AStalledCorpseRun_TakesTheSpiritHealer_AndNothingThrows` (RED before: threw
   `playerbot-recovery-stuck`), `WithoutAnySpiritHealer_TheRecoveryFaultsOnlyWhenTheFallbackFails` (RED before: the stuck code),
   scenario `dungeon-bot-spirit-healer` (`DungeonBotScenarioTests.ABotWhoseCorpseRunStalls_UsesTheSpiritHealer_AndIsNotQuarantined`).
9. **A death inside a dungeon.** The ghost is released at the graveyard outside; the recovery walks it into the entrance trigger whose
   `areatrigger_teleport` leads to the body's map and the server revives it at the entrance (vmangos Player.cpp:1953-1966). Before,
   `corpse.Map != player.Map` made the recovery wait doing nothing until it threw `playerbot-recovery-stalled`. Scenario
   `dungeon-bot-ghost-entrance`.
