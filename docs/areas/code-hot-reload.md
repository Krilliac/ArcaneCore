# Code hot reload

Change C# while the world daemon is running and have the change take effect without a restart.
Two independent lanes, both **off by default** and neither part of the retail server. Neither has
anything to do with the *content* reload lane (database rows); this page is only about code.

## Quick start: test my changes live

Goal: a server running on your PC that a real WoW 1.12.1 client can connect to, where a change to the C# source
shows up in the running server a few seconds after you save it, without restarting it or dropping the client.
Local test machine only: Debug build, Development environment, loopback addresses, a disposable database.

1. **Check the tools.** `dotnet --list-sdks` must list an 11.x SDK (this was measured with 10.0.401). The client's
   `realmlist.wtf` must say `set realmlist 127.0.0.1` (it already does at `D:\World of Warcraft Classic 1.12.1`).
2. **Start everything with one command**, from the repo root (never needs `-ExecutionPolicy Bypass`):

   ```powershell
   powershell -File scripts\dev-runner.ps1
   ```

   The first start builds Debug (a minute or two). It opens two console windows, **ArcaneCore dev: realm** and
   **ArcaneCore dev: world**, each running under `dotnet watch`, and prints the realm address (`127.0.0.1:3724`),
   the dev account name (`DEVGM`, an Administrator) and the path of the password file. The run directory is
   `%TEMP%\ArcaneCore-dev-dev` (`-Name other` makes another one); it holds fresh SQLite databases, the logs
   (`logs\world.log`, `logs\realm.log`) and `dev-account.txt` with the generated password.
3. **Log in with the real client.** Account `DEVGM`, the password from `dev-account.txt` (12 characters), realm
   "ArcaneCore Dev". Create a character and enter the world. (No client at hand? Build the mock client **before**
   you start the runner, see Troubleshooting, then run
   `dotnet tools\ArcaneCore.MockClient\bin\Debug\net11.0\arcane-mock.dll live --credentials-file %TEMP%\ArcaneCore-dev-dev\dev-account.txt --say ".server info"`.)
4. **Check that hot reload is on.** In game type `.hotcode status` (Administrator only). It reports the generation and
   `Process diverged from build: no`. Or run `powershell -File scripts\dev-runner.ps1 -Status`.
5. **Change code while connected.** Edit a `.cs` file under `src\`, for example the text of a reply in
   `src\ArcaneCore.World\Commands\BuiltinCommands.cs`, and save. The world window prints
   `dotnet watch : C# and Razor changes applied in ... ms.` (about two to three seconds after the save). Run the
   command again in game: the new text appears, on the same connection, with no relog. A new file with a new
   chat command or opcode handler group is picked up too (`Code hot reload applied (generation N)`).
6. **If the world window asks `Do you want to restart your app? Yes (y) / No (n) / Always (a) / Never (v)`**, the edit
   cannot be applied live (the line above names the reason, for example `ENC0047: Changing visibility of class requires
   restarting the application`). Nothing was restarted and nothing will be until you answer: your client stays
   connected and keeps running the old code. Answer `n` to keep going (revert the edit), or `y` to restart the world (the
   world saves, then every client is disconnected and has to log in again). The prompt blocks further reloads until it
   is answered.
7. **New code as a module** (a separate dll that you load, replace and unload without any restart, also useful for a
   Release build). Copy `tools\hotmodule-template\GmCommands`, edit it, then:

   ```powershell
   powershell -File scripts\dev-module.ps1 -Project tools\hotmodule-template\GmCommands
   ```

   and in game `.hotmodule load GmCommands`, then try `.modhello`. After the next edit rerun the script and use
   `.hotmodule reload GmCommands`; `.hotmodule unload GmCommands` removes it. The script puts the dll in the run's
   module directory and **approves its SHA-256** in `modules-allowlist.txt` (see "The module allowlist").
8. **Stop.** `powershell -File scripts\dev-runner.ps1 -Stop` sends Ctrl+Break to both windows: the world saves
   and both windows close. Start again later with `powershell -File scripts\dev-runner.ps1 -Reuse` to keep the
   databases, character and account. Without `-Reuse` an existing run directory is refused, never overwritten.

### What changes live and what needs a restart

The first four rows were measured on SDK 10.0.401 with the runner above (a mock client stayed connected throughout); the rest are the runtime's documented limits and are not re-measured here:

