# Server metrics (`Ops:Metrics`)

Both daemons can export server metrics for Prometheus and/or an OpenTelemetry collector. The instruments are plain
`System.Diagnostics.Metrics` meters (`ArcaneCore.Net`, `ArcaneCore.Runtime`, `ArcaneCore.World`), so `dotnet-counters`
and any `MeterListener` see them as well. The model is TrinityCore `src/common/Metric` (`Metric.Enable`,
`Metric.Interval`, `Metric.OverallStatusInterval`, `TC_METRIC_TIMER("world_update_time")`, `"map_update_time"`,
`"online_players"`, `"db_queue_*"`), which pushes to InfluxDB; vmangos and cMaNGOS have no equivalent.

**Off by default.** With `Ops:Metrics:Enabled=false` no listener is attached: every instrument stays disabled (one
branch per packet), and the world loop pays one null check per tick and per map. The section is read at start
(restart-only). Keys and defaults: [configuration reference](../reference/configuration.md#opsmetrics).

```json
"Ops": { "Metrics": { "Enabled": true, "Exporter": "Both", "PrometheusPrefix": "http://+:9464/metrics/",
                      "RealmPrometheusPrefix": "http://+:9465/metrics/",
                      "OtlpEndpoint": "http://otel-collector:4318/v1/metrics", "Realm": "ArcaneCore" } }
```

`Realm` is the `realm` label value, unrelated to the Realm daemon's endpoint (`RealmPrometheusPrefix`).

- `Prometheus`: an `HttpListener` serves the text format. Each daemon has its own key, so a Realm and a World started
  from one profile directory (one shared `appsettings.json`) both serve metrics out of the box:

  | Daemon | Key | Default |
  | --- | --- | --- |
  | ArcaneCore.World | `Ops:Metrics:PrometheusPrefix` | `http://localhost:9464/metrics/` |
  | ArcaneCore.Realm | `Ops:Metrics:RealmPrometheusPrefix` | `http://localhost:9465/metrics/` |

  The Realm never reads `PrometheusPrefix` (and never falls back to it); the World never reads `RealmPrometheusPrefix`.
  Setting both keys to the same prefix is allowed, but then the daemon that starts second logs
  `metrics: the Prometheus endpoint ... could not start; metrics are not served`, naming the other daemon and the key
  to change, and serves no metrics. Use `http://+:PORT/metrics/` in a container; on Windows a non-localhost prefix needs
  a `netsh http add urlacl`.
- `Otlp`: OTLP/HTTP JSON (`ExportMetricsServiceRequest`, cumulative temporality) is posted every `OtlpIntervalSeconds`.
  A failing collector logs one warning until it recovers; metrics never stop the daemon.
- Both exporters read the same in-process aggregation (`MetricsStore`), off the world thread.

## Series

| Prometheus name | Kind | Source |
| --- | --- | --- |
| `arcanecore_world_tick_duration_milliseconds` | histogram | world tick body (TC `world_update_time`) |
| `arcanecore_world_map_update_duration_milliseconds` | histogram | all maps in one tick |
| `arcanecore_world_ticks_total` | counter | world ticks |
| `arcanecore_world_tick_allocated_bytes_total` | counter | bytes the world thread allocated in ticks |
| `arcanecore_world_sessions` | gauge | authenticated world sessions |
| `arcanecore_world_players_online` | gauge | characters in the world (TC `online_players`) |
| `arcanecore_world_maps_loaded` | gauge | maps and instances at the last sample |
| `arcanecore_playerbots_split_brain` | gauge | 1 while this world stopped writing playerbot rows because another world process changed them (`.playerbot splitbrain`, [playerbots](../areas/playbots.md#another-world-on-the-same-database)), otherwise 0 |
| `arcanecore_nav_tile_installs_total` | counter | navigation mesh tiles installed on the thread that needed them: path-query reads and grid tile loads, nearly all on the world thread; the slow-map-update line shows the same count per update |
| `arcanecore_nav_tile_install_time_milliseconds_total` | counter | time those installations took (a tile read under disk contention can take seconds) |
| `arcanecore_creature_path_straight_fallbacks_total` | counter | authored creature routes (waypoint, point, formation, scripted move) walked along a straight line because the navmesh had no path |
| `arcanecore_creature_path_no_path_total` | counter | creature path searches that found no path (chase stands, flee/wander wait, home teleports). A chaser standing at an unreachable target searches every 100 ms, so it adds about 10 per second to this counter |
| `arcanecore_creature_path_deferred_total` | counter | creature path requests put off to a later update because the per-update search budget was spent, or whose tiles were still being read (`TilesPending`) |
| `arcanecore_creature_path_home_teleports_total` | counter | evade returns done by teleport because no whole mesh path led home |
| `arcanecore_creature_drape_shared_total` | counter | creature leg ground drapes answered from loaded tiles outside the world's serial gate (parallel maps phase, ungated wander legs) |
| `arcanecore_creature_drape_gate_fallbacks_total` | counter | creature leg ground drapes that needed a tile or navmesh lookup the shared drape may not do and ran again inside the serial gate; the hit rate is shared / (shared + fallbacks) |
| `arcanecore_db_save_queue_depth` | gauge | characters with a snapshot queued or being written; the queue keeps one per character (TC `db_queue_*`) |
| `arcanecore_db_save_queue_oldest_age_milliseconds` | gauge | age of the longest-waiting character snapshot, queued or in its write; 0 when none (the `World:SaveQueueWarnAgeMs` check, [persistence](../areas/persistence.md)) |
| `arcanecore_map_players` / `_creatures` / `_objects` `{map,instance}` | gauge | per map, sampled every `MapSampleIntervalSeconds` on the world thread (TC `map_creatures`, `map_players`) |
| `arcanecore_map_update_time_mean_milliseconds` / `_max_` `{map,instance}` | gauge | per map over the sample period (TC `map_update_time`) |
| `arcanecore_net_packets_in_total` / `_out_total` | counter | world and logon socket packets |
| `arcanecore_net_bytes_in_bytes_total` / `_out_` | counter | world and logon socket bytes, headers included |
| `arcanecore_net_opcode_packets_in_total` / `_out_total` `{protocol,opcode}` | counter | packets per opcode (see below) |
| `arcanecore_net_opcode_bytes_in_bytes_total` / `_out_` `{protocol,opcode}` | counter | bytes per opcode, headers included |
| `arcanecore_net_opcode_dev_dropped_total{protocol,opcode,traffic}` | counter | stripped developer, GM and cheat opcodes (docs/reference/opcodes.md, `dropped (dev)`) a client sent and the session dropped unread, by reference name and traffic kind (`player`, `bot`, `staff`, `tool`); not counted as unhandled. `.server wiring` shows the total |
| `arcanecore_net_opcode_unhandled_total{protocol,opcode}` | counter | client packets that had no registered handler and were dropped (`opcode` is the reference name, or `unknown` for values without one); the first of each opcode is also a log Warning, and `.server wiring` lists them (docs/ops/wiring-audit.md) |
| `arcanecore_playerbots_packets_skipped_total{kind}` | counter | what lean bot fidelity did not do (`World:Playerbots:ClientFidelity`, docs/areas/playbots.md): `kind` = `update_block` (SMSG_UPDATE_OBJECT blocks not built for a bot viewer), `ring_packet` / `ring_byte` (received packets, and their payload bytes, not copied into a bot session ring); also on `.server info` |
| `arcanecore_playerbot_chat_screening_timeouts_total{kind}` | counter | bot chat safety patterns that ran out of time (`kind`: `input` screening a player's line, `output` screening a model reply, `strip` removing personal data before sending or from a flag excerpt; docs/areas/playbots.md) |
| `arcanecore_playerbot_chat_lines_spoken_total{channel,topic}` | counter | chat lines and text emotes bots said through their own sessions (`channel`: Say, Yell, Party, Raid, RaidWarning, Battleground, Guild, Whisper, World, Emote; `topic`: the speaker's label, e.g. `reply`, `greet`, `emote-reply`; docs/areas/playbots.md, Bot speech); each is also a Debug log line |
| `arcanecore_playerbot_rivalry_emotes_total{kind}` | counter | emotes of the cross-faction rivalry stage (`kind`: `greet` and `rude` sent by the lane, `reply_rude` and `reply_shrug` answers to an emote aimed at a bot; docs/areas/playbots-social.md, Rivalry) |
| `arcanecore_playerbot_rivalry_optouts_total{change}` | counter | whispered `rivals off` / `rivals on` commands applied (`change`: `off`, `on`; `refused`: the store was full of opt-outs and the choice was not kept) |
| `arcanecore_playerbot_chat_lines_dropped_total{channel,reason}` | counter | bot lines not said: refused by the speech limits (`Cooldown`, `Target`, `Budget`, `Duplicate`, `BotToBot`), the safety screening (`Flagged`), the delivery checks (`NoListener`, `Unavailable`) or a full queue (`QueueFull`) |
| `arcanecore_faults_total{site,type}` | counter | exceptions caught by the server's catch-and-log sites and the crash hooks (the fault ledger, `.server faults`, [invariants](invariants.md#the-fault-ledger)) |
| `arcanecore_hotswap_total{kind,result}` | counter | live changes: `kind` data / module / code, `result` applied / rejected / failed / unchanged ([announcements](../areas/hot-swap-announcements.md)) |
| `arcanecore_announce_total{category,severity,outcome,staff_direct}` | counter | operational announcements and what became of each (`sent`, `disabled`, `below_minimum`, `off`, `staff_direct`, `rate_limited`, `duplicate`); `staff_direct` is true when the caller sent the staff line itself (Warden, anti-cheat), and the outcome `staff_direct` means the staff were told directly and there was no world line to send |
| `arcanecore_tools_packets_total{op,result}` | counter | packets the map feed's packet tools injected or sent (`op` = `inject` or `send`; `result` = `accepted` once the packet reached the session's dispatcher or send path, including an injection the dispatcher refused or whose handler faulted and that disconnected the session, or `refused` for a refused request), meter `ArcaneCore.Tools`; stays at zero unless the tools are on ([map feed](../areas/map-feed.md#packet-tools)) |
| `arcanecore_faults_distinct` | gauge | distinct fault signatures in the ledger (reset by `.server faults reset`) |
| `arcanecore_invariant_failures_total` | counter | failed `Invariant.Assert`/`Check` calls since start |
| `arcanecore_botqa_findings{kind,state}` | gauge | bot QA findings held, per finding kind (the slug of its GitHub label, e.g. `run-failed`, `tick-hang`) and publication state (`pending`, `dryrun`, `published`, `knownclosed`, `failed`); every combination is exported, zeros included. Registered only with `Ops:Metrics:Enabled` and QA enabled (docs/areas/playbots-qa.md) |
| `arcanecore_botqa_occurrences_total{kind}` | counter | occurrences folded into bot QA findings, per kind (a polled server fault counts its new occurrences; the start-up spell coverage counts none) |
| `arcanecore_playerbot_runs_total{outcome}` | counter | dungeon and raid runs that ended, when their group disbands: `outcome` = `completed`, `given_up`, `timeout` (the run ended on a reason ending in `timeout`: gather, travel, enter, regroup or run step; operator stops are `given_up`) |
| `arcanecore_playerbot_events_total{kind,outcome}` | counter | bot activities at world events ([World events](../areas/playbots-social.md)): `kind` = `festival_visit` (a bot took a place at a festival NPC and stood there), `outcome` = how the visit ended or was refused: `done` (the festival ended or the bot went on), `unreachable` (the walk to the spot was given up), `abandoned` (the bot was attacked there), `capped` (every place was full) |
| `arcanecore_playerbot_event_lines_total{kind}` | counter | lines and emotes bots made about world events: `kind` = `arrive` (the /say line at a festival spot), `emote` (a celebration text emote), `comment` (the remark that an event has started) |
| `arcanecore_playerbot_event_suppressed_total{reason}` | counter | event lines and emotes held back (lines and emotes both): `reason` = `no_listener` (no real player in range), `listener_cap` (a listener was addressed within the opening cooldown), `cut_off` (a listener is cut off by the chat safety), `ignored` (a listener ignores the bot), `emote_listener_budget` (a listener has had its festival emotes for the minute), `emote_anchor_budget` (the NPC has had its festival emotes for the minute) |
| `arcanecore_gc_collections_total{generation}` | counter | `GC.CollectionCount` |
| `arcanecore_gc_heap_size_bytes`, `arcanecore_gc_allocated_bytes_total`, `arcanecore_gc_pause_time_seconds_total` | gauge/counter | GC and allocation |
| `arcanecore_threadpool_queue_length`, `arcanecore_process_working_set_bytes` | gauge | runtime |

`PerMapMetrics=false` drops the `{map,instance}` series (cardinality on a server with many instances). `Realm` adds a
`realm` label (Prometheus) or resource attribute (OTLP).

## Per-opcode traffic

The four `arcanecore_net_opcode_*` series split the network totals by `protocol` (`world` or `logon`) and `opcode`:

- `world` labels are the build-5875 reference names (vmangos `Opcodes_1_12_1.h`, e.g. `CMSG_PING`); `logon` labels are the
  `eAuthCmd` names of vmangos `AuthCodes.h` (`CMD_AUTH_LOGON_CHALLENGE`, `CMD_REALM_LIST`, `CMD_XFER_DATA`, ...).
- Every value outside the registered set (the unassigned world value 826, anything past 827, an unknown logon command
  byte) shares the single label `opcode="unknown"`, so a client cannot create series.
- Cardinality is bounded without a toggle: at most 828 world series (827 opcodes + `unknown`) and 11 logon series per
  instrument, and only opcodes with a non-zero count are exported.
- The logon stream has no length framing: each command byte is one inbound packet and the parts read after it add bytes to
  the same command. The patch offer is one socket write counted as two outbound packets (`CMD_AUTH_LOGON_PROOF`, 2 bytes,
  then `CMD_XFER_INITIATE`, 31 bytes), as vmangos builds them.
- Cost: the instruments are observable, read only when the store collects. Recording is one enabled check when metrics are
  off, and an `Interlocked.Add` on a preallocated slot (no allocation, no tag lookup) when on.
- The logon daemon sets no `Ops:Metrics:Realm` in the docker compose file, so its series carry no `realm` label; panels
  that read `protocol="logon"` must not filter on it.

## Scraping both daemons

Prometheus scrapes two targets, one per daemon. [`apps/docker/prometheus.yml`](../../apps/docker/prometheus.yml) does
this for the docker compose stack; for a Realm and a World on one host with the default prefixes:

```yaml
scrape_configs:
  - job_name: arcanecore-world
    metrics_path: /metrics/
    static_configs: [{ targets: ["localhost:9464"] }]
  - job_name: arcanecore-realm
    metrics_path: /metrics/
    static_configs: [{ targets: ["localhost:9465"] }]
```

The dashboard's job-split panels (packets and bytes by job, logon commands) expect both jobs.

## Grafana

[`grafana/arcanecore-dashboard.json`](grafana/arcanecore-dashboard.json) is a sample dashboard (Prometheus data source,
`realm` variable): players, sessions, tick p50/p99, ticks per second, packets and bytes by job (world and logon daemon),
the slowest maps, players and creatures per map, GC and allocation, and the per-opcode panels (packets by protocol, top
inbound and outbound world opcodes, top world opcodes by bytes out, logon commands, unknown opcodes). Import it with *Dashboards → New → Import*.
