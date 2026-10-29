# Logging (infra lane `logging`)

Branch `ccr-build/infra-logging`. The structured logging subsystem in `src/ArcaneCore.Kernel/Logging`: one
`ILoggerProvider` (`ArcaneLoggerProvider`, alias `ArcaneCore`) that replaces the .NET host's default console provider in
both daemons, selectable from configuration, with a colour console, a plain console and one rolling log file whose format
is either text or JSON lines (`Logging:ArcaneCore:Format`). A daemon never writes two log files. Per-category minimum levels stay where they were (`Logging:LogLevel`). `LogSafe` (CWE-117 escaping of
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
| `Logging:ArcaneCore:Format` | `Json` when unset and no legacy key decides (see below) | restart | Content of the one log file: `Text` (same line layout as the plain console) or `Json` (one object per event, structured state kept). `Json` is the default because JSON keeps the structured state properties for ingestion, the console already gives a human-readable text view, and `Text` is one setting away (the in-repo log readers, `livecheck.ps1` and `tools/playerbot-policy/health_filter.py --tick-log`, read either format). Shipped `appsettings.json` files set `Json`. |
| `Logging:ArcaneCore:File:Enabled` | `false` | restart | The log file on or off. |
| `Logging:ArcaneCore:File:Path` | `logs/arcanecore.jsonl` for `Json`, `logs/arcanecore.log` for `Text` (shipped files: `logs/realm.jsonl`, `logs/world.jsonl`) | restart | Active file; relative to the working directory. Directory created on first write. One writer per file, across processes (a lock on `<path>.lock`, released when the writer closes or the process dies): a writer that finds the path owned writes `<name>.<process>.<ext>` instead (then `<name>.<process>.<pid>.<ext>`), says so once on stderr, and rolls that file under its own name. Give each daemon its own path. |
| `Logging:ArcaneCore:File:RollSizeMb` | `64` | restart | Roll before a write would take the file past this size; `0` never rolls by size. |
| `Logging:ArcaneCore:File:RollDaily` | `true` | restart | Roll at the first line of a new day (in the `Timestamps` clock). |
| `Logging:ArcaneCore:File:Retain` | `14` | restart | Rolled segments kept; oldest beyond this deleted after each roll. `0` keeps all. |
| `Logging:ArcaneCore:File:QueueCapacity` | `8192` | restart | Lines the file writer thread may hold before dropping. |
| `Logging:ArcaneCore:Json:Enabled` | `false` | restart | Deprecated alias, kept for old configs. With `Format` set it only switches the file on (like `Logging:ArcaneCore:File:Enabled`); with `Format` unset it selects the legacy JSON sink. |
| `Logging:ArcaneCore:Json:Path` | `logs/arcanecore.jsonl` | restart | Deprecated alias. With `Format` set (either format) it is used only when `Logging:ArcaneCore:File:Path` is unset; with `Format` unset it is the JSON file's path. |
| `Logging:ArcaneCore:Json:RollSizeMb` / `RollDaily` / `Retain` / `QueueCapacity` | `64` / `true` / `14` / `8192` | restart | Deprecated aliases. Read only when `Format` is unset and `Logging:ArcaneCore:Json:Enabled` selects the JSON sink; with `Format` set the tuning comes from `Logging:ArcaneCore:File:*`. |
| `Logging:ArcaneCore:Timestamps` | `Utc` | live | `Utc` or `Local`. Text: `yyyy-MM-dd HH:mm:ss.fff`. JSON: ISO 8601 with `Z` or the local offset. |
| `Logging:ArcaneCore:IncludeScopes` | `true` | live | Append active logger scopes (` => scope`) to text lines, `"scopes":[...]` to JSON. |
| `Logging:LogLevel:*` | host default | live | Unchanged: category minimum levels, applied by the logger factory before the provider sees an event. `Logging:ArcaneCore:LogLevel:*` overrides them for this provider alone (standard provider-alias rule). |

**One file, how it is resolved.** `LogFileSinkPlan` is the single resolution that the provider, the config checks, the
crash dialog and the dump directory all use.

- `Format` set (`Text` or `Json`): the file is switched on by `Logging:ArcaneCore:File:Enabled` or by the deprecated `Logging:ArcaneCore:Json:Enabled`. The path
  precedence, for both formats, is `Logging:ArcaneCore:File:Path` > the legacy `Logging:ArcaneCore:Json:Path` > the default for the format, so per-role
  environment `Json` paths never collapse the realm and the world onto one default. Rolling and queue
  tuning come from `Logging:ArcaneCore:File:*`. If `Logging:ArcaneCore:Json:Enabled` is true, a startup Warning (key `Logging:ArcaneCore:Json:Enabled`) says
  it is deprecated and the file is configured under `Logging:ArcaneCore:File`. Otherwise, if `Logging:ArcaneCore:Json:Path` is set and differs from
  `Logging:ArcaneCore:File:Path`, the same Warning is keyed `Logging:ArcaneCore:Json:Path`; a `Logging:ArcaneCore:Json:Path` equal to `Logging:ArcaneCore:File:Path`
  (compared as full paths) with `Logging:ArcaneCore:Json:Enabled` false does not warn.
- The section not in force (`Logging:ArcaneCore:Json:*` when the file section is `Logging:ArcaneCore:File`, and the reverse for a legacy `Json` file) is
  not used, but an out-of-range `RollSizeMb`, `Retain` or `QueueCapacity` in it is reported by `check-config` as a WARNING naming that key and saying
  the section is not in force. The same values in the section in force are errors.
- `Format` unset (legacy configs): `Logging:ArcaneCore:File:Enabled` alone writes `Text` at `Logging:ArcaneCore:File:Path`; `Logging:ArcaneCore:Json:Enabled` alone writes `Json` at
  `Logging:ArcaneCore:Json:Path` with the `Logging:ArcaneCore:Json:*` tuning; both enabled writes `Json` only at `Logging:ArcaneCore:Json:Path`, and a startup Warning names both
  paths and says the text sink is not being written; neither enabled writes no file. `Logging:ArcaneCore:Json:Enabled` alone (no `Format`) is
  a deprecated alias and also warns: set `Format=Json` and move `Enabled`/`Path`/tuning to `File`.
- Every one of these warnings is reported by `check-config` as a WARNING issue (key, message, fix) and logged at startup.
  Warnings never fail startup or sink construction, except that `Startup:Strict=true` turns every config-check warning into a startup
  failure, as for every other check; only errors fail otherwise.
- The locked-path fallback (`<stem>.<process>.<ext>`) and rolled segment names (`name-yyyyMMdd-NNN.ext`) apply to whichever
  file is written.

### Migrating from File+Json

A config that enabled both sinks used to write two files. It now writes one, so choose which format you want and move the
settings to `Logging:ArcaneCore:File`.

Before (two files; with no `Format` the daemon now writes only the JSON file and warns):

```json
"Logging": {
  "ArcaneCore": {
    "File": { "Enabled": true, "Path": "logs/world.log" },
    "Json": { "Enabled": true, "Path": "logs/world.jsonl", "Retain": 7 }
  }
}
```

After, JSON lines (the structured state properties are kept):

```json
"Logging": {
  "ArcaneCore": {
    "Format": "Json",
    "File": { "Enabled": true, "Path": "logs/world.jsonl", "Retain": 7 }
  }
}
```

After, text (read by eye or `grep`):

```json
"Logging": {
  "ArcaneCore": {
    "Format": "Text",
    "File": { "Enabled": true, "Path": "logs/world.log", "Retain": 7 }
  }
}
```

The live launchers follow the same rules. With `Logging:ArcaneCore:Format` set (environment or the profile's `appsettings.json`) they set only
`Logging__ArcaneCore__File__Path` per role (`<role>.debug.jsonl` or `.log`) and remove `Logging__ArcaneCore__Json__Path`, so the daemon has no
deprecated key to warn about. With `Format` unset (legacy) they set distinct `<role>.debug.log` (File) and `<role>.debug.jsonl` (Json) paths per role, so
the legacy resolution picks the right one and an older daemon binary that rejected equal paths still starts.

Environment variables work the same way (`Logging__ArcaneCore__Format=Text`, `Logging__ArcaneCore__File__Path=...`). Remove
the `Json` section once moved; leaving it in only produces the deprecation warning.

**Fail closed.** Every value is checked at start (`LoggingConfigChecks`): unknown enum text, non-numeric or out-of-range
numbers (`QueueCapacity` 1..1000000, `RollSizeMb`/`Retain` ≥ 0), an empty or directory-shaped path on the enabled file (the
key named is the one the path came from), a `Format` that is neither `Json` nor `Text`. The world lists them with its other config problems and exits 78;
the realm throws from `AddArcaneCoreLogging` before the host is built (one exception with every problem listed). The
raw-configuration check (`Check`, behind `check-config`) and the bound-options check (`ThrowIfInvalid`, run when the
provider is built and on every reload) are one rule set over the same typed values, defaults filled in, so a configuration
`check-config` accepts cannot fail at host build. An invalid reload is logged and rejected as a whole, and the running
settings stay.

**Hot reload.** The host re-reads `appsettings.json` on change (the generic host's `reloadOnChange`), and
`ArcaneLoggerProvider` follows `IOptionsMonitor<ArcaneLoggingOptions>`: the console mode, `Timestamps` and
`IncludeScopes` are applied at once and logged (`Logging:ArcaneCore:Console:Mode changed to Plain.`). Every file key is
restart-only: a changed value logs `Logging:ArcaneCore:File:Path option can't be changed at reload; still ...`, the
wording vmangos uses for `World::configNoReload` (World.cpp:3044-3055). The `.reload config` command and the
`WorldConfigKeys` catalog (docs/areas/hot-reload.md) are scoped to the `World` section and do not touch `Logging`; the
logging section has no `.reload` verb and needs none, because the file watch already delivers it. `Logging:LogLevel`
reloads the same way, as it always did.

## Formats

Text (console, and the file when `Format=Text`; the colour variant differs only by SGR codes around the same fields, so
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

JSON lines (the file when `Format=Json`; one object per event, keys stable, absent data omits its key):

```json
{"ts":"2026-10-04T12:34:56.789Z","level":"Warning","category":"ArcaneCore.Realm.Net.LogonServer","eventId":12,"eventName":"Auth","message":"Account TESTER failed 3 times","template":"Account {Account} failed {Attempts} times","props":{"Account":"TESTER","Attempts":3},"scopes":["peer 10.0.0.1"],"exception":"..."}
```

Strings are escaped per RFC 8259 (control characters as `\uXXXX`), numbers, booleans and null in the state are JSON
values, anything else its invariant string. Both formats cap a line at 256 KiB characters and mark the cut
(` ...[truncated]`), so a pathological exception cannot pin memory per event.

**Colour decision** (`ConsoleColorSupport`): colour only when `Console:Mode=Color`, stdout is not redirected,
`NO_COLOR` is unset or empty, and on Windows `SetConsoleMode(ENABLE_VIRTUAL_TERMINAL_PROCESSING)` succeeds (three
`kernel32` P/Invokes behind `OperatingSystem.IsWindows()`; any failure, including no console, means plain). The terminal's
half of the decision (`TerminalAllowsColor`) is probed once, the first time `Color` is applied, and kept for the process; the
mode half is applied at start and on every reload. So a daemon started `Plain` or `Off` on a real terminal renders colour
after a reload to `Color`, and a reload to `Color` on a redirected stdout stays plain.

## Rolling and retention

`RollingFileWriter`: the active file is the resolved path (`Logging:ArcaneCore:File:Path` or its default); a rolled segment is renamed to `name-yyyyMMdd-NNN.ext` beside it (date
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
- One file per daemon: there is no text and JSON pair any more. For a human view alongside a JSON file, use the console
  (`Console:Mode=Plain`) or switch `Format` to `Text`.
- No syslog/journald native sink: journald captures stdout, use `Console:Mode=Plain`.
- Per-sink minimum levels (`Logging:ArcaneCore:LogLevel`) apply to the provider as a whole, not per sink; a sink-level
  filter would be a second rule set and is not built.
- Daily rolling checks the date at the first write of the new day; an idle server rolls at its next line, not at midnight.
