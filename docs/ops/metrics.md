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
                      "OtlpEndpoint": "http://otel-collector:4318/v1/metrics", "Realm": "ArcaneCore" } }
```

- `Prometheus`: an `HttpListener` serves the text format at `PrometheusPrefix` (world default port 9464, realm 9465).
  Use `http://+:PORT/metrics/` in a container; on Windows a non-localhost prefix needs a `netsh http add urlacl`.
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
| `arcanecore_db_save_queue_depth` | gauge | character snapshots queued or being written (TC `db_queue_*`) |
| `arcanecore_map_players` / `_creatures` / `_objects` `{map,instance}` | gauge | per map, sampled every `MapSampleIntervalSeconds` on the world thread (TC `map_creatures`, `map_players`) |
| `arcanecore_map_update_time_mean_milliseconds` / `_max_` `{map,instance}` | gauge | per map over the sample period (TC `map_update_time`) |
| `arcanecore_net_packets_in_total` / `_out_total` | counter | world and logon socket packets |
| `arcanecore_net_bytes_in_bytes_total` / `_out_` | counter | world and logon socket bytes, headers included |
| `arcanecore_net_opcode_packets_in_total` / `_out_total` `{protocol,opcode}` | counter | packets per opcode (see below) |
| `arcanecore_net_opcode_bytes_in_bytes_total` / `_out_` `{protocol,opcode}` | counter | bytes per opcode, headers included |
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

## Grafana

[`grafana/arcanecore-dashboard.json`](grafana/arcanecore-dashboard.json) is a sample dashboard (Prometheus data source,
`realm` variable): players, sessions, tick p50/p99, ticks per second, packets and bytes by job (world and logon daemon),
the slowest maps, players and creatures per map, GC and allocation, and the per-opcode panels (packets by protocol, top
inbound and outbound world opcodes, top world opcodes by bytes out, logon commands, unknown opcodes). Import it with *Dashboards → New → Import*.
