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
