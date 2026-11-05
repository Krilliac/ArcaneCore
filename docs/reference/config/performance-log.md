# PerformanceLog options

8 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `PerformanceLog`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `DetailLinesPerMapPerInterval` | int | `1` | a whole number, -2147483648 to 2147483647 | World | start | With a summary interval: the most detail lines written per map instance (and for the world) in one interval; the rest are counted in the summary (0: none). |
| `SlowMapUpdate` | int | `100` | a whole number, -2147483648 to 2147483647 | World | start | Log one map's update longer than this many ms. |
| `SlowMapUpdateDetail` | int | `200` | a whole number, -2147483648 to 2147483647 | World | start | With a summary interval: one map's update longer than this many ms also gets its own detail line (0: never). |
| `SlowPackets` | int | `20` | a whole number, -2147483648 to 2147483647 | World | start | Log one in-world packet handler longer than this many ms. |
| `SlowWorldUpdate` | int | `100` | a whole number, -2147483648 to 2147483647 | World | start | Log a world frame (or tick, see SlowWorldUpdateMeasure) longer than this many ms. |
| `SlowWorldUpdateDetail` | int | `250` | a whole number, -2147483648 to 2147483647 | World | start | With a summary interval: a world frame (or tick) longer than this many ms also gets its own detail line (0: never). |
| `SlowWorldUpdateMeasure` | SlowWorldUpdateMeasure | `FrameInterval` | FrameInterval \| TickDuration | World | start | What SlowWorldUpdate is compared with. Defaults to the retail frame interval. Values: `FrameInterval`, `TickDuration`. |
| `SummaryIntervalSeconds` | int | `60` | a whole number, -2147483648 to 2147483647 | World | start | Seconds between the slow-update summary lines. Slow world frames and slow map updates are counted over the interval and written as one summary (counts, p50/p95/max, top contributors per map, host verdicts) instead of one line each; only the updates over SlowWorldUpdateDetail / SlowMapUpdateDetail still get their own line, a few per map per interval. 0 writes every slow update as its own line (the vmangos behaviour) and ignores the Slow*Detail keys and DetailLinesPerMapPerInterval. |
