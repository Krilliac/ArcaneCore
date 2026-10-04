# Logging (infra lane `logging`)

Branch `ccr-build/infra-logging`. The structured logging subsystem in `src/ArcaneCore.Kernel/Logging`: one
`ILoggerProvider` (`ArcaneLoggerProvider`, alias `ArcaneCore`) that replaces the .NET host's default console provider in
both daemons, selectable from configuration, with a colour console, a plain console, a rolling text file and a JSON-lines
file. Per-category minimum levels stay where they were (`Logging:LogLevel`). `LogSafe` (CWE-117 escaping of
client-controlled text) is unchanged and still the caller's job; the sinks append the message as given.

No third-party package is used. Serilog or NLog would have given the file and JSON sinks for free, but the repo has no
third-party logging or resilience dependency and the hot-path requirement (no per-event allocation on the world thread)
is easier to prove on ~1200 lines of owned code than on a framework whose pipeline boxes the event. The two package
references added to `ArcaneCore.Kernel` (`Microsoft.Extensions.Logging`, `Microsoft.Extensions.Options.ConfigurationExtensions`)
were already in both daemons' dependency graph through `Microsoft.Extensions.Hosting`; nothing new is downloaded.

## Wiring

One line in each `Program.cs`, right after the host builder is created (realm) or after `HostOptions` is bound (world):

```csharp
builder.Services.AddArcaneCoreLogging(builder.Configuration);
```

`AddArcaneCoreLogging` (namespace `ArcaneCore.Kernel.Configuration`, so the line needs no new `using`) validates the
`Logging:ArcaneCore` section, binds `ArcaneLoggingOptions`, calls `ClearProviders()` (console, debug, EventSource and,
on Windows, EventLog are gone) and registers `ArcaneLoggerProvider`. The world daemon's `check-config` verb and its
startup validation run the same `LoggingConfigChecks` (added to `OpsCli.Validate`), so a bad logging key is listed with
the other problems and exits 78.

## Configuration

Section `Logging:ArcaneCore` (every key also in `docs/reference/configuration.md`, generated from the options class).

| Key | Default | Reload | Meaning |
|---|---|---|---|
| `Logging:ArcaneCore:Console:Mode` | `Color` | live | `Color`: ANSI colour per level, timestamp/category/scopes dimmed. `Plain`: no escape codes, fixed four-character level column (journald, CI, redirected stdout). `Off`: nothing on stdout. |
| `Logging:ArcaneCore:Console:QueueCapacity` | `4096` | restart | Lines the console writer thread may hold before new lines are dropped. |
| `Logging:ArcaneCore:File:Enabled` | `false` | restart | Plain text file sink on or off. |
| `Logging:ArcaneCore:File:Path` | `logs/arcanecore.log` (shipped files: `logs/realm.log`, `logs/world.log`) | restart | Active file; relative to the working directory. Directory created on first write. |
| `Logging:ArcaneCore:File:RollSizeMb` | `64` | restart | Roll before a write would take the file past this size; `0` never rolls by size. |
| `Logging:ArcaneCore:File:RollDaily` | `true` | restart | Roll at the first line of a new day (in the `Timestamps` clock). |
| `Logging:ArcaneCore:File:Retain` | `14` | restart | Rolled segments kept; oldest beyond this deleted after each roll. `0` keeps all. |
| `Logging:ArcaneCore:File:QueueCapacity` | `8192` | restart | Lines the file writer thread may hold before dropping. |
| `Logging:ArcaneCore:Json:Enabled` | `false` | restart | JSON-lines sink on or off. |
| `Logging:ArcaneCore:Json:Path` | `logs/arcanecore.jsonl` (shipped: `logs/realm.jsonl`, `logs/world.jsonl`) | restart | Active JSON file. Must differ from the text file path when both are on. |
| `Logging:ArcaneCore:Json:RollSizeMb` / `RollDaily` / `Retain` / `QueueCapacity` | `64` / `true` / `14` / `8192` | restart | As for the text file. |
| `Logging:ArcaneCore:Timestamps` | `Utc` | live | `Utc` or `Local`. Text: `yyyy-MM-dd HH:mm:ss.fff`. JSON: ISO 8601 with `Z` or the local offset. |
| `Logging:ArcaneCore:IncludeScopes` | `true` | live | Append active logger scopes (` => scope`) to text lines, `"scopes":[...]` to JSON. |
| `Logging:LogLevel:*` | host default | live | Unchanged: category minimum levels, applied by the logger factory before the provider sees an event. `Logging:ArcaneCore:LogLevel:*` overrides them for this provider alone (standard provider-alias rule). |

