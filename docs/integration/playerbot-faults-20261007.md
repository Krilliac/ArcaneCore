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

## Live repair

The two rows still have `DesiredEnabled = 0` from the incident. Once this build runs, an Administrator brings them back with
`.playerbot start Dawnrover` and `.playerbot start Ironwander`. That is an ordinary start: it sets `DesiredEnabled` again, and the
ghosts reclaim their bodies after the delay. No schema change is involved.
