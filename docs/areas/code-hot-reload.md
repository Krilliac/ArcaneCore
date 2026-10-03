# Code hot reload

Change C# while the world daemon is running and have the change take effect without a restart.
Two independent lanes, both **off by default** and neither part of the retail server. Neither has
anything to do with the *content* reload lane (database rows); this page is only about code.

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

## Configuration (`World:HotCode`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Opt in. Accepted only when the host environment is Development or Staging. |
| `AuditLogPath` | empty | Append-only file, one tab-separated line per decision (`start-allowed`, `start-refused`). Not tamper-proof: it is written by the same account the server runs as. |
| `MaxConsecutiveFaults` | `50` | Fault breaker, effective only while `Enabled`: a map updater that throws in this many consecutive ticks is skipped (and logged once at error level) until the next applied code edit, or a restart, instead of failing every tick. `0` disables it. An explicit `World:MaxConsecutiveUpdaterFaults` wins. The vanilla default of that setting is `0`: unchanged behavior. |

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

* any signature change, return type change, rename, delete, base type or interface change,
  generic arity change (verified: a signature edit made the process restart with a new pid);
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
constructor implementing `IOpcodeHandlerGroup` and/or `ICommandGroup`: the same two interfaces the
server's own features use, so a module is written like a built-in feature. Nothing else in the assembly
is looked at. Build it against the server's `ArcaneCore.World` (and `ArcaneCore.Protocol`,
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
| `LoadOnStart` | `[]` | Module names loaded once the world is running (a failure is logged, not fatal). |

`World:HotCode:AuditLogPath` is shared with the other lane. Startup logs a warning when modules are enabled.

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
   `module-rejected`) with the dll's SHA-256, and logged (a rejection at error level).

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
no signature check: do not point this at a directory other people can write to.

### Tests

`ModuleHostTests` loads two real assemblies built from `tests/hotmodule-fixtures` (version 1 and a
different version 2 of the same module, built from the same source with a symbol) into the real
host and checks: the handlers come from a collectible context; unload removes exactly what the module
added; reload swaps old for new, including removing what the new version dropped; a bad reload, a
clash with a handler the module does not own, a command that shadows an existing root, a missing or
non-managed file, an assembly named wrongly, a junction, a name that is a path, and a commit the world
thread never reaches all change nothing; readers never see a window where a handler present in both
versions is missing during twelve reloads; every decision is audited; and the unload is collected.
`ModuleLaneConfigTests` proves it is off by default (no service registered) and fails closed. Not covered
by a test: the `.hotmodule` handlers themselves (they need a logged-in session) and a real `WorldRuntime`
tick loop; the swap is exercised through the same `IHotCodeWorld` seam the registry refresh tests use.

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

* **Hot-replacing the server's own code on a Release build.** The module lane loads *extension
  assemblies*; the server assembly itself (a built-in handler, formula or feature) changes live only
  through the development runner (Debug, SDK), otherwise by restart. A module cannot replace a
  built-in handler or command (it is rejected), by design.
* **Module contributions beyond opcode handler groups and chat command groups.** Map updaters,
  `IWorldFeature`s, `IDataModule`s (tables), DI services and spell or aura registrations are not
  module extension points yet. A module that needs one needs a new, reviewed seam in the server first.
* **Automatic reload on file change** (a `FileSystemWatcher`). A reload is an explicit, audited
  Administrator command.
* **A module that outlives a restart on its own.** `LoadOnStart` names modules to load again at start;
  nothing else is persisted.
* **Guaranteed unload.** The host releases a module and the runtime frees it once nothing references
  it; it cannot stop a thread, timer or event subscription the module created (see below).
* A home-grown in-process delta endpoint. Rejected: it would re-implement part of the compiler
  service for a gain (tick-atomic apply) a development runner does not need.