**Fail closed.** Every value is checked at start (`LoggingConfigChecks`): unknown enum text, non-numeric or out-of-range
numbers (`QueueCapacity` 1..1000000, `RollSizeMb`/`Retain` ≥ 0), an empty or directory-shaped `Path` on an enabled sink,
the text and JSON paths equal with both sinks enabled. The world lists them with its other config problems and exits 78;
the realm throws from `AddArcaneCoreLogging` before the host is built (one exception with every problem listed). The
same rules run on every reload; an invalid reload is logged and rejected as a whole, and the running settings stay.

**Hot reload.** The host re-reads `appsettings.json` on change (the generic host's `reloadOnChange`), and
`ArcaneLoggerProvider` follows `IOptionsMonitor<ArcaneLoggingOptions>`: the console mode, `Timestamps` and
`IncludeScopes` are applied at once and logged (`Logging:ArcaneCore:Console:Mode changed to Plain.`). Every file key is
restart-only: a changed value logs `Logging:ArcaneCore:File:Path option can't be changed at reload; still ...`, the
wording vmangos uses for `World::configNoReload` (World.cpp:3044-3055). The `.reload config` command and the
`WorldConfigKeys` catalog (docs/areas/hot-reload.md) are scoped to the `World` section and do not touch `Logging`; the
logging section has no `.reload` verb and needs none, because the file watch already delivers it. `Logging:LogLevel`
reloads the same way, as it always did.

## Formats

Text (console and file; the colour variant differs only by SGR codes around the same fields, so
`sed 's/\x1b\[[0-9;]*m//g'` turns one into the other):

```text
2026-10-04 12:34:56.789 INFO ArcaneCore.World.Net.WorldServer[Perf:7] => session 42: Listening on 0.0.0.0:8085
2026-10-04 12:34:57.001 FAIL ArcaneCore.Realm.Net.LogonServer: Handshake failed
      System.IO.IOException: connection reset
         at ...
```

Level column: `TRCE DBUG INFO WARN FAIL CRIT`. Event id: `[Name:Id]`, `[Name]` or `[Id]`, omitted for the default id.
Exception lines are indented six spaces. Colours: trace grey, debug white, information green, warning yellow, error red,
critical white on red.

JSON lines (one object per event, keys stable, absent data omits its key):

```json
{"ts":"2026-10-04T12:34:56.789Z","level":"Warning","category":"ArcaneCore.Realm.Net.LogonServer","eventId":12,"eventName":"Auth","message":"Account TESTER failed 3 times","template":"Account {Account} failed {Attempts} times","props":{"Account":"TESTER","Attempts":3},"scopes":["peer 10.0.0.1"],"exception":"..."}
```

Strings are escaped per RFC 8259 (control characters as `\uXXXX`), numbers, booleans and null in the state are JSON
values, anything else its invariant string. Both formats cap a line at 256 KiB characters and mark the cut
(` ...[truncated]`), so a pathological exception cannot pin memory per event.

**Colour decision** (`ConsoleColorSupport`): colour only when `Console:Mode=Color`, stdout is not redirected,
`NO_COLOR` is unset or empty, and on Windows `SetConsoleMode(ENABLE_VIRTUAL_TERMINAL_PROCESSING)` succeeds (three
`kernel32` P/Invokes behind `OperatingSystem.IsWindows()`; any failure, including no console, means plain). The decision is
made once at start; a reload to `Color` on a redirected stdout stays plain.

## Rolling and retention

`RollingFileWriter`: the active file is `Path`; a rolled segment is renamed to `name-yyyyMMdd-NNN.ext` beside it (date
the segment was opened, `NNN` counts rolls that day), then segments beyond `Retain` are deleted, newest kept. On start an
existing active file is continued (its size counts toward the size roll) and, with `RollDaily`, archived first when its
last write is from an earlier day. UTF-8 without BOM. I/O failure (disk full, permissions): the file is closed, one line
goes to stderr, lines are dropped and counted for 5 s, then the file is reopened; resumption reports the lost count.
Nothing ever throws into the logging caller.

## Threads, ownership, allocation

- `ArcaneLogger.Log<TState>` runs on the caller's thread (world thread, network threads). It renders the message once,
  stamps the time, and for each enabled sink formats into a `stackalloc char[512]` `LogBuffer` (growing into `ArrayPool`
  only for long lines) and hands the span to the sink's writer. A sink that throws is counted (`LogSink.Faults`) and
  skipped.
- Every sink writer is a `QueuedLineWriter`: a fixed ring of `capacity` slots and one background thread
  (`ArcaneCore.Log.<sink>`). Enqueue copies the line into a pooled array under a short lock and never waits on the
  destination; a full ring drops the line (`Dropped` counter) and the writer thread emits one notice with the count the
  next time it drains, in the sink's own format (`console sink dropped 12 log line(s): the queue was full.`). Memory is
  bounded by `capacity` lines per sink.
- Flush: one per batch of up to 256 lines on the writer thread. Shutdown: the provider is disposed by the logger factory
  at host disposal and drains each queue (10 s bound), then flushes and closes the files; a `ProcessExit` hook flushes as
  well (5 s). A wedged destination (a pipe nobody reads) cannot hold the process: the thread is background and the drain
  wait is bounded.
- Proven allocation-free (`LoggingAllocationTests`, `GC.GetAllocatedBytesForCurrentThread` deltas of 0 over 2000
  iterations): a constant template with or without event id and colour through `LoggerFactory` into the text sinks; the
  text formatter alone; the JSON formatter without state. Known allocations, outside the fast path: the message string
  for a template with arguments (MEL's `FormattedLogValues.ToString`, same as the stock console), `exception.ToString()`,
  scope `ToString()` for non-string scopes, the boxing of a value-type state in the JSON sink (the MEL contract hands the
  state as `TState`), and the drop notice string.
- `ScopeText`: a `[ThreadStatic]` buffer that renders scopes, because `ForEachScope` cannot take a `ref struct` state.

## Tests

`tests/ArcaneCore.Realm.Tests/Logging` (the lightest project that references the kernel): `LogFormatTests` (plain and
colour line, four-character levels, colour strips to plain, exception indentation, scopes on and off, console modes,
colour decision table, JSON object with structured state, JSON escaping, local offset, buffer growth and cap, failing
sink isolation), `RollingFileWriterTests` (roll at size with segment names, daily roll, retention pruning, retain 0,
continuation of an existing file, archiving an earlier day's file, UTF-8 without BOM, unwritable path), `QueuedLineWriterTests`
(overflow accounting and the drop notice, dispose drain, wedged destination timeout, flush semantics, four producers
with order per producer), `LoggingAllocationTests`, `LoggingConfigTests` (check output per key, defaults accepted, bound
options rejected, `AddArcaneCoreLogging` refusal and provider replacement with `LogLevel` rules, reload live/restart-only
reporting and rejection of an invalid reload).

## Limits

- Colour and VT enabling on a real Windows console were not run here (Linux box); the P/Invoke path is guarded and
  falls back to plain on any failure.
- No syslog/journald native sink: journald captures stdout, use `Console:Mode=Plain`.
- Per-sink minimum levels (`Logging:ArcaneCore:LogLevel`) apply to the provider as a whole, not per sink; a sink-level
  filter would be a second rule set and is not built.
- Daily rolling checks the date at the first write of the new day; an idle server rolls at its next line, not at midnight.
