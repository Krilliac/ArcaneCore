# Invariants, assertions and crash handling (lane `infra-invariants`)

What the daemons assume about their own state, how a broken assumption is reported, and what ends the process when
an exception escapes every handler. Code: `src/ArcaneCore.Kernel/Diagnostics` (`Invariant`, `CrashHandler`,
`CrashReport`), `src/ArcaneCore.Kernel/Configuration/DiagnosticsOptions.cs`; wired by one line in each `Program.cs`
(`builder.UseArcaneDiagnostics()` in the realm daemon, `builder.UseWorldDiagnostics()` in the world daemon, which adds
the tick context). Reference behaviour read, never copied: vmangos `MANGOS_ASSERT` (`src/shared/Errors.h`, aborts in
every build) and `Master.cpp`'s signal handlers, which only log and stop; ArcaneCore makes the choice configurable.

## Two strengths of invariant

| | `Invariant.Assert(condition, message)` | `Invariant.Check(condition, message)` |
|---|---|---|
| Compiled in | Debug builds of the **calling** assembly only (`[Conditional("DEBUG")]`: the call and its condition are removed from Release call sites) | every build |
| On failure | logged and counted, then `InvariantViolationException` (derives from `Exception` directly, so no protocol-error handler swallows it); the session's generic handler tears the connection down | logged (bounded per site) and counted; returns `false` so the caller runs its own fail-closed handling |
| `Diagnostics:OnInvariant=FailFast` | aborts instead of throwing | aborts instead of returning |
| Cost when the condition holds | nothing (not even the condition) in Release; one branch in Debug | one branch |

Messages are interpolated strings handled by `InvariantMessageHandler`: the compiler passes the condition into the handler,
so the message is formatted only when the check fails. `InvariantTests.PassingChecksAndAsserts_AllocateNothing` runs 100 000
passing checks of both kinds, with an interpolated message holding an object, and asserts a zero
`GC.GetAllocatedBytesForCurrentThread` delta and that the object's `ToString` was never called. The failure path allocates
(the message, a per-site counter) and takes a `ConcurrentDictionary` lookup; it is not a hot path by definition.

**Why the Debug assert throws instead of aborting.** `System.Diagnostics.Debug.Assert` ends the process on .NET Core. A thrown
exception reaches the test runner, names its call site (`InvariantViolationException.Member/File/Line`), and lets a
development server survive one bad session. Operators who want the mangos behaviour set `Diagnostics:OnInvariant=FailFast`.

**Counting and logging.** Every failure increments a process-wide counter and a per-call-site counter (member, file name, line).
The first `Diagnostics:InvariantLogLimit` failures of a site are logged at Error (category `ArcaneCore.Diagnostics.Invariant`;
standard error before the host exists); the rest are only counted. Every crash report lists the counters, most frequent first.
`Invariant.DebugBreak()` calls `Debugger.Break` only when a debugger is attached **and** `Diagnostics:BreakOnInvariant` is on;
it is called on every failure path and may be called directly.

## The `Diagnostics` section

Defaults keep the process behaving as the .NET runtime does without this section, except that every event is now reported
first. None of the keys is live (`.reload config` does not touch them). The world daemon's `check-config` validates the section
(`DiagnosticsConfigChecks`: unknown policy names, non-boolean flags, a negative limit are errors, exit 78); the realm daemon has
no `check-config`, so `UseArcaneDiagnostics` itself writes the binder's message to standard error and exits 78 before anything
binds or touches a database.