| You change | Result |
|---|---|
| The body of an existing method (a chat command's reply, a handler, a formula, a map updater) in `ArcaneCore.World` | Live, about 2.6 s from save to first changed reply; same process id, same connection |
| A new file with a new `ICommandGroup` / `IOpcodeHandlerGroup` / `[DefaultMapUpdater]` | Live: the registry refresh adds it (the registry refresh was applied 1.4 s after the save; a command sent 0.45 s after the save still got "There is no such command", the next one 5 s later worked). Existing maps keep their updaters |
| A private method rename, a method return type, a property type | The runtime **applied** them, but "applied" is not "works": the return-type change made the next call throw `TypeLoadException` (contained: the command reported failure, the client stayed connected) until the edit was reverted. Treat any signature change as a restart |
| The visibility of a class | Not applied: `ENC0047 ... requires restarting the application`, the watch prompt (step 6) |
| Static initializers and constructors, `static readonly` tables, DI wiring and constructor parameters, the set of `IWorldFeature`s / `IDataModule`s and the EF model, listener, port, tick interval, threads that already started, NuGet packages | Restart (the runtime cannot rerun them) |
| Any Release build | Never patched; restart |
| `appsettings` values and the run's `appsettings.json` | Restart (read once at start) |
| A module (`.hotmodule`) | Load, reload and unload live; module code only gets the interfaces the module lane exposes (opcode handler groups and chat command groups) |

An applied edit cannot be rolled back (`.hotcode status` says `Process diverged from build: yes`); reverting the
source is another forward edit, or restart. Edits are not tick-atomic.

### Troubleshooting

* **`port 3724 is already in use`**: another server (or another session's runner) owns it. The script names the
  process and does not touch it. Use `-RealmPort 3725 -WorldPort 8086` (and `set realmlist 127.0.0.1:3725` for the client).
* **A build error such as `MSB3027 ... is locked by ArcaneCore.World` while the runner is up.** Anything that
  references `ArcaneCore.World` (the mock client, the test projects, `dotnet build ArcaneCore.slnx`) tries to
  rebuild the running server's files. Build those first, or `-Stop` the runner. A hot-edited server must not be
  rebuilt underneath. Modules built from `tools\hotmodule-template` are safe: they reference the built dlls and never
  rebuild the world project.
* **No window appears / the windows are hidden.** The script starts each daemon in a normal console window of the
  PowerShell that runs it. When the script itself is started from a tool without a desktop (an editor task, an
  automation harness) Windows may hide them; the daemons still run and write `logs\world.log` and `logs\realm.log`.
  Run the script from a normal PowerShell window to see them.
* **`.hotcode` / `.hotmodule` say "There is no such command".** Your account is not an Administrator, or the lane is
  off. The dev account is an Administrator; the runner enables both lanes for its own processes only.
* **The world refuses to start (exit 78, `refuses to start`)**: `HotCodeGuard`. Read the message: hot reload was
  requested outside Development / Staging, or the runtime is set up for hot reload without
  `World:HotCode:Enabled` (a debugger session sets `DOTNET_MODIFIABLE_ASSEMBLIES` too).
* **`.hotmodule load X` answers `refused by the module allowlist`.** The dll's SHA-256 is not in
  `modules-allowlist.txt`: run `scripts\dev-module.ps1` (it builds and approves), or add the hash yourself.
* **`Unloaded modules not yet freed by the runtime: 1`.** The runtime has not collected the unloaded module yet; the
  host never forces a GC. It reached 0 here only after a collection (a hot-edited command that called `GC.Collect()`
  took it from 1 to 0); a count that stays above 0 after collections means module code is holding a reference.
* **`-Stop` says `STILL RUNNING`.** Close the named window by hand (the world may not have saved); check
  `logs\world.log` for `World saved and stopped`.
* **Forgot the password.** Delete `dev-account.txt` and start with `-Reuse`: the account's password is reset and a
  new file is written.
* **After a restart the client is dropped.** Expected: a restart (yours, or `-RestartOnRudeEdit`) ends the process.
  On SDK 10.0.401 that restart ran the host shutdown and logged `World saved and stopped`; the new process starts
  with every client disconnected.

## The two lanes

| Lane | For | Needs | Switch |
|---|---|---|---|
| **Development runner** (`dotnet watch`) | editing the server's own source and seeing it live | SDK, Debug build, Development or Staging | `World:HotCode:Enabled` |
| **Module lane** (collectible load contexts) | loading, replacing and unloading extension assemblies in a running server, including a Release build on a machine without the SDK | the compiled module dll | `World:HotCode:Modules:Enabled` |

The module lane is described in "Module lane" below; everything before it is the development runner.

Vanilla fidelity: with `World:HotCode:Enabled=false` (the default) and a normal launch, the
server behaves exactly as before. Nothing is loaded, nothing is patched and no command is added.

## What it is, in one paragraph

`scripts/hot-runner.ps1` (or `.sh`) starts the daemon under `dotnet watch` in a **Debug** build.
`dotnet watch` hands every saved edit to the .NET runtime's metadata-update support
(`System.Reflection.Metadata.MetadataUpdater`), which swaps method bodies inside the live
process. The server itself contributes only the safety gate (below). The runtime and the SDK do
the patching; this lane has no code loader of its own (the module lane below is the loader).

## Using it

For the full local test environment (realm + world, a dev account, windows, stop) use `scripts/dev-runner.ps1` (see "Quick start" above); the lower-level `hot-runner` scripts below only start the world daemon under `dotnet watch`.

```powershell
scripts/hot-runner.ps1                       # interactive: asks before restarting
scripts/hot-runner.ps1 -NonInteractive       # restarts by itself on an edit that cannot be applied live
scripts/hot-runner.ps1 -AuditLog C:\tmp\hotcode.log
```

```bash
scripts/hot-runner.sh [--non-interactive] [--audit-log PATH]
```

The scripts refuse to run unless `DOTNET_ENVIRONMENT` is Development or Staging (unset means
Development), set `World__HotCode__Enabled=true` for that one process, and build Debug.
Since tier 2b the bot code is its own assembly, the Playerbots pack, which the world executable cannot reference (see "The
Playerbots pack as its own assembly" below). The scripts therefore build `src/ArcaneCore.Playerbots` in Debug before they start
`dotnet watch` (a failed build exits 4 and starts nothing), and `dotnet watch` watches the world project and the projects it
references only: **an edit to a file under `src/ArcaneCore.Playerbots` is not applied live, and needs a restart of the script.**
A restart that `dotnet watch` makes on its own (a rude edit) rebuilds the world but not the pack, so restart the script instead
when the edit changed a world member the pack uses.
Edit a `.cs` file under `src/`, save, and the watch window reports whether the change was applied
or why it could not be. The spike below proves a body edit in a one-project app; an edit in a
referenced project of this solution (for example `ArcaneCore.Game` while `ArcaneCore.World`
runs) is expected to work the same way but has not been demonstrated here.

When you edit a method **body**, the next call runs the new code. Existing objects keep their
state, the process keeps its id, and connected clients stay connected.

## `.hotcode` (Administrator, only when enabled)

| Command | Does |
|---|---|
| `.hotcode status` | Generation and last applied generation, frozen / degraded state (and why), refreshes applied and rejected, and **Process diverged from build**: yes once the runtime has applied any code edit. |
| `.hotcode refresh` | Rescan now under a new generation. The answer arrives from the next tick. |
| `.hotcode freeze` / `thaw` | Stop / resume registry refreshes. Edits the runtime already applied stay applied; freeze only stops the server from picking up *new* registrations. |

The root exists only with `World:HotCode:Enabled=true` (even an Administrator gets "There is no
such command" otherwise), is appended last so it cannot change how any existing abbreviation
resolves (tested for every prefix of every existing root at every security level), and every use
is audited. None of these commands loads code.

"Diverged from build" matters: an applied edit cannot be rolled back. If the source on disk and the
running process disagree (you reverted the file, or an edit was applied then the tree was changed),
the process still runs the edited bodies until restart.

## `.worldport` (Tier 3 spike, Administrator, only when enabled)

A development-only experiment for [Hot reload tiers](../design/hot-reload-tiers.md) (section 5.5): does a
1.12.1 client accept `SMSG_NEW_WORLD` to the map it is already on? vmangos never sends that (a same-map teleport is a near one),
and phase 3b's handoff depends on the answer. The root exists only with `World:HotCode:Enabled=true` (so only in a Development or
Staging environment, see the launch gate below), is Administrator only, is appended last like `.hotcode`, takes no path or code
argument, and every request is audited (kind `worldport-spike`).

| Command | Does |
|---|---|
| `.worldport far [#playername] [$x $y $z [$o]]` | Far teleport of the named or selected player (or yourself) to the map it is on, through the existing far path with `TeleportOptions.ForceFar`: `SMSG_TRANSFER_PENDING` and `SMSG_NEW_WORLD` with the same map id, the player out of its map until `MSG_MOVE_WORLDPORT_ACK`, then the login packets and the map add exactly as after a cross-map teleport. Without coordinates, to where the player stands. |
| `.worldport readd [#playername]` | Comparison: the player is taken out of its map and added back where it stands at the start of the next tick, with the login packets around the add (its own create block and the visible set are sent again), and no transfer packet. |
| `.worldport status` | The open runs and the last 16 finished ones, each step with its wall-clock time since the request. |

Refused (nothing sent): a player in no map, already teleporting or with a run open, aboard a ship, on a battleground map, or with no
client (a lean bot). The console can run them with a player name.

What is recorded, as log lines starting `worldport-spike #<run> <far|readd> <player> +<ms> ms <step>:` (category
`ArcaneCore.World.Gm.Worldport`) and in `.worldport status`:

| Step | Meaning |
|---|---|
| `requested` | Who asked, from where to where. |
| `far-scheduled` | The far teleport was accepted; it runs after the map's update. |
| `transfer-sent` | `SMSG_TRANSFER_PENDING` then `SMSG_NEW_WORLD` (same map) went out; the player is in no map. |
| `worldport-ack` | The client's `MSG_MOVE_WORLDPORT_ACK` arrived (the time from `transfer-sent` is the loading screen). |
| `removed` (readd only) | The player was taken out of its map. |
| `re-added` | Back in a map, with the map id and position; a different map fails the run. |
| `ack-timeout` | No ack within 60 s (the teleport stage is named). The run stays open for a late ack, and is abandoned after 10 minutes. |
| `anomaly` | A `teleport/*` or `session/*` anomaly reported about this player while the run was open. |
| `client-left` | The player left the world (logout, disconnect, client crash): the run fails. |
| `observed` | Still in the world 15 s after the re-add: the run ends `ok`. |

The run's last line is `finished after <ms> ms: ok` or `failed: <why>`. The server cannot see a client-side error dialog or a
client that sits in the loading screen forever beyond the `ack-timeout`; the human or agent driving the client writes that down.

## Configuration (`World:HotCode`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Opt in. Accepted only when the host environment is Development or Staging. |
| `AuditLogPath` | empty | Append-only file, one tab-separated line per decision (`start-allowed`, `start-refused`). Not tamper-proof: it is written by the same account the server runs as. Lines are queued and written by a background writer in order; the queue is flushed at process exit. |
| `MaxConsecutiveFaults` | `50` | Fault breaker, effective only while `Enabled`: a map updater that throws in this many consecutive ticks is skipped (and logged once at error level) until the next applied code edit, or a restart, instead of failing every tick. `0` disables it. An explicit `World:MaxConsecutiveUpdaterFaults` wins. The vanilla default of that setting is `0`: unchanged behavior. |

The audit file never blocks the world thread: a line is only queued, and one background writer per file appends the lines in order. The
queue is flushed (for at most 10 s) when the process exits. Failures are counted in the fault ledger (`.server faults`) and logged:

| Ledger site | Meaning |
|---|---|
| `hotcode.audit-write` | A disk append failed; the lines of that batch are lost. |
| `hotcode.audit-flush` | A flush gave up after 10 s (a wedged disk); queued lines may be lost. |
| `hotcode.audit-report` | Reporting one of the faults above failed (a throwing logger); the writer keeps running. |
| `hotcode.audit-dropped` | The 4096-line queue was full (a wedged disk), so the line was dropped instead of blocking the world thread. |

When enabled, startup logs a warning: `Code hot reload is ENABLED (World:HotCode:Enabled, environment ...)`.

## The launch gate

`dotnet watch` injects its agent before any ArcaneCore code runs, so it cannot be vetoed later.
The only place to say no is startup, and `Program.cs` does it before the host is built and
before any database initializer runs (`HotCodeGuard.Enforce`). The server exits with code 78 and
an actionable message when:

* the process is set up for hot reload (`MetadataUpdater.IsSupported`, `DOTNET_WATCH=1`,
  `DOTNET_MODIFIABLE_ASSEMBLIES`, or a `DotNetDeltaApplier` startup hook) while
  `World:HotCode:Enabled` is false. This includes a Visual Studio / Rider debug session, which
  sets `DOTNET_MODIFIABLE_ASSEMBLIES`: start that session with `World__HotCode__Enabled=true`
  (Development) or unset the variable;
* `World:HotCode:Enabled` is true in any environment but Development or Staging.

There is deliberately no switch to relax the environment rule.

Measured on SDK 10.0.401 (see `tools/hotcode-spike`): the app sees `DOTNET_WATCH=1`,
`DOTNET_MODIFIABLE_ASSEMBLIES=debug` and `MetadataUpdater.IsSupported=True`; it does **not** see
a populated `DOTNET_STARTUP_HOOKS` (the variable exists but is empty), so the startup-hook check
is only a fallback and the other three signals do the work.

## What can and cannot be reloaded

Reloadable live (Debug build, verified by the spike for a body edit):

* bodies of existing methods: opcode and chat-command handlers, `IMapUpdater.Update`, spell and
  aura lambdas, formulas, packet builders;
* new methods, new lambdas, new private types, and new fields and types (see the limits below).

**Needs a restart.** The runtime, not ArcaneCore, decides, and the watch window says
`Restart is needed`:

* what the compiler marks as a rude edit (the watch window says `ENC####: ... requires restarting the application`;
  measured: changing the visibility of a class), base type or interface changes, generic arity changes. **Do not
  assume a signature change is refused**: on SDK 10.0.401 a method return-type change, a private method rename and a
  property type change were all *applied* live, and the return-type change then made the next call throw
  `TypeLoadException` until it was reverted (the spike below saw a signature edit restart the process; the two
  measurements disagree, so the SDK/runtime decides, not this page). Treat any signature change as needing a restart;
* static initializers and static constructors of existing types, including `static readonly`
  tables and `FrozenDictionary` snapshots built from code: they ran once and are not re-run;
* the set of `IWorldFeature` types, `IDataModule`s and the EF model, DI wiring and constructor
  parameter lists, the listener, bind address, port and tick interval, timers and threads that
  already started, NuGet packages;
* **any Release build.** An optimized build cannot be patched (verified: in Release a body edit
  only shows up after a restart).

Subtleties to know before trusting a patched process:

* A new field added to an existing class reads `default(T)` on objects that already exist.
* Edits are applied when the watcher delivers them, not at a tick boundary: one world tick can
  run some call sites on the old body and some on the new one.
* There is **no rollback** of an applied edit. To undo it, revert the source (that is a forward
  edit) or restart.
* A hot-patched map updater that throws every tick is skipped by the fault breaker (see `MaxConsecutiveFaults`); the next applied edit gives it another chance. Chat commands that throw are already contained per invocation (the invoker is told the command failed).
* **Debug tick cost is unmeasured.** Hot reload needs a Debug (unoptimized) build. How much slower a busy map ticks against the 50 ms budget has not been measured here; do not assume it fits, and do not run a realistic load test on the hot runner and read it as Release performance.
* Registries built once at startup (opcode table, chat command table, default map updaters) are
  refreshed for *added* handlers, commands and updaters; see "Refreshing registries".

## Refreshing registries (what the server adds on top of the runtime)

The runtime patches method bodies in place, so a handler whose body you edited simply runs the
new code. What it cannot do is tell the tables the server built once at startup about
**new** things. With `World:HotCode:Enabled=true`, `HotCodeMetadataHandler` (the runtime calls
its `UpdateApplication` after every applied edit; measured by the spike: once per edit, on a
thread-pool thread, never the main thread) triggers `HotCodeRefresh`, which:

1. rescans for `IOpcodeHandlerGroup`, `ICommandGroup` and `[DefaultMapUpdater]` types (off the
   world thread, so a bad edit cannot stall a tick);
2. builds a candidate and checks it: two groups claiming one opcode, a chat root that equals an
   existing one or is a proper prefix of one (it would change how an abbreviation resolves), or an
   invalid marked updater **rejects the whole refresh**;
3. commits through `WorldRuntime.Post`, i.e. at the start of a tick before any map update, as
   pointer flips: `OpcodeTable.Replace` (atomic, readers never see a half-filled table),
   `CommandTableSource.TryAdd` (immutable table, whole-reference swap), `DefaultMapUpdaters.Commit`.
   A failed commit restores the previous opcode table.

Rules of the refresh:

* **Additive only.** Handlers, roots and updaters that already exist are kept; nothing is removed
  (a removed type needs a restart anyway). Editing a group so it registers one more opcode or
  command is picked up, because the rescan creates fresh group instances.
* **All or nothing, old state kept.** A rejected refresh is logged at error level, audited as
  `refresh-rejected`, and leaves every registry as it was; the state is marked degraded until a
  later refresh succeeds.
* **Idempotent by generation.** A generation at or below the last applied one does nothing.
* **Existing maps keep their updaters.** A new default map updater is attached to maps created
  after the commit; maps that already exist never get it (restart, or it only appears in new
  instances).
* A commit the world thread does not run within 10 seconds is abandoned (and can no longer
  apply), reported as rejected.

Verified for real: with the daemon running under `dotnet watch` (Debug, SQLite databases), adding
a file with a new `ICommandGroup` produced `Code hot reload applied (generation 1): ... 1 command
roots (.zzhote2e)` and an audit line, with the process not restarted. That was a manual run
(not a committed test); the committed tests drive the same classes with a fake catalog and the
in-process test host.

## Security model

Code edited into a running server runs with the server's full trust. Therefore:

* Off by default; the launch gate refuses a hot-reload-capable process that did not opt in, and
  refuses an opt-in outside Development / Staging.
* The only trust boundary is who can write the source tree and who can start the process. Anyone
  who can edit a `.cs` file in a watched checkout, or reach the watch agent on the machine, can
  run code as the server. Use it on your own machine.
* The audit log records start decisions; a compromised process can rewrite it.
* The development runner adds no in-game or network command that loads code. The module lane does (`.hotmodule`, Administrator only, only when enabled; see its section).

## Module lane (load, replace and unload code in a running server)

Works in a **Release** build on a machine **without the SDK**: it needs only the compiled module.
Independent of the development runner (`World:HotCode:Enabled` may stay false).

### What a module is

A folder `<Directory>/<name>/` holding `<name>.dll` (the assembly must be named `<name>`) and any
private dependencies. It may contain public, non-abstract classes with a public parameterless
constructor implementing `IOpcodeHandlerGroup` and/or `ICommandGroup` (the interfaces the server's own
features use), `ICreatureAiGroup` (creature AI by entry or by `AIName` / `ScriptName`, see "Creature AI and script hooks")
and/or any global script hook interface (`IPlayerHooks`, `IWorldHooks`, ...), so a module is written like a built-in
feature. It may also contain spell scripts (`ISpellScript` with `[SpellScript(ids)]`), instance scripts (`InstanceData` with
`[InstanceScript(mapId)]` and a constructor taking a `Map`) and EventAI handlers (`EventAiEventHandler` / `EventAiActionHandler`
with a parameterless constructor), see "Spell scripts, instance scripts and EventAI handlers in a module"; those three are found
the way the server finds its own (any concrete class, public or not). Nothing else in the assembly is looked at. Build it against the server's `ArcaneCore.World` (and `ArcaneCore.Protocol`,
`ArcaneCore.Kernel`, ...); those, the framework and anything the server already has loaded are always
resolved to the server's copy, so the interfaces are the same types on both sides. A module and the
server must be built from compatible versions: a module compiled against a different `ArcaneCore.World`
fails when a type does not load or when its code runs; that is not detected up front.

### Using it

```
.hotmodule list
.hotmodule load <name>      # <Directory>/<name>/<name>.dll
.hotmodule reload <name>    # build the new version, then swap it for the loaded one
.hotmodule unload <name>
```

Administrator only; the root exists only with `World:HotCode:Modules:Enabled=true` and is appended last
(tested: it cannot change how any existing abbreviation resolves). The answer arrives from the next
tick. The commands take a module **name**, never a path and never code; the operator puts the dll in the
module directory, so the trust boundary is who can write that directory.

### Configuration (`World:HotCode:Modules`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Opt in. With it off no module object, command or hosted service exists. |
| `Directory` | empty | Required when enabled; the server refuses to start without it (exit 78). |
| `AllowAnyEnvironment` | `false` | Modules are accepted in Development and Staging only. Set this to allow them in a deployed (Production) instance: the operator saying that code may be loaded there. Has no effect on the dotnet-watch lane, which stays Development / Staging only. |
| `Allowlist` | empty | Path of a file of SHA-256 hashes (see "The module allowlist"): a module whose hash is not listed is refused and audited. Empty: no hash check, except that a non-Development/Staging instance (`AllowAnyEnvironment`) refuses to start without it. |
| `LoadOnStart` | `[]` | Module names loaded once the world is running (a failure is logged, not fatal). |

`World:HotCode:AuditLogPath` is shared with the other lane. Startup logs a warning when modules are enabled.

### The module allowlist (`World:HotCode:Modules:Allowlist`)

A text file of SHA-256 hashes, one per line. A module is loaded only if the hash of its dll's bytes is listed.

```text
# blank lines and lines starting with # are ignored
6136F56CE4E04BEDFE27F41F69EF05E928797D01BF3247E8FD8582387A17801D  GmCommands v1  (anything after the hash is a label)
0e14067c88e64702f938da183961b51777104e0e5c1d559692248cb2d3153a36  GmCommands v2  (hex in either case)
```

* **Compute a hash**: `Get-FileHash -Algorithm SHA256 <module>\<name>.dll` (PowerShell) or `sha256sum <name>.dll`.
  `scripts\dev-module.ps1` does it and appends the line for the dev runner. `.hotmodule load` also prints the
  hash, and a refusal names it, so you can approve exactly what you saw.
* **Where it lives**: the key is a path in the host configuration
  (`World__HotCode__Modules__Allowlist=C:\path\allow.txt` or `World:HotCode:Modules:Allowlist` in `appsettings`).
  The dev runner sets it to `<run directory>\modules-allowlist.txt`. Keep the file writable only by the operator: it is
  the approval, so whoever can edit it decides what code the server may load. The in-game command cannot add a hash.
* **Refusal**: the load or reload is rejected before any module code runs; the running version, if any, stays in
  force; the audit log gets `module-rejected ... refused by the module allowlist: sha256 <hash> is not in the allowlist`.
* **Fail-closed once configured**: a missing, unreadable, larger than 1 MB or malformed file (any non-comment line
  whose first token is not 64 hex digits, reported with its line number) refuses every module. A malformed line is
  never skipped: a typo must not silently shrink the list you reviewed. An empty list (only comments) allows nothing.
* **Read on every load and reload**, so approving a new build needs no restart. The hash is taken from the very
  bytes that are then loaded (no second read), so the file cannot be swapped between check and load.
* **Empty key (the default)**: no hash check; any dll that is in the module directory can be loaded by an
  Administrator. That is the behaviour of a Development or Staging run with no allowlist, and startup logs a warning
  saying so. It is **not** accepted for a Production instance: `AllowAnyEnvironment=true` outside Development / Staging
  without an `Allowlist` refuses to start (exit 78).

The allowlist is a list of approved builds, not a signature: it says nothing about who produced a dll, and anyone who
can write to the allowlist file or to the server's process can run code as the server.

### What happens on `load`, `reload` and `unload`

1. **Name and path checks**, before any file is read: a plain name only (letters, digits, `_`, `-`, dot
   separators, starting with a letter); it must resolve to a folder directly under `Directory`; a module
   folder or dll that is a link (symlink or junction) is refused; the dll is at most 64 MB; at most 32
   modules are loaded.
2. **Load into a fresh collectible `AssemblyLoadContext`, off the world thread.** Assemblies are read
   into memory and loaded from bytes, so no file is locked and the dll can be replaced on disk while
   the previous version is still loaded (that is what `reload` is for). The module's group constructors
   and `Register` run here, on a thread-pool thread, with full server trust.
3. **Candidate checks.** Nothing to contribute, a missing parameterless constructor, a type that does
   not load, a root name that is empty, has whitespace or repeats inside the module: rejected.
4. **Commit on the world thread at the start of a tick** (`WorldRuntime.Post`, the same path as the
   registry refresh): the opcode table and chat command table are swapped as whole references, and a
   module's old handlers and roots leave in the same step its new ones arrive. An opcode or root the
   module does not own (built-in, another module's, the registry refresh's, a root that would shadow
   an existing abbreviation) rejects the whole module. Both checks run before either table is touched,
   so a rejection changes nothing. A commit the world thread does not run within 10 seconds is
   abandoned and can no longer apply.
5. **Failure keeps the old version.** A bad `reload` (unreadable file, not a managed assembly, clash,
   timeout) leaves the running version in force and unchanged; the rejected candidate's context is
   released. Every decision is audited (`module-load`, `module-reload`, `module-unload`,
   `module-rejected`; lines dropped on a full queue are counted as `hotcode.audit-dropped`) with the dll's SHA-256, and logged (a rejection at error level).

### Creature AI and script hooks in a module

A module can also contribute creature AI and global script hooks. Both are validated before anything live changes and
swapped at the same tick boundary as the opcode and command tables; a clash (an entry or name the server or another
module already registered, a hook class that overrides nothing) rejects the whole module and leaves everything as it was.

* `ICreatureAiGroup.Register(ICreatureAiRegistry)`: `ForEntry(entry, creature => new MyAi(creature))` or `Named("MyAI", ...)`
  (selected by a template's `AIName` / `ScriptName`). Same slots as the server's own entry scripts and named AIs.
* Script hooks: a public class with a public parameterless constructor implementing e.g. `IPlayerHooks` and overriding only the
  methods it needs. On a swap every dispatch array is rebuilt from the hook objects that stay (registration order) plus the new
  ones, so an unloaded module's hooks are referenced by nothing. The new arrays are built completely before they replace the old
  ones, so a failure while building leaves the live hooks untouched. The bridges to game events (equipment, group, guild and
  spell hooks) are subscribed the first time any hook of that kind exists, so a module may add `IItemHooks.OnEquip` /
  `OnUnequip`, `IGroupHooks`, `IGuildHooks` or `ISpellHooks` even when no built-in module did (players already online are bound at
  that moment). **`IWorldHooks.OnStartup` is rejected for a module**: it runs once while the world starts, which has already happened.
* **Creatures that already exist.** A creature whose entry, `ScriptName` or `AIName` the module registers, or whose current AI is
  code of the module being replaced or unloaded, gets a new AI object **at once** unless it is in combat (a new AI would not have
  seen the fight begin). Only the AI object is rebuilt: position, health, threat and motion are untouched. Dead creatures are
  switched too (the new AI hears `OnRespawn`). Creatures in combat keep the old AI, are counted in the reply
  (`N in combat keep the old AI for now`) and retried every 5 seconds for up to 10 minutes (only those creatures: one that already switched is never rebuilt, which would
  wipe its AI state); one still in combat after that keeps
  the retired module alive (`Unloaded modules not yet freed` stays above 0) until it gets another AI. Pets and charmed creatures
  are never touched.
* A module's AI state does not carry over a replace; the new AI starts from nothing.
* Known limits: swapping rebuilds only the AI object. It does not run the creature's `ForgetAi` cleanup (summons, scripted despawns and
  arrival relays the old AI had queued stay queued) and does not call `OnRespawn` on the new AI of a living creature (a dead one hears it
  when it respawns). A map-specific entry AI (and the built-in dungeon boss scripts) is consulted before the module's registration, so
  a module cannot shadow those entries.
* **A failure after validation is rolled back.** Every slot (opcodes, creature AI, script hooks, spell scripts, instance scripts,
  EventAI, commands) is validated before any changes. The commit records an undo step for each slot it changes (`SwapUndo`, the
  content lane's `ReloadTransaction` pattern), and a failure in any slot puts every slot back exactly as it was: the same handler,
  command, hook and script objects in the same order. Spell, instance and EventAI registries restore their saved table
  (`Save()` / `Restore(Checkpoint)`), so instance data and bound EventAI handlers from before the swap count as current again.
  Live creatures get the new AI only after every slot is in. Then the owner policy: a failed version is never left in place.
  * A failed **load** leaves nothing registered.
  * A failed **unload** leaves the module loaded as before.
  * A failed **replace** also unloads the previous version in the same tick. The last good version is then rebuilt from the
    assembly bytes kept when it was loaded (the file on disk may have changed), checked against the allowlist again, and loaded.
    If that restore fails too, the module stays fully unloaded and both errors are reported.

  Each step is audited (`module-failed`, then `module-unload`, then `module-restore` or another `module-failed`) and announced in order: failed, then unloaded,
  then restored. The reply and the announcement say `Failed`. If an undo step itself throws, the reply says `ROLLBACK
  INCOMPLETE` and names the slots that could not be put back; only a restart restores a known state then.
* A `SpellScriptRegistry.ScriptsChanged` handler that throws (for example, chaining a newly declared execute effect fails) undoes the
  spell script swap and throws, so the module swap fails and is rolled back as above. Before this, the failure only reached the
  fault ledger.
* Not available to modules yet: map-specific entry AI, `IWorldFeature`s and tables.

### Spell scripts, instance scripts and EventAI handlers in a module

Validated together with the creature AI and script hooks (all or nothing) and committed at the same tick boundary; a replace
removes the old version's entries and adds the new ones in one step, an unload removes them. Each registry swaps its whole table
by one reference write, so a reader sees the old entries or the new ones, never a mix. A module never overrides or removes a
**built-in** entry (one the server discovered at startup): a spell id, map id or EventAI type id that has one rejects the whole
module and nothing changes. Two modules cannot claim the same id either. The startup scan of all three is over the startup code
set (`CodeAssemblies`, today the Game assembly; no pack is configured at startup).

| Kind | Live registry the module swaps | When the change is seen |
|---|---|---|
| Spell script (`[SpellScript(ids)]`) | `SpellScriptFeature.Registry` (the one the spell script dispatcher uses) | The next hook of any cast, including a cast already under way: nothing caches a script. A newly declared `ExecuteEffects` effect is chained on commit; the chain stays after unload but then only calls the handler it wraps, and `SpellSystem.HasBuiltInEffectHandler` looks through it again (so quest reward preflight refuses TeleportUnits/Summon rewards only while a loaded script declares that effect, as after a restart). A spell script that schedules its own work (timer, event, closure) keeps running the old code until that work ends. |
| Instance script (`[InstanceScript(mapId)]`) | The instance manager's `Scripts` (`InstanceScriptRegistry.Default`) | Instance maps created afterwards. A loaded instance keeps its `InstanceData` (it holds the instance's progress) until it unloads. |
| EventAI action handler | `EventAiRegistry.Default` | The next action run (looked up per action). |
| EventAI event handler | `EventAiRegistry.Default` | Creatures whose EventAI is built afterwards (spawn, respawn). Database creatures built before are respawned in staggered batches (below); a temporary creature keeps the old handler until it goes. |

A server without one of these registries (the spell script feature, the instance feature or the creature systems not running)
rejects a module that **adds** that kind ("... are not available on this server"); a host built without game seams rejects it
before anything is committed. A provider can also return null later, for example when a feature is detached. If the module then
only **removes** that kind (an unload, or a replace whose new version no longer has it), the removal is a no-op and the unload
succeeds, so a module never gets stuck loaded. Additions stay rejected. A registry that went away is not touched, and whatever it
still holds is its owner's to drop.

**EventAI event handlers reach live creatures.** After a swap that changes an event type, the database creatures whose EventAI rows use
that type and are still bound to a handler that is no longer current are respawned through the map's own `ForcedDespawn` /
`ForceRespawn` path. This runs in batches (`EventAiRespawnPolicy`, default 4 creatures per second, at most one batch per world tick).
Creatures in combat are deferred and retried, and given up on after 10 minutes. Temporary creatures are left alone. The reply and the
announcement carry `eventai_respawns_queued`. The policy is a `ModuleGameSeams` constructor argument only; no configuration key
sets it yet, so production uses the default.

**Retired code still running.** A loaded instance created from a replaced or removed instance script, and a creature whose EventAI
still has a row bound to a replaced or removed event handler, keep the old module version alive. `.hotmodule list` names them after
the `Unloaded modules not yet freed` count (`still running retired code: N instance maps until they unload, M EventAI creatures
until they respawn`, shown only when either is above 0; `ModuleHost.RetiredGameReferences`). Spell scripts have no such count:
only work a script scheduled itself can hold one, and no registry can see that.

### Announcements and the reason argument

Every load, replace, unload and rejection is reported to the hot swap announcer (`World:Announce:HotSwap`, see
[Hot swap announcements](hot-swap-announcements.md)). `.hotmodule load|reload|unload <name> [reason]`: the text after the name is the reason.

### Deploying a module to a Production server

Production needs `World:HotCode:Modules:Enabled=true`, `Directory`, `AllowAnyEnvironment=true` and an `Allowlist` file. Build the module against
the Release build the server runs (`dotnet build -c Release -p:ArcaneCoreBin=<server bin folder>/ -o <staging>`; the template csproj takes its
references from `ArcaneCoreBin`), copy `<name>.dll` to `<Directory>\<name>\`, then approve and load:

```powershell
powershell -File scripts\hotmodule-hash.ps1 -Name GmCommands -ModuleDirectory D:\arcane\modules -Allowlist D:\arcane\modules-allowlist.txt
# in game / console:  .hotmodule load GmCommands first deploy
```

`scripts/hotmodule-hash.ps1` prints the SHA-256 of the exact file and appends it to the allowlist; it never loads anything.

On the local live server, `tools/ops/live/redeploy.ps1` does the copy and the `.hotmodule load`/`reload` itself when a module
project changed and nothing else needs a restart, through the World's control pipe; it never adds a hash, so approve the build first
([Redeploy prefers hot reload](../ops/redeploy.md)).

The allowlist covers `<name>.dll` only. A private dependency dll copied into the module folder is **not** hashed (and is loaded
with the module), so keep the module directory writable only by the operator, or ship the module as one self-contained dll.

### Unload: what is proven and what is not

Proven by a committed test (`ModuleHostTests`): after `unload` or a replacing `reload`, with nothing
holding a module object, the runtime frees the load context (a `WeakReference` to it goes dead after
a GC), and `.hotmodule list` shows the count of unloaded modules not yet freed
(`RetiredStillReferenced`). The same test also proves the detector works: when a test deliberately keeps
a module's handler delegate alive, the count stays at 1 until it is released.

Not guaranteed, because it depends on module code the host cannot see: a thread or timer the module
started, a task still running its code, an event subscribed on a server object, a static reference from
the server to a module type, or a module object stored in a server collection keeps the context alive.
That is a leak, and `Unloaded modules not yet freed` stays above 0 after the runtime has had time to
collect. The host never forces a garbage collection itself. A module should create nothing that outlives
its handlers. A handler that is executing when its module is unloaded finishes normally.

Also true: module code is not tick-atomic (a call that started on the old version finishes on it),
the old and new handler delegates are different objects, and a reload does not migrate module state;
the new version starts from nothing.

### Security

Code in a module runs with the server's full trust: it can read the database credentials in the
process, open files and sockets. Therefore: off by default and independent of every other switch;
refused without a directory; refused outside Development / Staging unless the operator sets
`AllowAnyEnvironment`; Administrator-only command with no path or code argument; link and size checks;
SHA-256 in every audit line. The directory must be writable only by the account that runs the server
(and the operator who deploys): anyone who can write there and has an Administrator in-game account can
run code as the server. The audit log is written by the same account and is not tamper-proof. There is
no signature check: the allowlist (above) approves builds by hash, not authors. Do not point this at a directory other people can write to.

### Tests

`ModuleHostTests` loads two real assemblies built from `tests/hotmodule-fixtures` (version 1 and a
different version 2 of the same module, built from the same source with a symbol) into the real
host and checks: the handlers come from a collectible context; unload removes exactly what the module
added; reload swaps old for new, including removing what the new version dropped; a bad reload, a
clash with a handler the module does not own, a command that shadows an existing root, a missing or
non-managed file, an assembly named wrongly, a junction, a name that is a path, and a commit the world
thread never reaches all change nothing; readers never see a window where a handler present in both
versions is missing during twelve reloads; every decision is audited; and the unload is collected.
`ModuleScriptSwapTests` loads `tests/hotmodule-fixtures/HotScriptsV1` / `V2` (one spell script, one instance script, one EventAI
event and one action handler) into a host whose seams use real spell script, instance script and EventAI registries and checks:
load makes all of them active from the module's collectible context; reload replaces each with version 2 and version 1 is
collected; unload removes them and the module is collected; a clash with a built-in spell script, instance script or EventAI
event handler rejects the whole module with every registry unchanged; an instance still holding a replaced instance script is
reported as retired code and keeps the old version loaded; a server without the registries or without game seams refuses the
module. It also checks that the unloaded module's load context is collected, through a `WeakReference` and a GC loop. A real
EventAI creature bound to the module's event handler is counted as retired code after unload and keeps the context alive until it
respawns, after which the context is collected. An instance still running an unloaded script is counted and keeps the context alive.
No discovery scan of the test assembly finds the built-in clash handler: the test builds that registry with `EventAiRegistry.FromHandlers`.
`ModuleRollbackTests` injects a fault (`ModuleHost.FaultInjection`) after each slot, on a replace of each of three fixture modules
(HotFixture: opcodes and commands; HotGame: creature AI and hooks; HotScripts: spell, instance and EventAI scripts). In every case
version 1 is restored with its registrations live, and both old contexts are collected. The tests' world thread (`ThreadedWorld`) keeps
the last command it ran reachable until it runs the next, as a world thread's frame may, so "collected" also proves that no queued commit
closure reaches a retired version (each commit is posted by `ModuleHost.CommitAsync`, whose closure holds only the host and the swap).
Before 2026-10-18 the test thread dropped it or not depending on the JIT tier its loop ran at, and the restore's closure did reach the
replaced version: the tests failed when the class ran early in the assembly and passed when it ran late. The class also covers a restore that fails
too (fully unloaded), a first load and an unload that fail, a throwing `ScriptsChanged` handler, and a registry that went away
(removals succeed, additions are rejected). `ModuleEventAiRespawnTests` covers the EventAI respawn batches: the cap per tick, combat
deferral, and that unaffected creatures are not touched. RED proofs: each seam was broken in turn and the tests above failed. The
seams were a rollback that does nothing, a `ScriptsChanged` failure that is ignored, a vanished registry that refuses removals, a
retired-creature count that never counts, an `InstanceScriptRegistry` that lets a module remove or override a built-in script, an
`EventAiRegistry` that lets a module remove or override a built-in handler, a discoverable clash handler, a replace that skips the
restore, a respawn sweep that ignores the per-tick cap, and one that respawns creatures in combat. Each was then restored and the tests were green again. Not covered by a test: the
production wiring in `AddHotModules` (read, not run).
`ModuleAllowlistTests` covers the allowlist logic (empty setting, case, comments and labels, unlisted hash, missing, empty, malformed, oversize and directory files, edit without restart) and `ModuleHostTests` proves a refusal changes nothing and is audited and that approving a new build lets the reload through. `ModuleLaneConfigTests` proves it is off by default (no service registered) and fails closed (including: a Production opt-in needs an allowlist). Not covered
by a test: the `.hotmodule` handlers themselves (they need a logged-in session) and a real `WorldRuntime`
tick loop; the swap is exercised through the same `IHotCodeWorld` seam the registry refresh tests use.

## Feature lifecycle: detach and re-attach a world feature (`.feature`)

Tier 2a, part 1 of [Hot reload tiers](../design/hot-reload-tiers.md#42-lifecycle-contract). The seams a reloadable pack
needs to leave and rejoin a running world. The six playerbot features are detachable (Tier 2a part 2, below); every other
feature is pinned. Nothing detaches unless an Administrator runs `.feature detach|attach`, so on a default server the
features attach once at start and stop at shutdown as before.

* **Contracts assembly** `src/ArcaneCore.World.Contracts` (default load context, `ContractsVersion.Current = 2` since the map feed's `IBotRouteSource`; 1 before it):
  `IWorldFeature` (moved here, namespace unchanged), `IDetachableWorldFeature` with `DetachKind` (`Pinned`, `Stateless`,
  `Handover`), `QuiesceAsync`, `Detach(FeatureHandover)` and `AttachFrom(world, handover?, subscriptions)`;
  `FeatureHandover` (named values of core or contract types; a type from a collectible assembly is refused);
  `FeatureSubscriptions`, the bag every subscription of a detachable feature goes through (world events,
  `SpellSystem` observers, any `IDisposable` registration); `IWorldLifecycleHooks` and `ITeleportWaiverRegistry`.
* **`FeatureHost`** (`src/ArcaneCore.World/Features`) attaches every feature at start (a detachable one through
  `AttachFrom` with a bag the host owns) and detaches, attaches or cycles one in a running world: quiesce off the world
  thread, then commit on the world thread at a tick boundary. All or nothing: a `Detach` or unsubscribe that throws is
  rolled back in the same tick (bag disposed, feature re-attached from the same handover); an `AttachFrom` that throws has
  its subscriptions undone and the feature stays detached with its handover; a failed quiesce detaches nothing (the
  feature is detached and re-attached in one tick to restart its work); a commit the world thread does not pick up leaves
  the feature quiesced but attached. Every operation is logged, written to the hot code audit file (`feature` lines) and
  reported as a hot swap in the new `Feature` lane.
* **`.feature list | detach <name> [reason] | attach <name> [reason]`**, Administrator, added at start only when
  `World:HotCode:Modules:Enabled` is set (the `.hotmodule` switch). The name is the feature's type name.
* `SpellSystem.UnregisterObserver`, `WorldLifecycleHooks` (named `AfterWorldStart` / `BeforeWorldStop` hooks) and
  `TeleportWaiverRegistry` (itself a pinned feature and `IAreaTriggerWaiver`, so the teleport handler consults registered
  waivers without a change of its own; with none registered it waives nothing).

### Detaching the playerbot features (Tier 2a part 2)

Built on `claude/hr2-bots` ([design, section 4.5](../design/hot-reload-tiers.md)). `ManagedPlayerbotFeature`,
`PlayerbotPopulationFeature`, `PlayerbotGroupCoordinator`, `PlayerbotBattlegroundCoordinator`, `PlayerbotSocialFeature`
and `PlayerbotQaFeature` detach and attach again with bots online:

* The logged-in bots belong to the core's `ManagedBotRegistry` (row, service scope, session), so `.feature detach
  ManagedPlayerbotFeature` leaves every bot in the world, standing idle: no brain runs, operator commands answer
  `playerbots-detaching`, and the bot sessions do the full client packet work (the lean path is the pack's policy).
  `.feature attach ManagedPlayerbotFeature` builds a new brain and party AI for each of them, with their set-asides,
  homes, fault quarantine and scripted controllers handed over.
* The group coordinator finishes its groups on detach, the battleground coordinator takes its bots out of their matches,
  the population stops a rush in progress, and bot QA saves its findings and reloads them on attach.
* A shutdown while the bot feature is detached closes and flushes the bots' sessions and leaves their rows running, so
  the next start restores them.
* The world host runs the lifecycle hooks (`playerbots.restore` after start, `playerbots.shutdown` before stop,
  `managed-bots.orphans` after it), and the core reaches the pack only through the registry's seams.

### The Playerbots pack as its own assembly (Tier 2b)

Since tier 2b ([design, section 4.6](../design/hot-reload-tiers.md)) the bot code is its own project,
`src/ArcaneCore.Playerbots` (assembly `ArcaneCore.Playerbots`, namespaces unchanged: `ArcaneCore.World.Playerbots.*`). It
references the core; the core (`ArcaneCore.World`) and the contracts (`ArcaneCore.World.Contracts`) never reference it. The
playerbot options (`PlayerbotOptions` and its section classes) live in the contracts assembly, so `.reload config` and
`IOptions<PlayerbotOptions>` are keyed by a type outside the pack.

* **How the daemon finds it.** `ArcaneCore.World` is also the executable, so it cannot reference its own pack. The build copies
  `ArcaneCore.Playerbots.dll` next to `ArcaneCore.World.dll` (the pack project's `CopyPackBesideWorldHost` target; the Docker
  image publishes it beside the world too), and `Program.cs` registers it with `WorldCodeAssemblies.RegisterBuiltIn` before
  anything is discovered. A world directory without the pack refuses to start (exit 78, naming the missing file). Build the
  solution or `src/ArcaneCore.Playerbots`; building `ArcaneCore.World` alone does not refresh the copy.
* **Discovery.** Every startup discovery of the world (features, `IWorldServiceModule`s, opcode handler groups, command groups and
  extensions, script modules, content reloadables, config checks, the wiring audit) reads `WorldCodeAssemblies.Startup`: World
  plus the registered built-in packs, ordered by type name across both, so the features attach, stop and register in the same
  order as before the move. In-process hosts (the test suites, the mock client) reference the pack project and register it the
  same way (`BuiltInPacks`, `MockWorldPacks`).
* **Still the default context.** The pack never unloads; swapping it is tier 2c. `.feature detach|attach` works as above.
* **The boundary, both ways.** `PlayerbotCoreBoundaryTests` checks that the core and the contracts do not reference the pack
  (the 4-entry allowlist of tier 2a is now empty), and pins the core internals the pack still uses (World and the contracts
  grant it `InternalsVisibleTo`) to an explicit list that may only shrink: the managed-bot session members of `WorldSession`, a
  few battleground, GM boost, teleport and movement helpers, and the internal helpers of the options.
* **Dev runner and hot runner.** `scripts/dev-runner.ps1` and `scripts/hot-runner.ps1|.sh` build the pack before starting;
  `dotnet watch` watches the world project only, so an edit to the pack needs a runner restart.
* **Keep the pack and the world from the same build.** The pack uses world internals (`InternalsVisibleTo`), so a world rebuilt
  alone (`dotnet build` or `dotnet run` of `src/ArcaneCore.World`, or a restart by `dotnet watch`) next to an older pack can fail
  with `MissingMethodException` or `TypeLoadException` when a tick first calls the changed member, not at load. After changing
  world code, build the solution or the pack project, in the configuration you run.
* **`dotnet run --project src/ArcaneCore.World`** runs only after the pack has been built in the same configuration (the solution
  build or `dotnet build src/ArcaneCore.Playerbots -c <Configuration>`); without it the world exits 78.

Not done yet (next lanes): the collectible load and swap of the pack (2c), the internals list turned into public seams or
contracts, the architecture test that forbids a bare `+=` from pack code, a `ContractsVersion` check of a pack at load, and
handover values of contract types only (today they are World or pack types, accepted because neither is collectible).

Tests (bot features): `PlayerbotFeatureDetachTests` (4) and `ManagedBotRegistryTests` (2) in
`tests/ArcaneCore.World.Tests/Playerbots`; `PlayerbotCoreBoundaryTests` (3) and `WorldCodeAssembliesTests` (5) in
`tests/ArcaneCore.World.Tests/Architecture`. The bot tests stay in `ArcaneCore.World.Tests` (they share its fixtures and
fakes), which references the pack project.

Tests (lifecycle seams): `FeatureLifecycleTests` (14) and `FeatureSeamTests` (8) in `tests/ArcaneCore.World.Tests/FeatureLifecycle`, with
RED runs: bag disposal disabled (13 fail), `UnregisterObserver` not removing (11 fail), rollback, pinned refusal and
waiver union disabled together (exactly their 5 tests fail), attach-failure undo disabled (its 1 test fails).

## Proving the claims yourself

`tools/hotcode-spike/run-spike.ps1` is a throwaway process plus a driver. It runs five
scenarios and prints `RESULT <name> PASS|FAIL`, ending in `SPIKE SUMMARY`:

| Scenario | Proves |
|---|---|
| `plain-run` | Without watch, `IsSupported=False` and the gate would not fire. |
| `debug-body-edit` | Same pid, new value, `IsSupported=True`, and the assembly's `[MetadataUpdateHandler]` was called. |
| `debug-new-type` | A class added by an edit appears in `Assembly.GetTypes()` of the same process and the handler is called again (what the registry refresh relies on). |
| `debug-sig-edit` | A signature edit restarts the process (new pid). |
| `release-body-edit` | Release is never hot-applied (new pid). |

Exit code 77 means "skipped, no SDK": a skip proves nothing.

## Not built (limits that are deliberately absent)

Be precise about what this page does not offer:

The plan for removing these limits (multi-assembly discovery, swappable spell, instance and EventAI scripts, reloadable packs, a gateway with blue/green handoff) is [Hot reload tiers](../design/hot-reload-tiers.md). The gateway passthrough (phase 3a) is built: [Gateway passthrough](gateway.md).

* **Hot-replacing the server's own code on a Release build.** The module lane loads *extension
  assemblies*; the server assembly itself (a built-in handler, formula or feature) changes live only
  through the development runner (Debug, SDK), otherwise by restart. A module cannot replace a
  built-in handler or command (it is rejected), by design.
* **Module contributions beyond opcode handler groups, chat command groups, creature AI, script hooks, spell scripts,
  instance scripts and EventAI handlers.** Map updaters, `IWorldFeature`s (the detach/attach seams exist, see the feature
  lifecycle section, but no module or pack can contribute one yet), `IDataModule`s (tables), DI services, spell handler
  modules and aura registrations are not module extension points yet. A module that needs one needs a new, reviewed seam in the
  server first. (`CodeAssemblies` can already discover those kinds over built-ins plus pack assemblies, but nothing swaps their live
  registries.)
* **Moving a loaded instance or an existing EventAI creature onto a replaced script.** An instance keeps the instance script it was
  created with until it unloads, and a creature keeps its bound EventAI event handlers until it respawns (the module lane forces that respawn for database creatures, losing their state): their state belongs to
  the old code, and nothing migrates it.
* **Configured startup packs.** The world's startup set holds exactly one built-in pack, Playerbots, registered in code by
  `Program.cs` (tier 2b, above); no configuration adds another, and the spell script, instance script and EventAI startup
  discovery still runs over Game alone. Other extra code arrives only through the module lane.
* **Automatic reload on file change** (a `FileSystemWatcher`). A reload is an explicit, audited
  Administrator command.
* **A module that outlives a restart on its own.** `LoadOnStart` names modules to load again at start;
  nothing else is persisted.
* **Guaranteed unload.** The host releases a module and the runtime frees it once nothing references
  it; it cannot stop a thread, timer or event subscription the module created (see below).
* A home-grown in-process delta endpoint. Rejected: it would re-implement part of the compiler
  service for a gain (tick-atomic apply) a development runner does not need.
