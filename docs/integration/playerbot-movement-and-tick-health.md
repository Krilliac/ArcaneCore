# Playerbot movement and tick health

Managed players submit the ordinary build-5875 movement handlers. A route starts with
`MSG_MOVE_START_FORWARD` at the current position, continues with heartbeats, and stops through
`MSG_MOVE_STOP`. Standing is an admitted action before travel, so a seated player does not
start moving while food/drink remains active. A depleted shared action budget retains a pending
stop for the next control update.

A stop uses the safe current position or a bounded, collision-checked projection along the
actual retained route. It accounts for movement since the last heartbeat without directly
relocating the player. Complete, blocked or stale route state still stops prediction at the
safe current position. Route progress commits only after normal packet admission. Teleports,
death and server-owned movement changes invalidate prior projection data.

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
