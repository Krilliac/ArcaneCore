# Asynchronous long-distance bot navigation acceptance

Implementation baseline: `f67b41a15a5791d6b4850f7ea91d4067aaeba3cc` (2026-10-11). This acceptance specification covers a bounded navigation compute executor and its first production consumer, long-distance static quest/objective travel. It does not establish multi-process clustering or remove every synchronous navigation caller.

## Observable behavior

The world owner captures detached navigation facts and submits bounded work. A worker uses private geometry/query state. Completion is consumed and validated by the current world owner. Movement and all live gameplay decisions remain on that owner.

Waiting for navigation is an explicit pending state. Pending, overloaded, superseded, cancelled, failed infrastructure work, and stale results must not count as proof that a target is unreachable, increment failed route decisions, abandon a quest, or trigger a recovery teleport. Only a current, completed navigation verdict may use the established gameplay failure policy.

## Automated checks

| Criterion | Required evidence | Current status |
|---|---|---|
| Blocked navigation cannot block the world tick | `GatedFarDestination_HoldsPlanningIntentUntilWorkerCompletes` exercises a gated destination; real tick-latency evidence is still required | Partial: fixture gate passes; real WorldRuntime timing pending |
| Pending is distinct from unreachable | `GatedFarDestination_HoldsPlanningIntentUntilWorkerCompletes` keeps the intent pending without failure/backoff | Pass in focused fixture; real terrain pending |
| Request and result bounds hold | `CapacityIncludesActiveQueuedAndUnconsumedResults` | Pass in focused fixture |
| Supersession and lifecycle changes reject stale results | `ChangingPreferredEntry_SupersedesPendingPlanAndReleasesLease`, map/instance, death, content revision, and stopping tests | Partial: covered cases pass; geometry-generation and relog coverage pending |
| Query state is private | `AMapChangeCreatesANewPrivatePathfinderContext` and executor request-value contract | Pass in focused fixture |
| Current route semantics remain valid | Current tests cover plan retention and bounded answers; no complete serial geometry oracle is recorded here | Partial; synthetic oracle and real terrain pending |
| Search cooperates with cancellation | `CancellationPublishesDistinctCanceledResultAndReleasesAfterConsume` | Pass in focused fixture |
| Shutdown releases owned work | `PlanCachesHaveBoundedAdmissionAndAreReleasedOnShutdown` plus stopping-destination coverage | Pass in focused fixture |
| Existing workflows remain valid | Reported release build 8: Game 3/3 and World 83/83; native mock lifecycle and client acceptance remain separate | Partial: focused automated suites pass; native/real-client acceptance pending |

Local evidence reported for release build 8 on 2026-10-12: Game 3/3, World 83/83, Data 26/26, and Docs 98/98. These are SQLite/fixture results; they do not establish provider parity, geometry assets, performance, native-client behavior, or deployment acceptance. Reproduce with the repository's focused Game, World, Data, and Docs filters after the build runner has resources.

Performance evidence must use matching inputs, source SHA, assets, population, GC mode and host load. Report queue wait and compute/owner-validation time separately from tick duration. Historical 0.7–1.9-second spikes are a motivation, not proof of current performance or a new improvement. Missing terrain assets and skipped tests must be recorded.

`World:Playerbots:AsyncNavigationEnabled` is a startup option. The private navigation executor is created when the playerbot feature attaches, so changing this boolean through `.reload config` keeps the running value and reports that a restart is required. Enable or disable it in the startup configuration before attaching the feature.

## Real build-5875 acceptance

On a developer-owned test server, observe long quest trips across loaded/unloaded terrain, successful partial legs, indoor paths, and unreachable targets. Change the bot's goal and map while a route is pending; verify that an old completion never moves the bot. Relog and stop while work is pending; verify no late actuation. Confirm ordinary movement remains smooth and quest/backoff behavior is unchanged for genuine no-path results.

Real-client acceptance is pending until these checks are observed with the actual 1.12.1 client. Automated fixtures and the native mock do not substitute for it. Production deployment is a separate operation.

## Bot path jobs (`World:Playerbots:PathJobs`)

Live evidence, 2026-10-24: one bot think took 2 168 ms, and 2 158 ms of it was one capped 32 768-node navigation search made on the world thread by a town plan. Far bot plan queries (more than `PathJobSyncNodeThreshold`, 2 048 nodes by default, which is a goal more than 100 yards away) can now run on the shared `NavJobSystem` pool, as creature paths do. This is separate from the quest-travel executor above (`AsyncNavigationEnabled`), which is unchanged.

