# Ops options

60 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `Ops:Diagnostics`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Dumps:Directory` | string | `(empty)` | text | World | start | The directory the dump files are written to. Empty (default) means `dumps` beside the log file (the resolved `Logging:ArcaneCore:File:Path`, so `logs/dumps` by default). It is created on the first dump. |
| `Dumps:Enabled` | bool | `false` | true \| false | World | start | `true` lets an administrator write a dump of the running world with `.debug dump`. Default false, because a dump holds everything in memory: session keys, password material in flight, character data. Keep the dump directory private and delete the files when the analysis is done. |
| `Dumps:MinIntervalSeconds` | int | `600` | a whole number, -2147483648 to 2147483647 | World | start | The least seconds between two dumps, so a repeated command cannot suspend the process again and again or fill the disk. 0 disables the limit. Default 600. |

## `Ops:Metrics`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Enabled` | bool | `false` | true \| false | Both | start | The master switch. `false`: no listener is attached, so every instrument stays disabled and costs one branch. Default false (TrinityCore `Metric.Enable = 0`). |
| `Exporter` | MetricsExporter | `Prometheus` | Prometheus \| Otlp \| Both | Both | start | Where the metrics go. Default Prometheus. Values: `Prometheus` (scrape endpoint), `Otlp` (push to an OpenTelemetry collector), `Both`. Values: `Prometheus`, `Otlp`, `Both`. |
| `MapSampleIntervalSeconds` | int | `10` | a whole number, -2147483648 to 2147483647 | Both | start | Seconds between per-map samples (players, creatures, objects, update time) taken on the world thread. 1-3600. Default 10 (TrinityCore `Metric.OverallStatusInterval = 10`). |
| `OtlpEndpoint` | string | `http://localhost:4318/v1/metrics` | text | Both | start | The OTLP/HTTP metrics endpoint the push exporter posts JSON to. Default `http://localhost:4318/v1/metrics`. |
| `OtlpIntervalSeconds` | int | `15` | a whole number, -2147483648 to 2147483647 | Both | start | Seconds between OTLP pushes. 1-3600. Default 15 (TrinityCore `Metric.Interval = 1` pushes every second to InfluxDB; a collector batches, so a longer period suffices). |
| `PerMapMetrics` | bool | `true` | true \| false | Both | start | `false`: no per-map series, only the world totals (bounds the label cardinality on a server with many instances). Default true. |
| `PrometheusPrefix` | string | `http://localhost:9464/metrics/` | text | Both | start | The World daemon's Prometheus scrape listener prefix (an `HttpListener` prefix ending in `/`). Use `http://+:9464/metrics/` to listen on every interface (a container). The Realm daemon does not read this key: it reads `RealmPrometheusPrefix`. Default `http://localhost:9464/metrics/`. |
| `Realm` | string | `(empty)` | text | Both | start | A `realm` label added to every series (TrinityCore tags its metrics with the realm name). Empty: no label. Default empty. |
| `RealmPrometheusPrefix` | string | `http://localhost:9465/metrics/` | text | Both | start | The Realm daemon's Prometheus scrape listener prefix, so a Realm and a World started from one shared appsettings.json do not both bind the same port. It never falls back to `PrometheusPrefix`; setting both to the same prefix makes the second daemon to start log an error and serve no metrics. Default `http://localhost:9465/metrics/`. |

## `Ops:Observability`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `ClientRejections:BurstPerSession` | int | `20` | a whole number, -2147483648 to 2147483647 | World | start | Lines a session may log in a burst before the refill rate applies. 1-1000. Default 20. |
| `ClientRejections:Enabled` | bool | `true` | true \| false | World | start | `false`: no refusal is decoded or logged and the send path does no extra work. Default true. |
| `ClientRejections:LogSilentDrops` | bool | `true` | true \| false | World | start | `true`: handlers that return without answering the client log the reason (at Debug for a bot, Information for a player). Default true. |
| `ClientRejections:RefillPerMinute` | int | `60` | a whole number, -2147483648 to 2147483647 | World | start | Lines a session earns back per minute once its burst is spent; a refusal over the limit is counted and summarised in the next line that is written. 1-6000. Default 60. |
| `DuplicatePackets:CaptureStacks` | bool | `true` | true \| false | World | start | `true`: the warning carries a compact call stack of the second send, and of the first as well once the opcode has duplicated before in this process. Default true. |
| `DuplicatePackets:Enabled` | bool | `true` | true \| false | World | start | `false`: nothing is hashed and the send path does no extra work. Default true. |
| `DuplicatePackets:IncludeBots` | bool | `false` | true \| false | World | start | `true`: playerbot sessions are checked as well. Default false (bots are excluded). |
| `DuplicatePackets:RingSize` | int | `16` | a whole number, -2147483648 to 2147483647 | World | start | Recent packets remembered per session; older ones are overwritten. 4-256. Default 16. |
| `DuplicatePackets:WarnBurstPerSession` | int | `3` | a whole number, -2147483648 to 2147483647 | World | start | Warnings a session may log in a burst. 1-100. Default 3. |
| `DuplicatePackets:WarnPerMinute` | int | `6` | a whole number, -2147483648 to 2147483647 | World | start | Warnings a session earns back per minute after its burst. 1-600. Default 6. |
| `DuplicatePackets:WindowMs` | int | `100` | a whole number, -2147483648 to 2147483647 | World | start | Two identical packets closer together than this many milliseconds are a duplicate (a world tick is 50 ms). 1-5000. Default 100. |