| Key | Default | Values | Fail-closed behaviour |
|---|---|---|---|
| `Diagnostics:OnInvariant` | `Continue` | `Continue`, `FailFast` | unknown name: exit 78 at start. `Continue`: the caller's own refusal runs (drop the packet, refuse the login, throw `SchemaMismatchException`). `FailFast`: the process aborts with the site and message |
| `Diagnostics:BreakOnInvariant` | `false` | `true`, `false` | acts only with a debugger attached, so an unattended process never waits on a prompt |
| `Diagnostics:InvariantLogLimit` | `10` | 0 or more | `0` logs nothing and counts everything; negative is a configuration error (exit 78); a hot-path check that fails per packet cannot flood the log |
| `Diagnostics:OnUnhandled` | `FailFast` | `FailFast`, `Exit` | the report is always written first (standard error synchronously, then the logger). `FailFast`: `Environment.FailFast`, the runtime's own behaviour: SIGABRT 134 on Linux, 0x80131623 on Windows, a dump when `DOTNET_DbgEnableMiniDump=1`. `Exit`: `Environment.Exit(70)` (`ExitCodes.UnhandledException`, sysexits EX_SOFTWARE), no dump, an exit code a supervisor can match |
| `Diagnostics:OnUnobservedTask` | `Log` | `Log`, `Exit`, `FailFast` | `Log` is the runtime's own behaviour (the exception is marked observed) made visible at Error level. `Exit`/`FailFast` as above. Default stays `Log` so a fire-and-forget task that faulted silently before this lane does not start killing production servers |
| `Diagnostics:FirstChanceExceptions` | `false` | `true`, `false` | honoured only by a Debug build of the Kernel (the hook is compiled out of Release, and a Release daemon logs once that the key is ignored). Logs every throw, caught or not, at Debug level; the hook is re-entrancy guarded |

Exit code **70** is new in `src/ArcaneCore.Kernel/Ops/ExitCodes.cs` (summary on the constant); the table in
`docs/reference/exit-codes.md` is generated from that class by the Docs tests
(`ARCANECORE_UPDATE_DOCS=1 dotnet test tests/ArcaneCore.World.Tests --filter Docs`), as is the `Diagnostics` section of
`docs/reference/configuration.md`. Supervisors may restart on 70 and on the abort status; they must still not restart on 78.

## The crash report

`CrashHandler` attaches `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` (and, Debug Kernel with the
option on, `AppDomain.FirstChanceException`) the moment `UseArcaneDiagnostics` runs, with a standard-error sink, so a crash while
the host is built or a schema is initialised is already reported; when the host starts, `DiagnosticsHostedService` swaps in the
logger sink (category `ArcaneCore.Diagnostics.Crash`, the exception attached for structured sinks) without a window in which no
handler is attached. Because the console logger writes from a background thread, a terminal report is also written to standard
error synchronously before the process ends. The hooks stay attached through shutdown.

`CrashReport.Render` (pure; `CrashReportTests` check its content) writes, each probe in its own guard so one failing probe never
hides the exception: the exception type, message, inner-exception count, invariant site, and `ToString()` (the managed stack of
every inner exception); process id and name, uptime, `ExitCodes.Current`, runtime, OS, architecture, Kernel build configuration,
GC mode, processor count (never the command line: it can carry `--Database:...ConnectionString=` with a password); the crashing
thread's id, name, pool membership, pool thread and pending-item counts; working set, peak working set, private bytes, GC heap and
committed bytes, collection counts, total allocated bytes, handle count; the invariant counters; then each daemon's
`ICrashContextProvider`. A provider that throws is noted in the report, not fatal.

**World context** (`WorldCrashContext`, `src/ArcaneCore.World/Ops/Diagnostics`): the tick number counted from the existing
`WorldRuntime.WorldTick` hook (ticks begun), the last tick's `diff`, the ticks the statistics ring completed
(`TickStats.TotalTicks`), uptime, the world clock, the online count, and whether the crashing thread is the world thread.
Everything it reads is a volatile field or lock-free, except the statistics snapshot, whose lock `TickStats` holds only to copy
its ring and never while calling out; nothing can wait on the world thread, so a wedged world thread still reports. The realm
daemon has no context provider.

## Every invariant, and why it holds

Each entry is a precondition read from the code it guards, with the handling when it fails. **A** = `Invariant.Assert`
(Debug), **C** = `Invariant.Check` (every build). Call sites and messages are in the source; the tests named run in Debug and
Release (CI builds Release, so `Assert` sites are exercised by the Debug run developers do and by `InvariantTests`, which proves
the split).

### Kernel