- **What waits:** only queries made inside `PlanExact`, `PlanToward`, `PlanFar` and `PlanWander` (their callers already handle `PlanOutcome.Pending`) that already accept a pending answer (`LoadTiles` and `NonBlockingTiles`). A job that is queued or running answers exactly like a navigation tile still being read: `PathType.TilesPending`, no verdict, no route-watch or anomaly record, the goal is kept and planned again on a later think. The finished raw mesh answer is applied on the world thread, where risk, detours, smoothing, blacklist, dead-end pins and the anomaly checks run as for a synchronous answer.
- **What does not wait:** the bool planners (`TryPlan`, `TryPlanToward`, `TryPlanUnguarded`, `TryChain`, retreat, risk, elevator, tram, water), chase and trial plans, follow-drop, the battleground and ship probes, `OnWalkableMesh`, flying movers, maps without a shared tiled search, and queries made outside a think.
- **Pending is never unreachable.** A job wait does not count toward the 15 s tile bound (`TileWait.Hold`). It ends by escalation: after three cancellations or stale answers of one query, a fault, or `PathJobWaitMaxMs` of wall time, the next ask runs once on the world thread with the `SyncPathQueryMaxMs` deadline when one is set (0: unbounded, as before the branch; `nav/job-escalated`). With 0 an escalated search is therefore unbounded on the world thread, so "escalations rare" is a hard acceptance criterion below. A wait longer than 5 s is reported once (`nav/job-wait-long`).
- **Moving destinations:** a finished answer within 15% of the distance (10..40 yd) of the destination asked now is applied as a route cut short (`PathType.Short`), spliced from where the bot stands if it walked at most `MovedYards` (2 s at `MoveSpeed`, at least 15 yd) along it, and a refresh job for the new destination is queued in the same think. Independent of `PathJobs` and always on: a follower keeps walking its current route while a re-plan is pending (it also covers a navigation tile still being read).
- **Stale answers are dropped, never applied:** the bot left the route of a main query (it stands more than 5 yd from the legs it can have walked, or farther than `MovedYards` from where the job was asked; a fallback query from beside the bot or a detour point keeps the one-yard rule), the bot or its map object changed, the pathfinder object or dataset changed, the key changed, or the owner changed a navigation tile under the search (job canceled). Logout, removal and detach call `Forget`, which abandons queued jobs; unclaimed answers are swept after 10 s.
- **Limits:** one job per bot (`PathJobsMaxPerBot`), `PathJobsMaxInFlight` jobs and `PathJobsMaxInFlightNodes` search nodes together. A bot past a limit waits in a first-come line and answers pending; a bot held that long still escalates to one search on the world thread after `PathJobWaitMaxMs`, held to `SyncPathQueryMaxMs` (so the enable step must set it above 0). A worker search is bounded by `PathJobWorkerMaxMs`.
- **World-thread deadline (off by default, `SyncPathQueryMaxMs` = 0 until the 200-bot benchmark has run):** when set (10..60000 ms), a search that still runs on the world thread is held to it. A cut search is capped like one that ran out of nodes and the capped answer stands (`nav/sync-deadline` names the caller still to migrate); a plan then reads it as a refusal, so the outcome of a far plan depends on host load while it is on.
- **Detached searches cannot read tiles.** They report the Detour tiles they wanted and did not have; the owner reads them without blocking (`ISharedTiledPathSearch.ResolveSharedMisses`) and a whole path is applied whatever was missed, as the synchronous rule does.
- **Telemetry:** the slow-think line has an `off-thread:` section (jobs submitted, waiting, applied, worker time, largest job) kept apart from the world-thread query time; `nav.bot_path_job_wait` is the submit-to-apply latency histogram; a once-a-minute summary line lists the counters.

Automated: `PlayerbotPathJobTests` (deterministic: a manual runner and clocks; RED without the routing, GREEN with it), `NavMeshQueryDeadlineTests`, `NavMeshSharedMissedTileTests`. Not covered by them: a map or instance change of a pending bot (the check is `ReferenceEquals(player.Map, job map)`), pool queue behaviour under real load, and the 200-bot terrain benchmark and live acceptance (no slow think above 100 ms from path queries; canceled ratio under 10 %; stale ratio under 20 %; escalations rare; creature `PathJobsRejected` does not rise; quest, back-off and `nav/plan-*` anomaly rates unchanged), which remain pending. Rollout: `PathJobs` is off by default.
