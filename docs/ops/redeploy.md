# Redeploy prefers hot reload

`tools/ops/live/redeploy.ps1` redeploys the local live server from a built worktree. Before it touches anything it plans: it compares
the worktree with what the live World runs and applies the change the cheapest safe way. A hard stop/start happens only when the change
needs one ([Hot reload tiers, section 9.1](../design/hot-reload-tiers.md#91-redeploy-prefers-hot-reload-built-on-claudehr3-redeploy-hot)).

```powershell
pwsh -NoProfile -File tools/ops/live/redeploy.ps1 -Plan              # print the plan, touch nothing (same as -WhatIf)
pwsh -NoProfile -File tools/ops/live/redeploy.ps1                    # apply it
pwsh -NoProfile -File tools/ops/live/redeploy.ps1 -ForceRestart      # the hard restart, whatever the plan says
pwsh -NoProfile -File tools/ops/live/redeploy.ps1 -ContentTables spell_template,npc_vendor   # rows changed in the live world database
pwsh -NoProfile -File tools/ops/live/redeploy.ps1 -Plan -Json        # the plan as JSON as well
```

## The plan

| Action | When | What happens | Exit |
|---|---|---|---|
| `None` | Nothing that reaches the daemons changed (documentation, tests, tools, a project the daemons are not built from, such as the gateway) | Nothing | 0 |
| `Hot` | Only live configuration keys, content tables `.reload` supports and allowlisted hot modules changed | Applied through the World's control pipe, in this order: `.reload config`, `.reload <table>` per table, `.hotmodule load` or `reload` per module. No restart | 0 |
| `Restart` | Anything else, or no baseline | The hard path: Ctrl-C stop of World then Realm, `bin` snapshot, optional `world.db` swap, copy of both Debug builds and of changed approved modules, start | 0 |
| `Blocked` | A changed hot module is not built, is older than its changed source, or its SHA-256 is not on the module allowlist | Nothing; fix it (build, approve with `scripts/hotmodule-hash.ps1`) or pass `-ForceRestart` | 2 |

What decides each class:

- **Code.** The changed paths are `git diff --name-only <deployed SHA>` (committed and uncommitted changes), the untracked files, and
  the paths that were uncommitted when the deployed build was recorded (their deployed content is in no commit). Each path is classified:
  a hot module project's folder is `module:<Name>`; a folder of a project in the ProjectReference closure of `ArcaneCore.World` and
  `ArcaneCore.Realm` is `core`; `docs/`, `tests/`, `tools/`, `scripts/`, `.github/`, `apps/`, any `.md` file outside a deployed project,
  and a few root files (`.gitignore`, `.gitattributes`, `LICENSE`, `docker-compose.yml`) are `none`; a project under `src/` outside the
  closure is `not-deployed`; anything else (root build files such as `Directory.Build.props` or `global.json`, `sql/updates`, unknown
  paths) is `core`. One `core` path means a restart.
- **Configuration.** The profile's file configuration (`appsettings.json`, `appsettings.Development.json`, then `world.conf` from the
  profile or `ARCANECORE_CONFIG`, later files winning) is flattened and fingerprinted (a SHA-256 per key; no value is stored). Each key
  that differs from the deploy record's fingerprint is looked up in the configuration catalog
  (`src/ArcaneCore.Kernel/Configuration/Conf/conf-catalog.json`): only a key whose reload class is `live` and that only the World reads is
  applied hot (`.reload config`). `restart` and `start` keys, keys the Realm reads, host framework keys (`Logging:LogLevel:*`,
  `HostOptions:*`) and unknown keys mean a restart.
- **Content.** The worktree does not hold content rows: name the tables you changed in the live world database with `-ContentTables`.
  A name `.reload` has (the list is pinned to the World's reloadables by a test) becomes a `.reload <name>` step; any other name means a
  restart. `-WorldDbSwap` replaces the database file, which needs the World stopped: always a restart.
- **Hot modules.** A module project is a tracked `.csproj` whose assembly name has a folder in the live module directory
  (`World:HotCode:Modules:Directory`). Its build is `<project>\bin\Debug\<tfm>\<Name>.dll`; it must be newer than the changed source and
  its SHA-256 must be on the allowlist (`World:HotCode:Modules:Allowlist`). The planner never adds a hash: approving a build stays the
  operator's step.
- **The hot path must exist.** The World must be running, `World:Control:Pipe:Enabled` must be on (the planner talks to the World only
  through the control pipe), `HotReload:Commands` on for `.reload` steps and `World:HotCode:Modules:Enabled` on for module steps.
  Otherwise the plan is a restart and says why.

## Applying a hot plan

Each step goes through the World's control pipe ([server console](../areas/server-console.md#control-pipe)). The client checks that the
process serving the pipe is the live World's PID before it sends anything.

- `.reload <name>`: the step waits for the reload's result line, then requires `.reload status` to say `Applied`.
- A module: the dll (and pdb) is copied into `<Directory>\<Name>\` (the previous dll is kept until the step succeeds) and its hash checked;
  `.hotmodule reload` if the module is loaded, else `.hotmodule load`; the step waits for `Module ... done` and requires `.hotmodule list`
  to show the new hash. On any failure the previous dll is put back, so the directory matches what the World still runs.

Outcomes:

- **Applied**: every step took effect; the deploy record is updated (`method: hot`).
- **Unavailable** (no pipe, a command missing, `Busy`, no answer within the step's timeout): the redeploy falls back to the hard
  restart, which deploys everything.
- **Rejected** (the World refused the change: a reload `Rejected` or `Failed ... rolled back`, a module rejected): the redeploy stops
  with exit 3. The World keeps running what it ran (the reload and module lanes roll back), steps applied before the rejection are listed,
  and the deploy record is left as it was, so the next run plans the same steps again. A restart is deliberately not the fallback here:
  a value the reload rejects would stop the daemon at start, and a module it refuses would be refused at start too. `-ForceRestart` when
  you want it anyway.

## The deploy record

`<LiveHome>\bin\arcanecore-deploy.json`, written after every successful restart or hot redeploy (the `bin` snapshot of a restart
therefore carries the record of the build it holds):

| Field | Meaning |
|---|---|
| `sha` | `HEAD` of the worktree that was deployed |
| `dirtyPaths` | The paths that were uncommitted (or untracked) at that time; they always count as changed at the next plan |
| `src` | The worktree path |
| `method` | `restart` or `hot` |
| `deployedUtc`, `previousSha` | When, and the record it replaced |
| `config` | The configuration fingerprint (SHA-256 per key; no values) |
| `modules` | Module name to the SHA-256 deployed |

No record (the first redeploy with the planner), or a SHA the repository does not have, means a restart, after which the record exists.

## Limits

- The restart is still whole-server (World and Realm), also for a Realm-only change; there is no Realm-only restart.
- The core build is assumed to match the worktree (the restart path checks only that the executables exist); a module build is checked
  against its changed source by time stamp.
- A `.md` file inside a deployed project's folder counts as `core` (conservative). A project that compiles files from outside its own folder
  is not detected; such a file is `core` only if it lies in some deployed project's folder or outside the `none` prefixes.
- `Logging:LogLevel:*` changes would apply by themselves (the host reloads `appsettings.json`), but are planned as a restart.
- Content is declared, not detected: the planner cannot see rows changed in a database.
- Nothing here was run against the live server by this change; the pieces are covered by tests (see below).

## Tests

`tests/ArcaneCore.World.Tests/Ops/RedeployPlanTests.cs`: 34 planner fixtures (changed-file sets, configuration keys, content tables, module
builds, capabilities to plan), the reloadable list pinned to the World's, the daemon project closure of the real repository, the changed
paths of a scratch git repository, the configuration fingerprint, an end-to-end plan over a scratch repository, profile and deploy record,
and the client half of a hot step against a real World's control pipe. The pipe itself: `tests/ArcaneCore.World.Tests/Ops/Control/WorldControlPipeTests.cs`.