## `Ops:Watchdog`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `CheckIntervalMs` | int | `1000` | a whole number, -2147483648 to 2147483647 | Both | start | How often the monitoring thread wakes to run every check (hang detection, heartbeat, probes). 100-60000. Default 1000. |
| `Counters:ChangedOnly` | bool | `false` | true \| false | Both | start | `true`: counters that have not changed since the last dump are left out of it. Default false. |
| `Counters:DumpIntervalSeconds` | int | `300` | a whole number, -2147483648 to 2147483647 | Both | start | Seconds between dumps. 0 disables the dump (the registry still counts). Default 300. |
| `Enabled` | bool | `true` | true \| false | Both | start | The master switch. `false`: the monitoring thread is not started, nothing in this section runs, no heartbeat is sent. Default true. |
| `Heartbeat:FilePath` | string | `(empty)` | text | Both | start | The liveness file for `File` mode; relative paths are under the content root. Required in that mode (the daemon refuses to start without it). |
| `Heartbeat:IntervalSeconds` | int | `0` | a whole number, -2147483648 to 2147483647 | Both | start | Seconds between beats. 0 = half of systemd's `WATCHDOG_USEC` when it is set, otherwise 10. Default 0. |
| `Heartbeat:Mode` | HeartbeatMode | `Auto` | Auto \| None \| Systemd \| File \| Stdout | Both | start | Where the heartbeat goes. Default Auto. Values: `Auto`, `None`, `Systemd`, `File`, `Stdout`. |
| `Heartbeat:WarnIntervalSeconds` | int | `30` | a whole number, -2147483648 to 2147483647 | Both | start | Least seconds between two "heartbeat withheld" warnings. Default 30. |
| `HostLoad:Enabled` | bool | `true` | true \| false | Both | start | `false`: nothing is sampled and the hang, overrun and slow-map lines carry no host-load text. Default true. |
| `HostLoad:HistorySeconds` | int | `120` | a whole number, -2147483648 to 2147483647 | Both | start | How many seconds of samples (one per watchdog check) are kept to difference against; a slow window longer than this is measured from the oldest sample. 10-3600. Default 120. |
| `Memory:Action` | MemoryPressureAction | `None` | None \| Log \| Collect \| Stop | Both | start | What happens when the managed heap reaches `ActionHeapBytes`. Default None (log only; `Collect` pauses the world thread for a blocking compacting gen2 collection, `Stop` ends the process with exit code 1). Values: `None`, `Log`, `Collect`, `Stop`. |
| `Memory:ActionCooldownSeconds` | int | `300` | a whole number, -2147483648 to 2147483647 | Both | start | Least seconds between two actions (a Stop fires once). Default 300. |
| `Memory:ActionHeapBytes` | long | `0` | a whole number, -9223372036854775808 to 9223372036854775807 | Both | start | The heap size in bytes at which `Action` fires. 0 disables the action whatever `Action` says. Default 0. |
| `Memory:Enabled` | bool | `true` | true \| false | Both | start | `false`: no sample is taken and no action can fire. Default true. |
| `Memory:FullGcNotifications` | bool | `false` | true \| false | Both | start | `true`: register for full-GC approach/completion notifications where the runtime allows it (concurrent GC refuses; then this logs once and only polling runs). Default false. |
| `Memory:GrowthLogBytes` | long | `16777216` | a whole number, -9223372036854775808 to 9223372036854775807 | Both | start | An Information line when the gen2 or large-object-heap high-water mark grows by at least this many bytes since the last line. 0 disables growth lines. Default 16777216 (16 MiB). |
| `Memory:SampleIntervalSeconds` | int | `10` | a whole number, -2147483648 to 2147483647 | Both | start | Seconds between heap samples. 1-3600. Default 10. |
| `Memory:WarnHeapBytes` | long | `0` | a whole number, -9223372036854775808 to 9223372036854775807 | Both | start | A rate-limited warning when the managed heap is at least this many bytes. 0 disables the check. Default 0. |
| `Memory:WarnIntervalSeconds` | int | `60` | a whole number, -2147483648 to 2147483647 | Both | start | Least seconds between two memory warnings of the same kind. Default 60. |
| `Memory:WarnLoadPercent` | int | `85` | a whole number, -2147483648 to 2147483647 | Both | start | A rate-limited warning when the runtime's memory load (process and machine) reaches this percentage of the available memory; the GC's own high-load threshold is 90. 0 disables the check. Default 85. |
| `ThreadPool:CriticalDelayMs` | int | `2000` | a whole number, -2147483648 to 2147483647 | Both | start | A probe that waits longer than this (milliseconds), or has not run at all for this long, is a critical line. Default 2000. |
| `ThreadPool:Enabled` | bool | `true` | true \| false | Both | start | `false`: nothing is queued. Default true. |
| `ThreadPool:ProbeIntervalSeconds` | int | `5` | a whole number, -2147483648 to 2147483647 | Both | start | Seconds between probes. 1-3600. Default 5. |
| `ThreadPool:WarnDelayMs` | int | `200` | a whole number, -2147483648 to 2147483647 | Both | start | A probe that waits longer than this (milliseconds) for a pool thread is a rate-limited warning with the pool counts. Default 200. |
| `ThreadPool:WarnIntervalSeconds` | int | `30` | a whole number, -2147483648 to 2147483647 | Both | start | Least seconds between two starvation warnings. Default 30. |
| `TickMonitor:Action` | TickHangAction | `None` | None \| Log \| Stop \| FailFast | Both | start | What happens when a tick has run longer than `ActionHangMs`. Default None (log only; `Stop` stops the host with exit code 1; `FailFast` terminates immediately with exit code 70 so a supervisor restarts a deadlocked world thread). Values: `None`, `Log`, `Stop`, `FailFast`. |
| `TickMonitor:ActionHangMs` | int | `0` | a whole number, -2147483648 to 2147483647 | Both | start | A frame running longer than this triggers `Action` (when `Action` is not `TickHangAction.None` and the host-load sampler does not blame GC pauses or machine starvation). 0 disables the action whatever `Action` says. Default 0. |
| `TickMonitor:BudgetMs` | int | `0` | a whole number, -2147483648 to 2147483647 | Both | start | A frame longer than this is an overrun. 0 = twice the configured tick interval (100 ms at the retail 50 ms tick), tolerating sleep jitter. Default 0. |
| `TickMonitor:Enabled` | bool | `true` | true \| false | Both | start | `false`: no tick is recorded, no overrun or hang is reported and the heartbeat does not depend on the tick. Default true. |
| `TickMonitor:GatesHeartbeat` | bool | `true` | true \| false | Both | start | `true`: the heartbeat is withheld while a tick has run longer than `HangMs`, so a supervisor watchdog restarts a hung world. Default true. |
| `TickMonitor:HangMs` | int | `2000` | a whole number, -2147483648 to 2147483647 | Both | start | A frame longer than this is a hang: one critical line when it is still running and one when it completes. 0 disables hang detection. Default 2000. |
| `TickMonitor:RingCapacity` | int | `4096` | a whole number, -2147483648 to 2147483647 | Both | start | Ring capacity in ticks (rounded up to a power of two, 16-1048576); the percentiles are over the last capacity-1 ticks. Default 4096 (3.4 minutes at 50 ms). |
| `TickMonitor:StackCaptureDirectory` | string | `logs/hangstacks` | text | Both | start | Where the hang stack captures are written (relative paths are under the working directory, like the log files). Default `logs/hangstacks`. |
| `TickMonitor:StackCaptureIntervalSeconds` | int | `300` | a whole number, -2147483648 to 2147483647 | Both | start | Least seconds between two hang stack captures (a capture briefly samples every thread of the process). 10-86400. Default 300. |
| `TickMonitor:StackCaptureTool` | string | `(empty)` | text | Both | start | A stack tool run once per hung tick (past `HangMs`) against this process, out of process and off the world thread, whose output (every managed thread's stack, the world thread's among them) goes to `StackCaptureDirectory`; it is called as `&lt;tool&gt; report -p &lt;pid&gt;` (dotnet-stack). `%VAR%` references are expanded. Empty, or a file that does not exist: no capture. Default empty. |
| `TickMonitor:SummaryIntervalSeconds` | int | `300` | a whole number, -2147483648 to 2147483647 | Both | start | Seconds between periodic frame-time summaries (p50/p99/max, overruns) at Information level. 0 disables the summary. Default 300. |
| `TickMonitor:WarnIntervalSeconds` | int | `30` | a whole number, -2147483648 to 2147483647 | Both | start | Least seconds between two overrun warnings; overruns in between are counted and reported with the next warning. Default 30. |
