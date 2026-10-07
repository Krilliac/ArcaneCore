# Playerbot movement and tick health

Managed players submit the ordinary build-5875 movement handlers, and observers see them the way
they see a real 1.12 client. `PlayerbotMotion` is the bot's movement "client":

- The brain and its goals only choose a route (`PlayerbotNavigation.TryAdvance` → `PlayerbotMotion.Follow`).
  A new route starts with `MSG_MOVE_START_FORWARD` at the current position, oriented at the first
  waypoint; a different route while running is one heartbeat at the current position with the new
  orientation (no stop/start).
- The motion advances every world tick for every bot (`ManagedPlayerbotFeature.Update` pumps it before
  any think), independent of `ThinkIntervalMs`, the shared `MaxActionsPerTick` budget and the 8 ms
  per-tick think cap. It sends a heartbeat every 500 ms, one at once when the route turns by more
  than ~10 degrees, and `MSG_MOVE_STOP` on arrival.
- Every packet's position is the exact route position at that packet's server time: distance is
  speed × time since the previous packet (horizontal yards), orientation is the direction of the
  segment the bot is on (normalized to [0, 2π)). A client extrapolating the previous packet therefore
  lands on the next one.
- Speed is what observers know: the run speed, or walk mode (`MOVEFLAG_WALK_MODE`, walk speed) when
  `MoveSpeed` is below the run speed. A speed/flag order is acknowledged after a heartbeat at the
  current position, and the motion continues at the acknowledged speed.
- `Stop` sends `MSG_MOVE_STOP` at the current route position (where observers already see the bot),
  never refused for lack of action budget. A route the brain stops following for
  `max(2 s, 4 × ThinkIntervalMs)` is stopped the same way. A sitting, rooted, jumping or falling bot
  stops; death (not ghost), teleports and lost forward flags end the motion without a packet.
- Standing is an admitted action before travel, so a seated player does not start moving while
  food/drink remains active.

Loops: `PlayerbotLoopDetector` gives up (a) a back-and-forth between destinations — three returns to
a destination left within 30 s (A, B, A, B, A) — and (b) running in place — at least 50 yards run in
~12-15 s without leaving a 10-yard radius. The bot stops, the destinations involved are refused by
`PlayerbotNavigation.TryPlan` for 60 s (5-yard radius), the brain drops its target and marks it
unreachable for 30 s, and the goals choose again. Grind targets whose path cannot be planned are also
skipped for 30 s instead of being re-picked every think, and exploration keeps roughly its facing
(±60°) instead of turning ~137° per leg. `.playerbot inspect` shows `following:` and `loops:`.

Root causes of the live reports (2026-10-07, Bramblepaw and friends):

| Report | Cause before | Now |
|---|---|---|
| Sliding (also while sitting to drink) | Distance came from the think interval, not from time since the last packet; a think that did not advance the route lost its time, and `Stop` projected at most 1000 ms. Observers had run further and the STOP pulled the bot back without a forward flag — a visible slide, after which it sat. | Packet positions are a function of server time; STOP lands where observers extrapolated. |
| Rubber-banding | Same lost time (observers ahead, snap back each heartbeat); heartbeat orientation was the chord of the past step, so after a corner observers extrapolated the wrong way for a whole heartbeat; acks carried the stale position. | Exact positions, segment orientation, a packet at each turn, acks flushed first. |
| Choppy (slow / fast, same average) | Heartbeat spacing followed think timing and early-return branches (1 s gaps carrying 0.5 s of distance), and the final heartbeat covered less than its interval. 100 ms thinks only made the errors smaller. | Cadence and distance come from the world tick and server time. |
| Circles / back-and-forth (Bramblepaw) | A heartbeat refused by the exhausted shared budget made `TryAdvance` fail, the route was dropped and Explore chose a new direction ~137° away; competing goals re-routed every think with nothing noticing. | Movement is not budgeted; loops are detected and given up; exploration keeps its heading. |

Evidence: `PlayerbotMovementWireTests` (scenario harness, manual clock) decodes what an observing bot
receives and checks each packet against the previous one's extrapolation, packet gaps, flags,
orientation range, time order, the final STOP and the loop give-up; the pre-fix run of the same tests
fails all seven (logs under `_logs/bot-movement/red-movement-wire.txt` in the lane). Visual
acceptance in the original client is still required.

Town travel is independent of the service-request cooldown. Vendor/trainer requests retain
their throttle while approach movement continues at normal decision intervals. Rest, pending
loot, active casts and combat linger retire movement rather than leaving observers predicting
forward travel. Taking a town or quest goal retires a previous idle combat route.

`.server info` retains version, population and uptime, and adds target/observed tick rate,
frame timing, tick-work mean/p99/max, distinct frame/work overrun counts, command backlog,
process working set, managed heap, allocation per tick and managed-bot count. Rates come from
measured frame intervals, not inverse simulation-work duration. Empty samples are unavailable.
`.playerbot inspect` also exposes movement flags, stand state and movement timestamp.

The world loop preserves actual elapsed time. Its posted-command phase is bounded by
`World:MaxCommandsPerTick` (default1024) and `World:CommandTimeBudgetMs` (default5; zero disables
the time bound). FIFO commands beyond that budget remain queued for later ticks. Shutdown
retains the full drain. Both budgets support ordinary config reload; command counts must be
positive, and the time budget follows the existing nonnegative reload policy. This prevents a sustained
producer or self-posting action from starving map simulation; it cannot remove OS memory stalls
or preempt one long-running command.

These changes address movement presentation and scheduling. Terrain, collision-model and
navigation-mesh data remain separate requirements for reliable routes throughout the world.
Original-client visual acceptance and loaded-area navigation must be observed independently
of packet and lifecycle test results.
