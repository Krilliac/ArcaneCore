# Resilience: circuit breakers, retries, bulkheads and timeouts

Lane `ccr-build/infra-resilience`. How the daemons behave when a dependency (today: the three databases) is slow,
refuses connections or disappears, and the primitives other lanes use to get the same behaviour. No third-party
package is used: the repository has no resilience or logging dependency and this lane keeps it that way. Polly would
have given the same four primitives for free, with a larger API surface, allocation per call on its older versions
and one more package to audit for a server that handles credentials; the primitives here are ~600 lines, allocation
free on the hot path, and shaped exactly for the two call sites that need them. The trade-off is that nothing beyond
those four primitives (hedging, rate limiting, result-based retries with policies per result) exists until someone writes it.

Retail reference: vmangos has no equivalent. A dead database there makes every query fail and log until the server is
restarted (`realmd` keeps answering `WOW_FAIL_UNKNOWN_ACCOUNT`-shaped errors from empty results; `mangosd` eventually
asserts). ArcaneCore fails closed and says why, once.

## What is delivered

### Primitives (`src/ArcaneCore.Kernel/Resilience`)

| Type | What it does | Hot-path cost |
|---|---|---|
| `CircuitBreaker` | Closed → Open → HalfOpen → Closed. Trips on a run of consecutive failures (`FailureThreshold`) **or** a failure rate over a sliding window of fixed buckets (`FailureRateThreshold` of the calls in `SamplingWindow`, judged once `MinimumThroughput` calls are in it). Open for `OpenDuration`, then lets `HalfOpenMaxProbes` through; one probe success closes, one probe failure re-opens for a full period. `Isolate()`/`Reset()` are manual overrides. State changes go to a callback outside the lock. | One uncontended `Lock` per call, no I/O, no allocation when closed and the call succeeds (`Execute` sync, `ExecuteAsync` with a synchronously completing `ValueTask`). A refusal allocates its `CircuitOpenException`; `TryEnter`/`OnSuccess`/`OnFailure`/`OnNeutral` are the exception-free protocol. |
| `RetryPolicy` | Bounded attempts, exponential backoff `min(MaxDelay, BaseDelay·2^(n-1))`, full jitter (`[0, delay]`, uniform) or none, optional total budget, a transient-exception classifier (`TransientFailure.IsTransient` by default) and a per-retry callback. `ExecuteUntilAsync` is the poll shape (retry on a `false` result, last poll at the deadline). | A first attempt that completes synchronously is returned as is: no state machine. Delays use `Task.Delay(…, TimeProvider, …)`, so tests drive them with a fake clock. |
| `Bulkhead` | `MaxConcurrency` slots + `MaxQueue` waiters; anything beyond is refused at once with `BulkheadRejectedException`. | `SemaphoreSlim.Wait(0)` fast path, interlocked queue counter; nothing allocated when a slot is free. |
| `TimeoutPolicy` | Per-call deadline. `Cooperative` cancels the operation's token; `Pessimistic` additionally stops waiting (for APIs that block or ignore their token, e.g. Microsoft.Data.Sqlite's async methods). The deadline is a `CancellationTokenSource(timeout, timeProvider)`, never a separate timer calling `Cancel()`: a timer's `Dispose()` does not wait for a callback already dequeued, and a call completing at the deadline would let that callback throw `ObjectDisposedException` on a timer thread and abort the process (`TimeoutPolicyDeadlineRaceTests` pins the race, by hand and under a 64-worker stress with first-chance and unobserved-exception hooks). | One `CancellationTokenSource` carrying the deadline timer per call, plus a linked one when the caller's token can be cancelled, by nature. Keep it on I/O, never on per-packet in-memory work. |
| `Policy.Builder(name)…Build()` → `ResiliencePipeline` | Composes the four in the fixed order bulkhead → retry → breaker → timeout (each optional). | A struct frame threaded through cached `static` lambdas: no allocation beyond the layers' own. |
| `TransientFailure` | The default classifier: `TimeoutException`, `SocketException`, `IOException`, `DbException.IsTransient`, `TimeoutRejectedException`, `DependencyUnavailableException` (walking inner exceptions, depth 8). Never: caller cancellation, `CircuitOpenException`, `BulkheadRejectedException`, business exceptions. | Pure. |
| `ResilienceException` family | `CircuitOpenException`, `BulkheadRejectedException`, `TimeoutRejectedException`, `DependencyUnavailableException`: one base type to catch for "the dependency is the problem". | – |

