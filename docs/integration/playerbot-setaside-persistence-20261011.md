# Playerbot quest set-asides across restarts (2026-10-11)

Base: `ebdd4a37`. The live analysis in `<lanes-root>/_wf/analysis/bot-xp-analysis.md` §4.2 observed 48 bots holding completed quest 1361, 251 completed unturned quests in total, and repeated stalls across five restarts. Those measurements are from the prior deployment; this lane has no new live or client acceptance.

## Root cause and rule

`PlayerbotSuspensions` and `PlayerbotSharedSetAsides` used world-clock dictionaries owned by a brain and `ManagedPlayerbotFeature`. `ManagedPlayerbot` persisted only the current goal. Restart rebuilt empty dictionaries; replacing a brain on group departure also discarded its per-bot suspensions. A completed quest with an ender elsewhere on the same map got return priority even across thousands of yards, while an ender on another map could not be reached by the destination lookup at all.

Characters schema 59 adds `managed_playerbot_set_aside`. Rows distinguish bot entry, bot quest, bot training, shared entry, shared quest, and (kind 5, added by `4de62c6b`, no schema change) a bot's unreachable quest: an abandoned, mob-blocked completed quest it must never re-take, stored with `ExpiresUnixMs = long.MaxValue` so it never expires (bounded to 64 per bot). **Update (defer ledger, 2026-10-16): kind 5 is now finite and escalating**: the expiry is a real time (60 s, 10 min, 1 h, 6 h, 24 h by strike), `RepeatCount` carries the strikes, and the row is kept after the expiry (expiry = the save time) until the quest is turned in; an old `long.MaxValue` row is restored as a staggered minute with two strikes and rewritten finite. The kind values are persisted and pinned explicitly in `PlayerbotSetAsideKind`. Rolling back to `f67b41a1` (before kind 5) drops the kind-5 rows on that build's next save, so those bots may re-take the quest. Their absolute UTC millisecond expiry is converted to remaining world time on load. The existing five-second checkpoint saves changed snapshots; bot stop and feature shutdown flush them too. A brain replacement carries the current suspension state. A character deletion removes that character's bot rows; shared rows have no character owner.

A home-bound bot keeps a completed quest when a present ender is in its home or at most 600 yards from its current position on the same map. Otherwise it sends the normal `CMSG_QUESTLOG_REMOVE_QUEST` with the quest's log slot, freeing the slot through the ordinary journal, item and persistence path (`src/ArcaneCore.World/Npc/QuestNpcInteractionHandlers.cs:60`; `src/ArcaneCore.Game/Npc/QuestNpcServices.Interaction.cs:314`; vmangos `src/game/Handlers/QuestHandler.cpp:327`, `HandleQuestLogRemoveQuest`). Bots without a configured home retain the existing return behavior. The 600-yard limit is a bot policy chosen for local errands, not a retail quest rule.

## QA quest content check

Read-only imported world DB: `<work-dir>/playable-content-20261005/complete-r5/world.db`, opened using SQLite `?mode=ro`. Quest template, creature starter/ender relation, NPC template and spawn were present for all six. These checks establish content presence, not successful live pathing or turn-in.

| QA issue | Quest | Starter → ender in imported DB | Content verdict |
|---|---|---|---|
| #202 | 6605 A Strange One | Harlo Wigglesworth 11755 (map 1, 6714,-4656) → Winna Hazzard 9996 (map 1, 5127,-325) | No missing NPC/objective/script found. No objectives; breadcrumb to 4505. About 4,600 yards straight line, so a local bot should abandon. |
| #198 | 1361 Regthar Deathgate | Krusk 2229 (map 0, -41,-957) → Regthar 3389 (map 1, -307,-1972) | No missing quest content found; ender is on another map. Regthar's `npc_regthar_deathgate` script is not implemented here, but mangos-classic `src/game/AI/ScriptDevAI/scripts/kalimdor/the_barrens.cpp:1073`, `QuestAccept_npc_regthar_deathgate`, starts the event only for quest 4021. This is a separate script gap, not proof that 1361's ordinary turn-in is broken. |
| #194 | 1418 Neeka Bloodscar | Helgrum 1442 (map 0, -10465,-3318) → Neeka 5394 (map 0, -6672,-2241) | No missing NPC/objective/script found. No objectives; breadcrumb to 1420. Ender is far outside the starter area. |
| #191 | 5881 Calling in the Reserves | Maggran 11860 (map 1, 925,941) → Grish 12576 (map 1, -4602,-1835) | No missing NPC/objective/script found. Required item 16189 is also the configured source item and exists in `item_template`; travel is about 6,200 yards straight line. |
| #186 | 1111 Wharfmaster Dizzywig | Kravel 4452 (map 1, -6233,-3856) → Dizzywig 3453 (map 1, -986,-3797) | No missing NPC/objective/script found. Required item 5799 is also the configured source item and exists in `item_template`; travel is about 5,200 yards straight line. |
| #162 | 109 Report to Gryan Stoutmantle | Five Westfall/Elwynn starters → Gryan 234 (map 0, -10509,1045) | No missing NPC/objective/script found. The prior QA analysis notes stalls outside home; Gryan has a Westfall spawn, so a home whose area includes it can retain the turn-in. |