| Where | Kind | Invariant | Why it holds; what happens when it does not |
|---|---|---|---|
| `Ops/ExitCodes.Current` setter | C | `0 <= value <= 125` | only a shutdown request sets it, and `ServerLifecycleCommands` caps requests at `MaxRequested` (vmangos ServerCommands.cpp:424-430: 126-255 belong to shells). Counted; the value is still stored so the stop is never lost |
| `Net/ConnectionLimiter.Release` | C ×2 | `_total > 0`; the per-address entry exists | every release pairs with one admission (`Lease.Dispose` is `Interlocked`-guarded against a second call). A failure skips the decrement, so the cap cannot go negative and switch itself off |

### Cryptography (`src/ArcaneCore.Cryptography`)

| Where | Kind | Invariant | Why it holds; what happens when it does not |
|---|---|---|---|
| `Srp6Server` ctor | A | `0 <= B < N` | `B = (k·v + g^b) mod N` is a residue; the 32-byte wire field (`ToFixedLittleEndian`) would throw on anything wider |
| `Srp6Server.TryAcceptProof` entry | C | `SessionKey is null` (one proof per object) | the realm discards the server after one proof (vmangos AuthSocket.cpp:555 `STATUS_INVALID` on entry). A second call is refused, so a kept object cannot re-judge a replayed A/M1. `Srp6InvariantTests` |
| `Srp6Server.TryAcceptProof` after the interleave | C | `K.Length == 40` | K is two interleaved SHA-1 digests; the header cipher and `account.sessionkey` take exactly that. Refused (the logon fails) |
| `Srp6Server.TryAcceptProof` | A | `M1.Length == 20` | M1 is one SHA-1 digest |
| `Srp6Math.Interleave` | A | `0 <= S`, fits 32 bytes | the client's SHA1Interleave reads a 32-byte buffer; the gtker KAT rows use 32-byte values that are not residues of N, so the width (not N) is the precondition |
| `Srp6Math.Interleave` | A | an even number of bytes is split | an odd count of leading zeros is rounded up before the split |
| `WowSrp6.GenerateSalt` | A | `Srp6Validation.IsUsableSalt(salt)` | a salt this method makes must pass the test the logon applies to stored salts, or every auto-created account is refused at its first login |

### Protocol (`src/ArcaneCore.Protocol`)

| Where | Kind | Invariant | Why it holds; what happens when it does not |
|---|---|---|---|
| `WorldHeaderCrypt.Initialize` | C | not already initialised | keyed once per connection after CMSG_AUTH_SESSION (vmangos `WorldSocket::HandleAuthSession` calls `m_Crypt.Init` once); a second call resets both rolling states and desynchronises the peer. Counted; the key is applied (the test clients re-key deliberately in no test) |
| `WorldHeaderCrypt.Initialize` | C | `key.Length == 40` | the cipher is keyed with K; another length means the wrong field was read. Counted; the cipher still runs (the algorithm, like vmangos `AuthCrypt::Init`, is defined for any length) |
| `WorldHeaderCrypt.Encrypt/DecryptHeader` | A | span is 4 or 6 bytes | the rolling state advances one byte per header byte on both peers; any other length desynchronises every later header |
| `PacketWriter.WritePackedGuid` | A | bytes after the mask = popcount(mask) | `PacketReader.ReadPackedGuid` (vmangos `readPackGUID`) consumes one byte per set bit |
| `PacketWriter.WriteCString` | A | encoded bytes = counted bytes | the terminator goes right after the text; a disagreement leaves stale buffer bytes inside the string |
| `PacketWriter.EnsureCapacity` | A | the buffer now fits `_length + additional` | postcondition of the resize every writer relies on |

### Data (`src/ArcaneCore.Data/Schema`)