Ownership and threads: every primitive is one shared instance per dependency, thread-safe, immutable after
construction; the breaker's callback runs on the thread that caused the transition, after the lock is released. The
clock is a `TimeProvider` everywhere (fake in tests, `TimeProvider.System` in production).

Proof of the allocation claims: `CircuitBreakerTests.ClosedFastPath_AllocatesNothing`,
`RetryPolicyTests.FirstAttemptSuccess_CompletesSynchronously_WithoutAllocation`,
`BulkheadTests.FreeSlotFastPath_AllocatesNothing`, `PolicyPipelineTests.FullPipelineWithoutTimeout_FastPathAllocatesNothing`
(all assert a zero `GC.GetAllocatedBytesForCurrentThread` delta over 10 000 calls, in Debug too: the fast paths
contain no `async` state machine).

### Applied to the database layer (`src/ArcaneCore.Data/Resilience`)

- `DatabaseTransience`: the classifier for the three providers. SQLite codes 5 BUSY, 6 LOCKED, 10 IOERR, 14 CANTOPEN,
  26 NOTADB; `DbUpdateException` wrapping a transient provider error; MySqlConnector's and Npgsql's `IsTransient`
  (connection refused, server gone away, deadlock, lock wait). A constraint violation, "no such table" or a domain
  exception is **not** transient: it surfaces unchanged and never opens a circuit.
- `DatabaseCircuits`: one breaker per logical database (Auth, Characters, World), all from `Resilience:Database:Breaker`,
  with the operator lines:
  `Auth database circuit OPENED (MySqlException: Unable to connect …); every call is refused for the next 10000 ms (fail closed), then one probe is let through` (ERROR),
  `… circuit half-open: probing the database with one call` (WARNING), `… circuit closed: database access restored` (INFORMATION).
- `DatabaseGuard`: the adapter. `ExecuteAsync(component, static (state, ct) => …, state, ct)` runs one store call
  through bulkhead → breaker → timeout; a transient failure counts and is rethrown as `DependencyUnavailableException`;
  anything else passes through. `BootstrapAsync` wraps the start-up schema initialisation in the
  `Resilience:Database:Bootstrap` retry. `PipelineFor(component)` exposes the composed pipeline for callers adding their own layer.
  The timeout strategy follows each component's `Database:<Component>:Provider` (`DatabaseGuard.TimeoutStrategyFor`):
  `Cooperative` for MariaDB/MySQL (MySqlConnector) and PostgreSQL (Npgsql), which honour the cancellation token;
  `Pessimistic` for `Sqlite`, because Microsoft.Data.Sqlite's async methods run synchronously and ignore the token.
  Measured on Linux: a guarded EF read against a file another connection holds (`PRAGMA locking_mode=EXCLUSIVE` +
  `BEGIN EXCLUSIVE`) blocked 30 082 ms under the cooperative strategy, SQLite's busy timeout, whatever `QueryTimeoutMs`
  said; with the pessimistic strategy the caller gets `TimeoutRejectedException` at the deadline. **Cost of the
  pessimistic strategy:** the abandoned call keeps its thread-pool thread until SQLite gives up (the connection's
  `Default Timeout`, 30 s) or the lock is released, its result is discarded, and disposing the scope whose connection it
  still uses may block until that statement returns (SQLite serialises the calls on one connection; measured only after
  the lock release, where it took under 5 s, UNVERIFIED while the lock is still held). One parked thread per refused
  call is the price of a bounded answer on the zero-setup engine; the network providers pay nothing extra.