For 5881 and 1111, `QuestNpcServices.Interaction.cs` `GiveSourceItem` on accept supplies the source item; the read-only DB check found both templates. This does not prove the live bot had bag room or that the exchange succeeded.

## Verification

RED: I deliberately omitted the `Restore` call in `PlayerbotSetAsidePersistenceTests.BotSetAside_RestoresRemainingLifetime_AndDoesNotExtendOnRestart` for a single run. The new brain reported `IsQuestSuspended(1361) == false` and `Assert.True` failed at line 21. Restoring the call made this deterministic probe GREEN. The first test build had also reported a compile RED for `ReasonableReturnTrip` before its production method was compiled.

A broader run exposed shutdown ordering: `WorldTestHost` stops `WorldRuntime` before feature shutdown, so a queued snapshot was canceled. The save path now captures directly after the world has stopped; the affected lifecycle tests passed on rerun. The quest-abandon integration test initially used the character save queue as a barrier; its persisted row still read `Incomplete` because quests use `QuestNpcPersistence`'s own queue. Waiting on `QuestNpcPersistence.FlushCharacterAsync` gave the correct state-specific barrier.

The final Release solution build used `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:NuGetAudit=false -v:q` after a free-RAM check. The affected World classes passed 55/55, Data/schema classes 6/6, character deletion 6/6, and generated Docs tests 98/98 with and without `ARCANECORE_UPDATE_DOCS=1`. The final build and focused GREEN run after the RED probe are recorded in the coordinator handoff. `NuGetAudit=false` avoids the sandbox's blocked NuGet audit endpoint; the build does not establish an audit result. No real-client quest interaction or deployment was run.

Exact final commands, from this worktree (PowerShell; both test data environment variables set before every test command):

```powershell
(Get-Counter '\Memory\Available MBytes').CounterSamples.CookedValue
dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:NuGetAudit=false -v:q
$env:ARCANECORE_TEST_TERRAIN_DIR='<data-root>/terrain-5875'
$env:ARCANECORE_TEST_DBC_DIR='<data-root>/client-dbc-5875'
dotnet test tests/ArcaneCore.World.Tests -c Release --no-build --filter 'FullyQualifiedName~PlayerbotSetAsidePersistenceTests|FullyQualifiedName~ManagedPlayerbotSetAsideRestartTests|FullyQualifiedName~PlayerbotCompletedReturnTests|FullyQualifiedName~PlayerbotQuestReturnTests.CompletedOutOfHomeBreadcrumb_IsAbandonedThroughTheQuestHandler'
dotnet test tests/ArcaneCore.World.Tests -c Release --no-build --filter 'FullyQualifiedName~PlayerbotSetAsidePersistenceTests|FullyQualifiedName~ManagedPlayerbotSetAsideRestartTests|FullyQualifiedName~PlayerbotCompletedReturnTests|FullyQualifiedName~ManagedPlayerbotLifecycleTests|FullyQualifiedName~PlayerbotQuestReturnTests|FullyQualifiedName~PlayerbotQuestGoalsTests|FullyQualifiedName~PlayerbotSharedSetAsideTests|FullyQualifiedName~ManagedPlayerbotHomeTests'
dotnet test tests/ArcaneCore.Data.Tests -c Release --no-build --filter 'FullyQualifiedName~ManagedPlayerbotSetAsideStoreTests|FullyQualifiedName~IntegratedSchemaTests'
dotnet test tests/ArcaneCore.Data.Tests -c Release --no-build --filter 'FullyQualifiedName~CharacterDeletionTests'
$env:ARCANECORE_UPDATE_DOCS='1'
dotnet test tests/ArcaneCore.World.Tests -c Release --no-build --filter 'FullyQualifiedName~Docs'
Remove-Item Env:ARCANECORE_UPDATE_DOCS
dotnet test tests/ArcaneCore.World.Tests -c Release --no-build --filter 'FullyQualifiedName~Docs'
```

After the final stop-path change and RED probe, the final build reported 0 warnings/errors, focused World tests 5/5 passed, and lifecycle/restart tests 24/24 passed. The broad 55/55 World run, 6/6 Data/schema run, 6/6 deletion run, and both 98/98 Docs runs were completed before that last stop-path change. Its direct coverage passed again in the 24/24 lifecycle/restart run. The tested DLLs were newer than the last corresponding source edit. `git diff --check` had no whitespace errors.

