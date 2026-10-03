# Code hot reload (development runner)

Edit C# while the world daemon is running and have the edit take effect without a restart.
This is a **development / staging tool**, off by default, and it is not part of the retail
server. It has nothing to do with the *content* reload lane (database rows); this page is only
about code.

Vanilla fidelity: with `World:HotCode:Enabled=false` (the default) and a normal launch, the
server behaves exactly as before. Nothing is loaded, nothing is patched and no command is added.

## What it is, in one paragraph

`scripts/hot-runner.ps1` (or `.sh`) starts the daemon under `dotnet watch` in a **Debug** build.
`dotnet watch` hands every saved edit to the .NET runtime's metadata-update support
(`System.Reflection.Metadata.MetadataUpdater`), which swaps method bodies inside the live
process. The server itself contributes only the safety gate (below). The runtime and the SDK do
the patching; ArcaneCore does not implement a code loader.

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

## Configuration (`World:HotCode`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Opt in. Accepted only when the host environment is Development or Staging. |
| `AuditLogPath` | empty | Append-only file, one tab-separated line per decision (`start-allowed`, `start-refused`). Not tamper-proof: it is written by the same account the server runs as. |

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
* Registries built once at startup do not learn about *added* handler or command types on their
  own. See "Refreshing registries" if that slice is present in your build.

## Security model

Code edited into a running server runs with the server's full trust. Therefore:

* Off by default; the launch gate refuses a hot-reload-capable process that did not opt in, and
  refuses an opt-in outside Development / Staging.
* The only trust boundary is who can write the source tree and who can start the process. Anyone
  who can edit a `.cs` file in a watched checkout, or reach the watch agent on the machine, can
  run code as the server. Use it on your own machine.
* The audit log records start decisions; a compromised process can rewrite it.
* Nothing in this feature adds an in-game or network command that loads code.

## Proving the claims yourself

`tools/hotcode-spike/run-spike.ps1` is a throwaway process plus a driver. It runs four
scenarios and prints `RESULT <name> PASS|FAIL`, ending in `SPIKE SUMMARY`:

| Scenario | Proves |
|---|---|
| `plain-run` | Without watch, `IsSupported=False` and the gate would not fire. |
| `debug-body-edit` | Same pid, new value, `IsSupported=True`. |
| `debug-sig-edit` | A signature edit restarts the process (new pid). |
| `release-body-edit` | Release is never hot-applied (new pid). |

Exit code 77 means "skipped, no SDK": a skip proves nothing.

## Not built (design scope that is deliberately absent)

Be precise about what this page does not offer:

* **Loading new code into a deployed server (no SDK, Release).** The design for this is a
  collectible `AssemblyLoadContext` module host with proven unload and a tick-boundary swap. It
  is **not implemented**: the plugin-host rows in the roadmap say it arrives with its first real
  consumer, and no consumer has been named. Until then nothing can be hot loaded into a
  Release / deployed instance; existing code there changes only by restart.
* A home-grown in-process delta endpoint. Rejected: it would re-implement part of the compiler
  service for a gain (tick-atomic apply) a development runner does not need.
* Any claim that unload is safe: no module host exists, so there is nothing to unload.