- `DatabaseStartup.InitializeAsync` (both daemons' start-up path) runs the initializers through `BootstrapAsync` when
  the guard is registered and turns a final transient failure into one scrubbed line,
  `database unreachable: MySqlException: Unable to connect to any of the specified MySQL hosts.`, and exit code **6**
  (`DbUpgradeExitCodes.Unreachable`, the same code `arcane-db` uses). A schema refusal (`SchemaMismatchException`) is not
  transient and is never retried. Before this lane the same failure was an unhandled-exception crash with a stack trace.
- `SchemaLock.PollAsync` (the MariaDB `GET_LOCK` / PostgreSQL `pg_try_advisory_lock` wait) is now
  `RetryPolicy.ExecuteUntilAsync` with a fixed 250 ms interval, no jitter and the lock timeout as the budget. Same
  behaviour as the hand-rolled loop, one implementation.

### Applied to the logon daemon (`src/ArcaneCore.Realm`)

- `AddRealmResilience` (`Net/RealmResilienceServiceCollectionExtensions.cs`) registers the guard and decorates
  `IAccountStore`, `IRealmStore` and `IBanStore` with `GuardedAccountStore` / `GuardedRealmStore` / `GuardedBanStore`
  (`Resilience/GuardedAuthStores.cs`). The EF store is resolved *inside* the guarded call, because with MariaDB the
  `DbContext` constructor already connects (`ServerVersion.AutoDetect`) and that failure has to count too. The call
  must follow `AddAuthDatabase`; it throws a clear `InvalidOperationException` otherwise rather than guarding nothing.
- `LogonSession` catches `ResilienceException` around each command: the SRP state is dropped, the client gets
  `WOW_FAIL_DB_BUSY` (`AuthResult.FailDbBusy` = 0x08, vmangos `AuthCodes.h`; the client shows a "try again later"
  style message instead of a silent disconnect) on a challenge or proof, the connection closes, and one WARNING line
  without a stack is written: `[1.2.3.4:5000] auth database unavailable (circuit 'Auth database' is open; retry after 7312 ms); refusing logon (fail closed)`.
  The breaker logged the cause once when it opened; a thousand refused clients do not produce a thousand stack traces.
  Nothing is ever authenticated against a database that did not answer.

### Wiring (one line per daemon)

- `src/ArcaneCore.Realm/Program.cs`: `builder.Services.AddRealmResilience(builder.Configuration);` after `AddAuthDatabase`.
- `src/ArcaneCore.World/Program.cs`: `builder.Services.AddDatabaseResilience(builder.Configuration);` after the three `Add…Database` calls.
- `src/ArcaneCore.World/Ops/Cli/OpsCli.cs`: `ResilienceConfigChecks` joins `check-config` and the start-up validation.

## Options (`Resilience` section)

All keys are **restart-only**: the breakers and pipelines are built once at start, `.reload config` does not touch the
section and it is not in `WorldConfigKeys` (it is not a `World` key). The reference table is generated into
`docs/reference/configuration.md`. Validation: `check-config` and every world start report each bad key with its fix
(`ResilienceConfigChecks`); both daemons also validate the bound object at host start (`IValidateOptions`), so an invalid
section stops the process before a port is bound.

| Key | Default | Meaning | Fail-closed behaviour |
|---|---|---|---|
| `Resilience:Database:Enabled` | `true` | `false` turns the guard off: calls go straight to the store, start-up is not retried (pre-lane behaviour). | With the guard off a dead database is a per-call exception again (`session error` with a stack per client). |
| `Resilience:Database:QueryTimeoutMs` | `5000` | Longest one guarded call may take; 0 disables. 1..600000. | A call past the deadline is cancelled, counted as a failure, and answered with the refusal. With the `Sqlite` provider the call is abandoned instead of cancelled (the driver ignores the token); the abandoned call parks a thread-pool thread until SQLite's own 30 s busy timeout or the lock release. |
| `Resilience:Database:Breaker:FailureThreshold` | `5` | Consecutive failures that open the circuit; 0 disables this trip. | Open: every call refused for `OpenDurationMs`. |
| `Resilience:Database:Breaker:FailureRateThreshold` | `0.5` | Failed share (0..1) of the calls in the window that opens the circuit; 0 disables this trip. Both trips cannot be 0. | Same. |
| `Resilience:Database:Breaker:MinimumThroughput` | `10` | Calls that must be in the window before the rate is judged. ≥ 1. | Protects against one early failure tripping a quiet server. |
| `Resilience:Database:Breaker:SamplingWindowMs` | `10000` | Length of the sliding window (10 buckets). 1..86400000. | – |
| `Resilience:Database:Breaker:OpenDurationMs` | `10000` | How long the circuit refuses before probing. 1..86400000. | Refusals are instant (no connection attempt) for this long. |
| `Resilience:Database:Breaker:HalfOpenMaxProbes` | `1` | Probes let through at once while half-open. ≥ 1. | One success closes; one failure re-opens for a full period. |
| `Resilience:Database:Bulkhead:MaxConcurrency` | `0` | Guarded calls allowed to run at once per database; 0 disables the bulkhead. | Beyond slots + queue a call is refused at once (`BulkheadRejectedException` → `WOW_FAIL_DB_BUSY`). |
| `Resilience:Database:Bulkhead:MaxQueue` | `64` | Waiters allowed when every slot is busy. ≥ 0. | – |
| `Resilience:Database:Bootstrap:MaxAttempts` | `5` | Start-up attempts to reach the database, including the first. ≥ 1. | The last failure is one line and exit code 6; supervisors may restart on 6 (the server may simply not be up yet), never on 78. |
| `Resilience:Database:Bootstrap:BaseDelayMs` | `500` | Wait after the first failed attempt; doubles each time, full jitter. 0..3600000. | – |
| `Resilience:Database:Bootstrap:MaxDelayMs` | `5000` | Cap on the wait. ≥ `BaseDelayMs`. | – |
| `Resilience:Database:Bootstrap:MaxTotalDurationMs` | `0` | Overall budget for the attempts; 0 = attempts alone bound it. 0..86400000. | A retry whose delay would cross the budget is not made. |

Why these defaults: five consecutive failures or half of ten calls in ten seconds is a real outage, not a blip; ten
seconds open is long enough to stop a connection storm against a restarting MariaDB and short enough that players see
a working login within one retry of the client's own "try again"; the bulkhead stays off because the logon daemon's
connection caps (`Auth:MaxConnections`) already bound concurrency and a second cap needs a measured pool size; five
start-up attempts with 0.5 s → 5 s backoff cover a database that comes up a few seconds after the daemon under one
supervisor, without hiding a genuinely wrong connection string for more than ~15 s.

## Exit codes

| Code | Where | Meaning |
|---|---|---|
| 6 | realm and world start | the database server could not be reached after `Resilience:Database:Bootstrap:MaxAttempts` attempts (message scrubbed of connection-string secrets) |
| 4 / 7 | unchanged | schema refused / lock timeout (`docs/ops/database-upgrade.md`) |

## Migrating the World write queues (for the lanes that own them)

`HonorWriteQueue`, `ReputationWriteQueue`, `SocialFeature`'s write queue and the autosave path keep their own ordering
and retention guarantees and are not touched by this lane. Each has the same hand-rolled loop:

```csharp
for (int attempt = 1; ; attempt++)
{
    try { … store calls … return; }
    catch (Exception ex) when (attempt < MaxAttempts)
    {
        logger.LogWarning(ex, "… failed (attempt {Attempt}); retrying", attempt);
        await Task.Delay(200 * attempt).ConfigureAwait(false);
    }
}
```

The replacement, keeping the retention semantics (a write that fails every attempt is still retained by the queue):

```csharp
// once, in the constructor (inject DatabaseGuard):
_retry = new RetryPolicy(
    new RetryOptions { MaxAttempts = 3, BaseDelay = TimeSpan.FromMilliseconds(200), MaxDelay = TimeSpan.FromSeconds(2) },
    isTransient: DatabaseTransience.Classifier,
    onRetry: a => logger.LogWarning(a.Exception, "honor write failed (attempt {Attempt}); retrying in {Delay}", a.Attempt, a.Delay));

// per write:
await _retry.ExecuteAsync(
    static async (s, ct) =>
    {
        await s.Guard.ExecuteAsync(DatabaseComponent.Characters, static (w, c) => new ValueTask(w.Persist(c)), s, ct).ConfigureAwait(false);
    },
    (Guard: guard, Persist: snapshot.PersistAsync),
    cancellationToken).ConfigureAwait(false);
```

What changes for the queue: a domain error (name taken, constraint) is no longer retried three times (the classifier
stops it at once, which is what those loops wanted); a transient error is retried with jitter instead of a fixed
200·n ms; when the Characters circuit is open the write fails immediately with `CircuitOpenException` instead of
waiting through three connection timeouts, so the queue retains it and the next trigger (login barrier, logout, stop)
retries it after the circuit has probed. The `FlushCharacterAsync` and `StopAsync` contracts are unchanged: they still
fault while a character's write is not durable. Do not put a `TimeoutPolicy` around a multi-statement transaction with
`Pessimistic`: the abandoned transaction would hold its locks until the provider gives up. The guard's own timeout is
pessimistic for the `Sqlite` provider (see above), so a queue that runs a multi-statement transaction through
`DatabaseGuard.ExecuteAsync` against SQLite must keep the transaction inside one store call, as the realm stores do.

## Tests

- `tests/ArcaneCore.Realm.Tests/Resilience`: `CircuitBreakerTests` (every transition under a fake clock, half-open
  probe exhaustion and neutral release, rate window with expiry, neutral cancellation, faulted-ValueTask refusal,
  isolate/reset, 40 000 concurrent calls, 16 threads racing the half-open probe slot, zero-allocation fast path),
  `RetryPolicyTests` (delay table, 6 000 jitter samples within bounds, attempts, non-transient and open-circuit stop,
  cancellation mid-wait, total budget, `ExecuteUntil` poll times `0, 250, 500, 600`), `BulkheadTests` (reject fast,
  queue, cancel while queued, slot release on failure, 200 concurrent callers never exceed the cap), `TimeoutPolicyTests`
  (cooperative deadline, caller cancellation is not a timeout, pessimistic abandonment of a blocking call),
  `TimeoutPolicyDeadlineRaceTests` (a hand-fired clock runs the deadline callback after the call has disposed its
  source, cooperative and pessimistic, 250 times without an exception; 64 workers × 300 calls completing on either side
  of a 2 ms deadline under the system clock, with `AppDomain.FirstChanceException` and
  `TaskScheduler.UnobservedTaskException` hooked, raise nothing from `TimeoutPolicy`; before the fix the hand-fired test
  returned the `ObjectDisposedException` and the stress aborted the test host),
  `PolicyPipelineTests` (retry stops at an open circuit, timeout counts as a failure, bulkhead outermost, classifier
  table), `ResilienceOptionsTests` (defaults, every bad key named, `ResilienceConfigChecks`),
  `AuthDatabaseOutageTests`: the daemon composed as `Program.cs` composes it, against an SQLite file in a missing
  directory, against a bootstrapped SQLite file another connection holds in exclusive locking mode, and against a
  MariaDB connection string to a closed loopback port; two real challenge packets are answered `WOW_FAIL_DB_BUSY` each
  within the 2 s query timeout (the locked file would otherwise take 30 s each), the Auth circuit is open afterwards,
  the third client is refused in under 2 s with `Rejected` incremented and no database attempt, the three log lines are
  present and no `session error` or password appears in the log.
- `tests/ArcaneCore.Data.Tests/Resilience`: `DatabaseTransienceTests` (SQLite codes, Npgsql `IsTransient`, EF wrapping),
  `DatabaseStartupResilienceTests` (closed port: three attempts logged, exit 6, password scrubbed; unopenable SQLite
  file: exit 6; schema refusal not retried; a reachable SQLite bootstraps and serves guarded calls; the guard translates
  transient errors, passes domain errors, trips per component and honours `Enabled=false`),
  `DatabaseGuardSqliteLockTests` (the strategy follows each component's provider: MariaDB and PostgreSQL cooperative,
  Sqlite pessimistic; a guarded read against a locked, bootstrapped SQLite file is refused with
  `TimeoutRejectedException` within the 500 ms `QueryTimeoutMs` and counted as one failure, the next call after the
  lock is released succeeds and clears the count, and disposing the abandoned call's scope then takes under 5 s; before
  the fix the same call took 31 s and surfaced as `DependencyUnavailableException`).
- Existing suites still pass: `LogonHandshakeTests`, the Realm `Security` tests, `SchemaLock*`.

## Limits and not done

- The World write queues and the autosave path are not migrated (other lanes own them); the adapter and the recipe
  above are what they use.
- `check-config` still does not probe database reachability (unchanged from the ops lane).
- The breaker has no operator command (`.server circuits` / reset) yet; `DatabaseCircuits.All` and
  `CircuitBreaker.Isolate()/Reset()` are the hooks for the GM-commands lane.
- `WOW_FAIL_DB_BUSY` is the value from vmangos `AuthCodes.h` (0x08, consistent with the enum's 0x09 `VersionInvalid`,
  0x0C `Suspended`, 0x0D `FailNoAccess`); the exact text the 1.12.1 client shows for it was not checked on a real client in this lane.
- MySqlConnector's `MySqlException.IsTransient` is exercised through the real connector in the closed-port tests, not
  by a constructed exception (the type has no public constructor).