## Independent review follow-up (same worktree, after commit `7651bf07`)

The follow-up changes are uncommitted. No new schema step or config key was added.

* A completed quest is considered for distant abandonment only when the quest is active and `QuestNpcServices.IsRewardable` says its turn-in is supported (`QuestNpcServices.Support.cs:242`, `SupportedRewardQuest`; vmangos `Quest::IsActive` as already documented by `PlayerbotQuestGoals.RefreshObjectiveEntry`). This preserves a completed event quest while its event is off. After dispatching `CMSG_QUESTLOG_REMOVE_QUEST`, the bot checks the live quest status; `TryManagedAction` only reports that the session remained in-world (`ManagedWorldSession.cs:99-100`). A refused abandon enters the existing ten-minute quest refusal backoff, and the brain continues other decisions.
* A failed local set-aside save is logged while bot stop still kicks the session, persists Stopped/Faulted and removes the active bot. A failed shared save during shutdown is also logged, allowing planner and chat cleanup. The snapshot is best effort; a storage error may lose that pause on a later restart.
* Shared repeat count now saturates at the store's 256 limit. Local restore enforces the existing 64-key bound per set. Startup's cached snapshot is discarded after successful bot start. Version reads use `Volatile`; a checkpoint finishing after `ReplaceBrain` cannot overwrite the new brain's unsaved marker because the final version write checks brain identity under the same gate as replacement.

Behavioral RED before production edits, then GREEN after them:

| Test | RED observation | GREEN observation |
|---|---|---|
| `RefusedDistantAbandon_DoesNotHoldTheBrainOrRepeatEveryThink` | With a no-op registered abandon handler, `IsRefused` was false and the bot kept returning early. | The quest remains Complete, only one abandon packet is sent over two thinks, the quest is refused for backoff, and the brain chooses another goal. |
| `AnOutOfSeasonQuest_IsNeitherTakenNorTurnedIn_UntilItsEventRuns` | Explicit distant-return probe observed `abandoned == true` after Winter Veil stopped. | The completed event quest remains in the log until the event returns; the full existing scenario passes. |
| `ThrowingSetAsideStore_DoesNotPreventBotShutdownOrLaterCleanup` | `set-aside-storage-refused` escaped feature shutdown. | Both bot records end Stopped and chat cleanup runs, despite bot and shared snapshot writes throwing. |
| `RepeatedSharedSuspensions_KeepPersistedRepeatCountWithinStoreLimit` | Exported repeat count was 257. | After 257 suspensions it remains at 256, which the store accepts. |
| `RestoreTwice_KeepsEachLocalSetBounded` | Two 80-row restores left more than 64 quest entries. | Export contains at most 64 quest entries. |

Final validation: free RAM checked through `Get-Counter '\Memory\Available MBytes'` before `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:NuGetAudit=false -v:q`; build exit 0, 0 warnings/errors. With `ARCANECORE_TEST_TERRAIN_DIR=<data-root>/terrain-5875` and `ARCANECORE_TEST_DBC_DIR=<data-root>/client-dbc-5875`, `dotnet test tests/ArcaneCore.World.Tests -c Release --no-build --filter 'FullyQualifiedName~PlayerbotSetAsidePersistenceTests|FullyQualifiedName~PlayerbotSharedSetAsideTests|FullyQualifiedName~ManagedPlayerbotSetAsideRestartTests|FullyQualifiedName~PlayerbotStallTests|FullyQualifiedName~PlayerbotQuestGoalsTests|FullyQualifiedName~PlayerbotQuestReturnTests|FullyQualifiedName~PlayerbotEventQuestTests|FullyQualifiedName~ManagedPlayerbotLifecycleTests'` passed 52/52. `dotnet test tests/ArcaneCore.Data.Tests -c Release --no-build --filter 'FullyQualifiedName~IntegratedSchemaTests'` passed 5/5. DLL timestamps postdate source edits; `git diff --check` reported no whitespace errors. Real-client behavior, crash recovery after a missed checkpoint, and a forced checkpoint/brain-replacement interleaving were not exercised in this follow-up.

Changed production files: `src/ArcaneCore.Playerbots/PlayerbotQuestGoals.cs`, `ManagedPlayerbotFeature.cs`, `PlayerbotStallWatch.cs`, `src/ArcaneCore.Kernel/Characters/PlayerbotSetAside.cs`, `src/ArcaneCore.Data/Characters/Playerbots/ManagedPlayerbotSetAsideDataModule.cs`. Changed tests: `tests/ArcaneCore.World.Tests/Playerbots/PlayerbotQuestReturnTests.cs`, `PlayerbotEventQuestTests.cs`, `ManagedPlayerbotSetAsideRestartTests.cs`, `PlayerbotSharedSetAsideTests.cs`, `PlayerbotSetAsidePersistenceTests.cs`.
