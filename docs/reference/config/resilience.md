# Resilience options

14 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `Resilience`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Database:Bootstrap:BaseDelayMs` | int | `500` | a whole number, -2147483648 to 2147483647 | Both | start | Wait after the first failed attempt, in milliseconds; it doubles after each further failure (with full jitter). |
| `Database:Bootstrap:MaxAttempts` | int | `5` | a whole number, -2147483648 to 2147483647 | Both | start | Attempts to reach the database at start, including the first; 1 means no retry. The last failure exits the process with code 6. |
| `Database:Bootstrap:MaxDelayMs` | int | `5000` | a whole number, -2147483648 to 2147483647 | Both | start | Cap on the wait between attempts, in milliseconds. |
| `Database:Bootstrap:MaxTotalDurationMs` | int | `0` | a whole number, -2147483648 to 2147483647 | Both | start | Total time budget for all attempts, in milliseconds; 0 lets the attempts alone bound it. |
| `Database:Breaker:FailureRateThreshold` | double | `0.5` | a number, '.' is the decimal point | Both | start | Share of failed calls (0..1) in the sampling window that opens the circuit once MinimumThroughput calls are in it; 0 disables this trip. |
| `Database:Breaker:FailureThreshold` | int | `5` | a whole number, -2147483648 to 2147483647 | Both | start | Consecutive failed calls that open the circuit; 0 disables this trip (the rate trip must then be on). |
| `Database:Breaker:HalfOpenMaxProbes` | int | `1` | a whole number, -2147483648 to 2147483647 | Both | start | Probe calls let through at once while half-open; one success closes the circuit, one failure re-opens it. |
| `Database:Breaker:MinimumThroughput` | int | `10` | a whole number, -2147483648 to 2147483647 | Both | start | Calls that must fall in the sampling window before the failure rate is judged. |
| `Database:Breaker:OpenDurationMs` | int | `10000` | a whole number, -2147483648 to 2147483647 | Both | start | How long an open circuit refuses every call before it lets one probe through, in milliseconds. |
| `Database:Breaker:SamplingWindowMs` | int | `10000` | a whole number, -2147483648 to 2147483647 | Both | start | Length of the sliding window the failure rate is measured over, in milliseconds. |
| `Database:Bulkhead:MaxConcurrency` | int | `0` | a whole number, -2147483648 to 2147483647 | Both | start | Guarded calls allowed to run at once per logical database; 0 disables the bulkhead (no cap). |
| `Database:Bulkhead:MaxQueue` | int | `64` | a whole number, -2147483648 to 2147483647 | Both | start | Calls allowed to wait for a slot when every slot is busy; more are refused at once. |
| `Database:Enabled` | bool | `true` | true \| false | Both | start | `false` turns the whole database guard off: calls go straight to the store and a start-up failure is not retried (the pre-resilience behaviour). |
| `Database:QueryTimeoutMs` | int | `5000` | a whole number, -2147483648 to 2147483647 | Both | start | Longest one guarded database call may take before it is cancelled and counted as a failure, in milliseconds; 0 disables the timeout. |