| Where | Kind | Invariant | Why it holds; what happens when it does not |
|---|---|---|---|
| `SchemaBootstrapper.TryReadVersionAsync` | C | the version row is `>= 0` | versions are 0 (create in progress) or a released version from 1; a negative row is a damaged table. Fails closed: `SchemaMismatchException`, nothing written. `SchemaVersionInvariantTests` (SQLite) |
| `SchemaBootstrapper.RunAsync` | A ×2 | after create-or-adopt the version is 1 or current; after a resumed create it is current | `CreateOrAdoptAsync` returns 1 (pre-M5 adoption) or `CreateFreshAsync`'s result; `CreateFreshAsync` writes `CurrentVersion` last |
| `SchemaBootstrapper.WriteVersionAsync` | C | the row moves up (or rewrites the 0 marker) | each step writes `version + 1`, a create writes 0 then current. A downgrade would make a newer database look older and let a later start "upgrade" over tables it does not know. Fails closed: `SchemaMismatchException` |
| `SchemaLock.CompleteAsync` | C | the lock is held | `AcquireAsync` returns a held lock or throws. Completing an unheld lock means the bootstrap ran unserialised; fails closed with `InvalidOperationException` |
| `SchemaLock.BeginSqliteWriteAsync` / `PollAsync` | A | not yet held | a lock is acquired once |
| `DataModules.Compose` | A | the last step's version is `steps.Count + 1` | the loop proved contiguity from 2; the bootstrapper's upgrade loop relies on each step being one above its predecessor |

The write queues (`CharacterSaveQueue`, the social write queue) live in `src/ArcaneCore.World/Persistence` and
`src/ArcaneCore.Game`, which this lane may not edit; their state invariants are deferred (see Limits).

### Realm (`src/ArcaneCore.Realm/Net/LogonSession.cs`)

| Where | Kind | Invariant | Why it holds; what happens when it does not |
|---|---|---|---|
| `HandleChallengeAsync` after the body read | A | `body.Length >= 31` and `31 > 29` | the size window 31..47 (vmangos AuthSocket.cpp:248-262) is what puts offsets 17..20 (locale) and 29 (name length) in range |
| `HandleProofAsync` after `ResetChallengeState` | A | no SRP state, not authenticated, no username | a proof consumes the challenge before it is judged (vmangos `STATUS_INVALID` on entry) |
| `HandleProofAsync` before persisting | C ×2 | `K.Length == 40`; an auto-create candidate has its verifier | what is written to `account.sessionkey` must be the key the world daemon keys its cipher with; `CreateAsync` dereferences the verifier. Refused with `FAIL_NOACCESS`, nothing stored |
| `HandleRealmListAsync` | A | authenticated implies `_srp is null` | the proof handler clears the SRP state before setting `_authenticated`; a session holding both could answer a second proof against a stale challenge. (Note: `_username` is **not** kept after the proof, which is why the invariant is about `_srp`, not the name) |

### World (`src/ArcaneCore.World/Net`)

| Where | Kind | Invariant | Why it holds; what happens when it does not |
|---|---|---|---|
| `WorldSession.ProcessWorldPackets`, `OnLoggedOut`, `TryEnterWorld`, `AbortLogin` | A | `World.IsWorldThread` | player and map state are owned by the world thread; `CharacterHandlers.EnterWorld` runs there (posted), `WorldRuntime` calls `OnLoggedOut` there. `TryBeginLogin` is deliberately **not** asserted: it runs on the session task |
| `WorldSession.TryEnterWorld` | A | `Player is null` | `LoggingIn` is reached from `CharacterSelect` only; `OnLoggedOut` and `Close` clear `Player` before the session can get back there. Two players on one session would confuse the online registry and the save path |
| `WorldSession.Release` | C | queued packet and byte counters stay `>= 0` | every dequeue releases exactly what its enqueue added; a negative counter would silently disable the inbound queue bound (`World:MaxQueuedWorldPackets/Bytes`). Counted (hot path: one branch, no allocation) |
| `WorldSession.HandleAuthSessionAsync` | C | the stored `SessionKey.Length == 40` | the realm wrote the 40-byte interleave; another length is a damaged or foreign row. Refused with `AUTH_FAILED` before the cipher is keyed. `WorldAuthSessionKeyShapeTests` |
| `SessionRegistry.Register` | A | `AccountId != 0` | the registry is keyed by account; `Register` runs after `AccountId` was taken from the authenticated row |

## Thread affinity, ownership and allocation

- `Invariant`: static, any thread. Pass path: a branch, no allocation (proven). Failure path: `Interlocked` counters, a
  `ConcurrentDictionary` keyed by (member, file, line), one formatted string, one log call; the per-site counter stops logging at
  the limit. `Invariant.Capture()` opens a flow-scoped (`AsyncLocal`) recorder of the failures raised on the calling thread and
  in the work it awaits; it is read on the failure path only. Tests use it for exact counts, because `FailureCount` is
  process-wide and xunit runs the other test classes of an assembly in parallel (`Srp6InvariantTests`,
  `SchemaVersionInvariantTests`; `InvariantTests.Capture_*` prove the scoping against a failure on an unflowed thread).
