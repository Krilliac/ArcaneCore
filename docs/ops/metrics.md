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
| `arcanecore_net_packets_in_total` / `_out_total` | counter | world socket packets |
| `arcanecore_net_bytes_in_bytes_total` / `_out_` | counter | world socket bytes, headers included |
| `arcanecore_gc_collections_total{generation}` | counter | `GC.CollectionCount` |
| `arcanecore_gc_heap_size_bytes`, `arcanecore_gc_allocated_bytes_total`, `arcanecore_gc_pause_time_seconds_total` | gauge/counter | GC and allocation |
| `arcanecore_threadpool_queue_length`, `arcanecore_process_working_set_bytes` | gauge | runtime |

`PerMapMetrics=false` drops the `{map,instance}` series (cardinality on a server with many instances). `Realm` adds a
`realm` label (Prometheus) or resource attribute (OTLP).

## Grafana

[`grafana/arcanecore-dashboard.json`](grafana/arcanecore-dashboard.json) is a sample dashboard (Prometheus data source,
`realm` variable): players, sessions, tick p50/p99, ticks per second, packets and bytes, the slowest maps, players and
creatures per map, GC and allocation. Import it with *Dashboards → New → Import*.