- `CrashHandler`: one per process (`Install` is idempotent, `Reconfigure` swaps sink/options/providers under a `volatile` read).
  Hooks run on the crashing thread; a thread-static guard makes a nested report (a sink or provider that throws, or a first-chance
  event raised by the report itself) a no-op. Nothing allocates until a hook fires.
- `WorldCrashContext`: the counter is written on the world thread only (`Interlocked.Increment` from the `WorldTick` hook) and
  read with `Volatile.Read` from the crashing thread; `Describe` allocates only the text it appends.

## Tests

- `tests/ArcaneCore.Kernel.Tests` (new project, in the solution): `InvariantTests` (zero allocation, message formatted once,
  per-site counters and log limit, `[Conditional("DEBUG")]` by reflection, the same `Assert` call site evaluating and throwing in
  Debug and not being evaluated in Release via `#if DEBUG`, `Check` in both, `DebugBreak` a no-op without a debugger, the
  `ExitCodes.Current` range); `CrashReportTests` (content, providers, aggregate and invariant exceptions, install/reconfigure/
  dispose, the logger sink, the first-chance hook following the Kernel build); `DiagnosticsHostingTests` (the one-line wiring:
  binding, bootstrap hooks before `Build`, the swap to the host logger at start, defaults without a section);
  `CrashProcessTests`, which start this assembly as a child process (`Program.Main`, `--diagnostics-probe`) and watch: invariant
  under `FailFast` aborts after logging the site; unhandled exception aborts (default) or exits 70; unobserved task is logged and
  survived (default), exits 70, or aborts; an unknown policy name exits 78 before the host is built. The suite is run in both
  `-c Debug` and `-c Release`.
- `tests/ArcaneCore.Cryptography.Tests/Security/Srp6InvariantTests.cs`, `tests/ArcaneCore.Data.Tests/SchemaVersionInvariantTests.cs`,
  `tests/ArcaneCore.World.Tests/Ops/DiagnosticsWiringTests.cs` (config check, world context),
  `tests/ArcaneCore.World.Tests/Security/WorldAuthSessionKeyShapeTests.cs`. The existing Realm, Cryptography (8019 KATs) and
  world handshake suites run with the Debug asserts live; one draft precondition (`S < N` in the interleave) was wrong and the
  KAT rows caught it, which is the point of asserting real preconditions only.

## Dependencies and tradeoffs

- No NuGet package was added to the repository's graph. `ArcaneCore.Kernel` now references
  `Microsoft.Extensions.Options.ConfigurationExtensions` 9.0.0 (already pulled in by `Microsoft.Extensions.Hosting` in both
  daemons) to bind the section. `ArcaneCore.Cryptography` and `ArcaneCore.Protocol` gained a project reference to the Kernel (which
  references no project) for `Invariant`; their csproj comments say so.
- Pure C#: `Debugger.Break`, `Environment.FailFast`, `GC`, `Process` and `RuntimeInformation` are cross-platform; nothing
  P/Invokes.

## Limits

- Write-queue state invariants (`CharacterSaveQueue`, `SocialWriteQueueOptions` consumers) are deferred: those files belong to
  lanes this one may not edit.
- `Invariant.Check` under `Continue` cannot force a caller to refuse; each site above states its own refusal. New sites must do the
  same (return false, throw, drop) or they are decoration.
- The first-chance hook exists only in a Debug Kernel build; CI builds Release, so it is covered by the Debug run of
  `CrashReportTests.FirstChanceHook_FollowsTheKernelBuild` only.
- `Environment.FailFast` exit statuses are the runtime's; the process tests assert "not 0 and not 70" rather than 134, because
  Windows reports a different code.
- A dump on abort needs `DOTNET_DbgEnableMiniDump=1` (and `DOTNET_DbgMiniDumpName`) in the daemon's environment; this lane does
  not set it.
